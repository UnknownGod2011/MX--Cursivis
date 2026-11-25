"use strict";

const http = require("node:http");
const { randomUUID } = require("node:crypto");
const { createAuthenticator, takeServiceKey } = require("../../../shared/local-ipc/local-service-auth.cjs");

const PORT = Number(process.env.CURSIVIS_EXTENSION_BRIDGE_PORT || 48830);
const REQUEST_TIMEOUT_MS = Number(process.env.CURSIVIS_EXTENSION_BRIDGE_TIMEOUT_MS || 90000);
const POLL_TIMEOUT_MS = 15000;
const STALE_AFTER_MS = 45000;
const MAX_BODY = 8 * 1024 * 1024;
const relayPaths = new Set(["/extension/pull", "/extension/response", "/extension/disconnect"]);
const desktop = createAuthenticator(takeServiceKey());
const relay = createAuthenticator(takeServiceKey("CURSIVIS_LOCAL_IPC_RELAY_KEY"));
const sessions = new Map();

const server = http.createServer({ requestTimeout: 30000 }, async (req, res) => {
  const auth = relayPaths.has(req.url) ? relay : desktop;
  if (!auth.authorizeHttp(req, res)) {
    return;
  }

  try {
    await routeRequest(req, res);
  } catch (error) {
    if (!res.destroyed && !res.headersSent) {
      if (error.status === 413) {
        res.setHeader("Connection", "close");
      }
      writeJson(res, error.status || 500, { ok: false, error: error.publicMessage || "The browser connection could not complete the request." });
    }
  }
});
server.listen(PORT, "127.0.0.1");

function problem(status, publicMessage) {
  return Object.assign(new Error(publicMessage), { status, publicMessage });
}

async function routeRequest(req, res) {
  pruneSessions();
  if (req.method === "GET" && req.url === "/health") {
    const session = activeSession();
    writeJson(res, 200, {
      ok: true,
      extensionConnected: Boolean(session),
      transport: session ? "native" : "none",
      browserName: session?.browserName || "unknown",
      extensionId: session?.extensionId || "",
      connectedAtUtc: session?.connectedAtUtc || null,
      lastSeenUtc: session ? new Date(session.lastSeen).toISOString() : null,
      lastError: null,
      capabilities: session?.capabilities || []
    });
    return;
  }

  if (req.method !== "POST") {
    return writeJson(res, 404, { ok: false, error: "Not found." });
  }

  switch (req.url) {
    case "/extension/pull":
      pollForRequest(await readJsonBody(req), res);
      break;
    case "/extension/response":
      finishRequest(await readJsonBody(req));
      writeJson(res, 200, { ok: true });
      break;
    case "/extension/disconnect": {
      const { sessionId } = await readJsonBody(req);
      const session = sessions.get(sessionId);
      if (session) {
        closeSession(session);
      }
      writeJson(res, 200, { ok: true });
      break;
    }
    case "/active-tab-context":
      writeJson(res, 200, await dispatchRequest("get_active_tab_context", {}, res));
      break;
    case "/execute-plan": {
      const { steps } = await readJsonBody(req);
      if (!Array.isArray(steps)) {
        throw problem(400, "A browser action plan requires a steps array.");
      }
      writeJson(res, 200, await dispatchRequest("execute_plan", { steps }, res));
      break;
    }
    default:
      writeJson(res, 404, { ok: false, error: "Not found." });
  }
}

function activeSession() {
  let active = null;
  for (const session of sessions.values()) {
    if (!active || session.lastSeen >= active.lastSeen) {
      active = session;
    }
  }
  return active;
}

function closeSession(session) {
  sessions.delete(session.id);
  completePoll(session, { ok: true, idle: true });
  for (const pending of session.pending.values()) {
    pending.fail(problem(503, "The browser extension disconnected. Open your browser and try the action again."));
  }
}

function pruneSessions() {
  const now = Date.now();
  for (const session of sessions.values()) {
    if (now - session.lastSeen > STALE_AFTER_MS) {
      closeSession(session);
    }
  }
}

function pollForRequest(body, res) {
  const { sessionId, extensionId } = body;
  if (typeof sessionId !== "string" || !/^[a-f0-9]{32}$/.test(sessionId) ||
      typeof extensionId !== "string" || !/^[a-p]{32}$/.test(extensionId)) {
    throw problem(400, "Invalid browser connection.");
  }

  let session = sessions.get(sessionId);
  if (!session) {
    if (sessions.size >= 16) {
      throw problem(503, "Too many browser connections.");
    }
    session = {
      id: sessionId,
      extensionId,
      browserName: typeof body.browserName === "string" ? body.browserName.slice(0, 24) : "unknown",
      capabilities: Array.isArray(body.capabilities)
        ? body.capabilities.filter((item) => typeof item === "string" && item.length <= 40).slice(0, 12)
        : [],
      connectedAtUtc: new Date().toISOString(),
      queue: [],
      pending: new Map(),
      poll: null
    };
    sessions.set(sessionId, session);
  }
  if (session.extensionId !== extensionId) {
    throw problem(403, "Invalid browser connection.");
  }
  session.lastSeen = Date.now();
  completePoll(session, { ok: true, idle: true });

  const timer = setTimeout(() => completePoll(session, { ok: true, idle: true }), POLL_TIMEOUT_MS);
  session.poll = { res, timer };
  res.once("close", () => {
    clearTimeout(timer);
    if (session.poll?.res === res) {
      session.poll = null;
    }
  });
  flushQueue(session);
}

function completePoll(session, payload) {
  const poll = session.poll;
  if (!poll) {
    return;
  }
  session.poll = null;
  clearTimeout(poll.timer);
  writeJson(poll.res, 200, payload);
}

function flushQueue(session) {
  if (session.poll && !session.poll.res.destroyed && !session.inFlight && session.queue.length) {
    const request = session.queue.shift();
    session.inFlight = request.requestId;
    completePoll(session, request);
  }
}

function dispatchRequest(action, payload, res) {
  const session = activeSession();
  if (!session) {
    throw problem(503, "No current browser extension session is connected.");
  }
  if (session.pending.size >= 16) {
    throw problem(429, "The browser connection is busy. Try again shortly.");
  }

  const requestId = randomUUID();
  return new Promise((resolve, reject) => {
    const complete = (error, response) => {
      clearTimeout(timer);
      res.off("close", onClose);
      session.pending.delete(requestId);
      session.queue = session.queue.filter((request) => request.requestId !== requestId);
      if (session.inFlight === requestId) {
        session.inFlight = null;
      }
      if (error) {
        reject(error);
      } else {
        resolve(response);
      }
      flushQueue(session);
    };
    const onClose = () => complete(problem(503, "The request was cancelled."));
    const timer = setTimeout(() => complete(problem(504, "The browser action timed out.")), REQUEST_TIMEOUT_MS);
    res.once("close", onClose);
    session.pending.set(requestId, { complete, fail: (error) => complete(error) });
    session.queue.push({ type: "request", requestId, action, payload });
    flushQueue(session);
  });
}

function finishRequest(body) {
  const session = sessions.get(body.sessionId);
  const pending = session?.pending.get(body.requestId);
  if (!pending || session.inFlight !== body.requestId) {
    throw problem(404, "The browser request is no longer pending.");
  }
  session.lastSeen = Date.now();
  if (body.ok === true) {
    pending.complete(null, body.payload || {});
  } else {
    pending.fail(problem(502, "The browser could not complete the action. Try it again from the active tab."));
  }
}

async function readJsonBody(req) {
  if (req.headers["content-type"]?.split(";", 1)[0].trim().toLowerCase() !== "application/json" ||
      req.headers["content-encoding"] !== undefined) {
    throw problem(415, "A JSON request is required.");
  }
  if (Number(req.headers["content-length"]) > MAX_BODY) {
    throw problem(413, "The browser request is too large.");
  }

  const chunks = [];
  let size = 0;
  for await (const chunk of req.iterator({ destroyOnReturn: false })) {
    size += chunk.length;
    if (size > MAX_BODY) {
      throw problem(413, "The browser request is too large.");
    }
    chunks.push(chunk);
  }
  try {
    const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    if (body && typeof body === "object" && !Array.isArray(body)) {
      return body;
    }
  } catch {
    // Keep parser errors and request content out of responses and logs.
  }
  throw problem(400, "Invalid JSON request body.");
}

function writeJson(res, status, payload) {
  if (res.destroyed) {
    return;
  }
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body)
  });
  res.end(body);
}
