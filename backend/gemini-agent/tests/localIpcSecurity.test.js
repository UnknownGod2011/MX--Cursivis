import assert from "node:assert/strict";
import { once } from "node:events";
import { spawn } from "node:child_process";
import { createHmac, randomBytes } from "node:crypto";
import http from "node:http";
import { createServer } from "node:net";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { WebSocket, WebSocketServer } from "ws";

async function startService(t, relativePath, portVariable, extra = {}) {
  const listener = createServer();
  listener.listen(0, "127.0.0.1");
  await once(listener, "listening");
  const port = listener.address().port;
  await new Promise((resolve) => listener.close(resolve));

  const key = randomBytes(32);
  const relayKey = randomBytes(32);
  const child = spawn(process.execPath, [fileURLToPath(new URL(relativePath, import.meta.url))], {
    env: {
      ...process.env,
      GOOGLE_API_KEY: "",
      GOOGLE_API_KEYS: "",
      GEMINI_API_KEY: "",
      GEMINI_API_KEYS: "",
      [portVariable]: String(port),
      CURSIVIS_LOCAL_IPC_KEY: key.toString("hex"),
      CURSIVIS_LOCAL_IPC_RELAY_KEY: relayKey.toString("hex"),
      ...extra
    },
    stdio: "ignore",
    windowsHide: true
  });
  const exited = once(child, "exit");
  t.after(async () => {
    child.kill();
    await exited;
  });

  const service = { port, key, relayKey, authority: "127.0.0.1:" + port };
  const deadline = Date.now() + 15000;
  do {
    const response = await request(service, "GET", "/health").catch(() => null);
    if (response) {
      assert.equal(response.status, 401);
      return service;
    }
    assert.equal(child.exitCode, null, "local service must stay running during startup");
    await delay(50);
  } while (Date.now() < deadline);
  assert.fail("local service never started");
}

function signed(service, method, path, key = service.key, timestamp = Math.floor(Date.now() / 1000)) {
  const nonce = randomBytes(16).toString("hex");
  const signature = createHmac("sha256", key)
    .update(["request", method, service.authority, path, timestamp, nonce].join("\n"))
    .digest("hex");
  return ["v1", timestamp, nonce, signature].join(".");
}

function request(service, method, path, { headers, body } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({
      host: "127.0.0.1",
      port: service.port,
      method,
      path,
      headers: { ...(body && { "Content-Type": "application/json" }), ...headers },
      timeout: 25000
    }, async (res) => {
      try {
        const chunks = [];
        for await (const chunk of res) {
          chunks.push(chunk);
        }
        const data = Buffer.concat(chunks).toString("utf8");
        resolve({ status: res.statusCode, headers: res.headers, body: data && JSON.parse(data) });
      } catch (error) {
        reject(error);
      }
    });
    req.on("timeout", () => req.destroy(new Error("Test request timed out.")));
    req.on("error", reject);
    req.end(body ? JSON.stringify(body) : undefined);
  });
}

function authenticated(service, method, path, body, key = service.key) {
  return request(service, method, path, { body, headers: { "X-Cursivis-IPC": signed(service, method, path, key) } });
}

test("AI HTTP endpoints authenticate before exposing or mutating runtime state", { timeout: 30000 }, async (t) => {
  const service = await startService(t, "../src/server.js", "PORT");
  for (const [method, path] of [
    ["POST", "/analyze"], ["POST", "/api/intent"], ["POST", "/transcribe"],
    ["POST", "/plan-browser-action"], ["POST", "/refine-browser-action"],
    ["POST", "/runtime/api-key"], ["POST", "/runtime/ai-provider"],
    ["POST", "/runtime/local-llm/pull-stream"], ["GET", "/runtime/ai-provider"]
  ]) {
    assert.equal((await request(service, method, path)).status, 401, path);
  }
  const path = "/health";
  const proof = signed(service, "GET", path);
  for (const headers of [
    { Origin: "https://untrusted.example" },
    { Origin: "null" },
    { "Sec-Fetch-Site": "same-origin" },
    { Host: "rebound.example:" + service.port },
    { "Access-Control-Request-Method": "POST" }
  ]) {
    const result = await request(service, "GET", path, { headers: { ...headers, "X-Cursivis-IPC": proof } });
    assert.equal(result.status, 403);
    assert.equal(result.headers["access-control-allow-origin"], undefined);
    assert.equal(result.headers["x-cursivis-ipc-ack"], undefined);
  }
  const trusted = await request(service, "GET", path, { headers: { "X-Cursivis-IPC": proof } });
  assert.equal(trusted.status, 200);
  assert.equal(trusted.body.ok, true);
  assert.equal(trusted.headers["x-cursivis-ipc-ack"], createHmac("sha256", service.key).update("response\n" + proof).digest("hex"));
  assert.equal((await request(service, "GET", path, { headers: { "X-Cursivis-IPC": proof } })).status, 401);
  assert.equal((await request(service, "GET", path, { headers: {
    "X-Cursivis-IPC": signed(service, "GET", path, service.key, Math.floor(Date.now() / 1000) - 60)
  } })).status, 401);
  assert.equal((await authenticated(service, "GET", path, undefined, service.relayKey)).status, 401);
  assert.equal((await authenticated(service, "HEAD", "/runtime/api-key")).status, 204);
});

test("streaming voice rejects unauthenticated upgrades before opening a Gemini session", { timeout: 30000 }, async (t) => {
  const service = await startService(t, "../src/server.js", "PORT");
  const endpoint = "ws://" + service.authority + "/live";
  async function rejected(headers, expected) {
    const socket = new WebSocket(endpoint, { headers, handshakeTimeout: 5000 });
    await new Promise((resolve, reject) => {
      socket.once("error", reject);
      socket.once("open", () => reject(new Error("Unauthorized upgrade succeeded.")));
      socket.once("unexpected-response", (_request, response) => {
        response.resume();
        assert.equal(response.statusCode, expected);
        resolve();
      });
    });
  }
  await rejected({}, 401);
  await rejected({ Origin: "null", "X-Cursivis-IPC": signed(service, "GET", "/live") }, 403);
  await rejected({ "X-Cursivis-IPC": signed(service, "GET", "/live", service.relayKey) }, 401);
  const socket = new WebSocket(endpoint, {
    headers: { "X-Cursivis-IPC": signed(service, "GET", "/live") },
    handshakeTimeout: 5000
  });
  const [error] = await once(socket, "message");
  assert.equal(JSON.parse(String(error)).type, "error");
  socket.close();
});

test("authenticated streaming voice waits for Gemini setup and forwards SDK-compatible audio", { timeout: 30000 }, async (t) => {
  const upstream = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await once(upstream, "listening");
  t.after(() => {
    for (const socket of upstream.clients) socket.terminate();
    upstream.close();
  });
  const service = await startService(t, "../src/server.js", "PORT", {
    GOOGLE_API_KEY: "AQ." + randomBytes(32).toString("hex"),
    GOOGLE_GEMINI_BASE_URL: "http://127.0.0.1:" + upstream.address().port
  });
  const upstreamConnection = once(upstream, "connection");
  const endpoint = "ws://" + service.authority + "/live";
  const socket = new WebSocket(endpoint, {
    headers: { "X-Cursivis-IPC": signed(service, "GET", "/live") },
    handshakeTimeout: 5000
  });
  t.after(() => socket.terminate());
  const ready = once(socket, "message");
  const [gemini] = await upstreamConnection;
  const [setupBuffer] = await once(gemini, "message");
  assert.ok(JSON.parse(String(setupBuffer)).setup);
  gemini.send(JSON.stringify({ setupComplete: {} }));
  const [readyBuffer] = await ready;
  assert.equal(JSON.parse(String(readyBuffer)).type, "live_open");
  const audio = { data: Buffer.from([0, 1, 2, 3]).toString("base64"), mimeType: "audio/pcm;rate=16000" };
  const sent = once(gemini, "message");
  socket.send(JSON.stringify({ type: "audio_chunk", dataBase64: audio.data, mimeType: audio.mimeType }));
  const [sentBuffer] = await sent;
  assert.deepEqual(JSON.parse(String(sentBuffer)).realtimeInput.audio, audio);
});

test("browser services deny web callers and separate native relay and desktop authority", { timeout: 30000 }, async (t) => {
  const managed = await startService(t, "../../../desktop/browser-action-agent/src/server.js", "CURSIVIS_BROWSER_AGENT_PORT");
  assert.equal((await request(managed, "GET", "/page-context")).status, 401);
  assert.equal((await request(managed, "POST", "/execute-plan", {
    headers: { Origin: "https://untrusted.example" }
  })).status, 403);
  assert.equal((await authenticated(managed, "GET", "/health")).body.browserReady, false);

  const bridge = await startService(t, "../../../desktop/browser-native-host/src/host.js", "CURSIVIS_EXTENSION_BRIDGE_PORT");
  for (const path of ["/extension/pull", "/extension/response", "/extension/disconnect", "/execute-plan", "/active-tab-context"]) {
    assert.equal((await request(bridge, "POST", path)).status, 401, path);
    assert.equal((await request(bridge, "POST", path, { headers: { Origin: "chrome-extension://test" } })).status, 403, path);
  }
  assert.equal((await authenticated(bridge, "POST", "/extension/pull", {})).status, 401);
  assert.equal((await authenticated(bridge, "POST", "/execute-plan", {}, bridge.relayKey)).status, 401);
  const session = { sessionId: randomBytes(16).toString("hex"), extensionId: "a".repeat(32), browserName: "fixture" };
  const queued = authenticated(bridge, "POST", "/extension/pull", session, bridge.relayKey);
  for (let i = 0; i < 50; i++) {
    if ((await authenticated(bridge, "GET", "/health")).body.extensionConnected) break;
    await delay(25);
  }
  const context = authenticated(bridge, "POST", "/active-tab-context", {});
  const task = (await queued).body;
  assert.equal(task.action, "get_active_tab_context");
  assert.equal((await authenticated(bridge, "POST", "/extension/response", {
    sessionId: randomBytes(16).toString("hex"), requestId: task.requestId, ok: true
  }, bridge.relayKey)).status, 404);
  assert.equal((await authenticated(bridge, "POST", "/extension/response", {
    sessionId: session.sessionId, requestId: task.requestId, ok: true, payload: { title: "Fixture" }
  }, bridge.relayKey)).status, 200);
  assert.equal((await context).body.title, "Fixture");
  await authenticated(bridge, "POST", "/extension/disconnect", session, bridge.relayKey);
  assert.equal((await authenticated(bridge, "GET", "/health")).body.extensionConnected, false);
});
