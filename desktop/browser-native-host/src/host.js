const http = require("node:http");

const PORT = Number(process.env.CURSIVIS_EXTENSION_BRIDGE_PORT || 48830);
const REQUEST_TIMEOUT_MS = Number(process.env.CURSIVIS_EXTENSION_BRIDGE_TIMEOUT_MS || 90000);
const EXTENSION_POLL_TIMEOUT_MS = Number(process.env.CURSIVIS_EXTENSION_POLL_TIMEOUT_MS || 85000);
const EXTENSION_STALE_AFTER_MS = Number(process.env.CURSIVIS_EXTENSION_STALE_AFTER_MS || 120000);
const NATIVE_HOST_MODE = !process.stdout.isTTY && !process.stdin.isTTY;

let readBuffer = Buffer.alloc(0);
let extensionState = {
  connected: false,
  transport: "none",
  browserName: "unknown",
  extensionId: "",
  connectedAtUtc: null,
  capabilities: [],
  lastSeenUtc: null,
  lastError: null
};
let nextRequestId = 1;
const pendingRequests = new Map();
const queuedExtensionRequests = [];
let pendingPoll = null;

const server = http.createServer(async (req, res) => {
  try {
    await routeRequest(req, res);
  } catch (error) {
    writeJson(res, 500, {
      ok: false,
      error: error instanceof Error ? error.message : String(error)
    });
  }
});

server.listen(PORT, "127.0.0.1");

if (NATIVE_HOST_MODE) {
  process.stdin.on("readable", readNativeMessages);
  process.stdin.on("end", shutdown);
  process.stdin.on("error", shutdown);
}
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);

function shutdown() {
  for (const pending of pendingRequests.values()) {
    pending.reject(new Error("Native host disconnected."));
  }
  pendingRequests.clear();
  if (pendingPoll) {
    clearTimeout(pendingPoll.timer);
    safeWriteJson(pendingPoll.res, 503, {
      ok: false,
      error: "Extension bridge shutting down."
    });
    pendingPoll = null;
  }

  try {
    server.close();
  } catch {
    // Ignore close race.
  }

  process.exit(0);
}

function readNativeMessages() {
  let chunk;
  while ((chunk = process.stdin.read()) !== null) {
    readBuffer = Buffer.concat([readBuffer, chunk]);
    processBufferedMessages();
  }
}

function processBufferedMessages() {
  while (readBuffer.length >= 4) {
    const messageLength = readBuffer.readUInt32LE(0);
    if (readBuffer.length < messageLength + 4) {
      return;
    }

    const messageBytes = readBuffer.subarray(4, messageLength + 4);
    readBuffer = readBuffer.subarray(messageLength + 4);

    try {
      const message = JSON.parse(messageBytes.toString("utf8"));
      handleNativeMessage(message);
    } catch (error) {
      extensionState.lastError = error instanceof Error ? error.message : String(error);
    }
  }
}

function handleNativeMessage(message) {
  if (!message || typeof message !== "object") {
    return;
  }

  extensionState.lastSeenUtc = new Date().toISOString();

  if (message.type === "hello") {
    updateExtensionState(message, "native");
    return;
  }

  if (message.type !== "response" || !message.requestId) {
    return;
  }

  const pending = pendingRequests.get(String(message.requestId));
  if (!pending) {
    return;
  }

  clearTimeout(pending.timer);
  pendingRequests.delete(String(message.requestId));

  if (message.ok) {
    pending.resolve(message.payload || {});
  } else {
    pending.reject(new Error(String(message.error || "The browser extension returned an error.")));
  }
}

async function routeRequest(req, res) {
  const url = new URL(req.url || "/", `http://${req.headers.host || "127.0.0.1"}`);

  if (req.method === "GET" && url.pathname === "/health") {
    refreshExtensionState();
    writeJson(res, 200, {
      ok: true,
      extensionConnected: extensionState.connected,
      transport: extensionState.transport,
      browserName: extensionState.browserName,
      extensionId: extensionState.extensionId,
      connectedAtUtc: extensionState.connectedAtUtc,
      lastSeenUtc: extensionState.lastSeenUtc,
      lastError: extensionState.lastError,
      capabilities: extensionState.capabilities
    });
    return;
  }

  if (req.method === "POST" && url.pathname === "/extension/pull") {
    const body = await readJsonBody(req);
    handleExtensionPoll(body, res);
    return;
  }

  if (req.method === "POST" && url.pathname === "/extension/response") {
    const body = await readJsonBody(req);
    handleExtensionHttpResponse(body);
    writeJson(res, 200, { ok: true });
    return;
  }

  if (req.method === "POST" && url.pathname === "/active-tab-context") {
    const payload = await dispatchRequest("get_active_tab_context", {});
    writeJson(res, 200, payload);
    return;
  }

  if (req.method === "POST" && url.pathname === "/execute-plan") {
    const body = await readJsonBody(req);
    const payload = await dispatchRequest("execute_plan", {
      steps: Array.isArray(body.steps) ? body.steps : []
    });
    writeJson(res, 200, payload);
    return;
  }

  writeJson(res, 404, {
    ok: false,
    error: "Not found."
  });
}

function dispatchRequest(action, payload) {
  refreshExtensionState();
  if (!extensionState.connected) {
    throw new Error("No current browser extension session is connected.");
  }

  const requestId = `req_${nextRequestId++}`;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pendingRequests.delete(requestId);
      reject(new Error(`Timed out waiting for extension response: ${action}`));
    }, REQUEST_TIMEOUT_MS);

    pendingRequests.set(requestId, {
      resolve,
      reject,
      timer
    });

    const request = {
      type: "request",
      requestId,
      action,
      payload
    };

    if (extensionState.transport === "native") {
      writeNativeMessage(request);
      return;
    }

    queuedExtensionRequests.push(request);
    flushPendingPoll();
  });
}

function writeNativeMessage(message) {
  if (!NATIVE_HOST_MODE) {
    return;
  }

  const json = Buffer.from(JSON.stringify(message), "utf8");
  const header = Buffer.alloc(4);
  header.writeUInt32LE(json.length, 0);
  process.stdout.write(Buffer.concat([header, json]));
}

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (chunk) => chunks.push(chunk));
    req.on("end", () => {
      if (chunks.length === 0) {
        resolve({});
        return;
      }

      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString("utf8")));
      } catch (error) {
        reject(new Error("Invalid JSON request body."));
      }
    });
    req.on("error", reject);
  });
}

function writeJson(res, statusCode, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(statusCode, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body),
    "Access-Control-Allow-Origin": "*"
  });
  res.end(body);
}

function safeWriteJson(res, statusCode, payload) {
  try {
    if (!res.headersSent) {
      writeJson(res, statusCode, payload);
    }
  } catch {
    // Ignore broken pipe / already closed responses.
  }
}

function updateExtensionState(message, transport) {
  extensionState = {
    connected: true,
    transport,
    browserName: String(message.browserName || "chrome"),
    extensionId: String(message.extensionId || extensionState.extensionId || ""),
    connectedAtUtc: extensionState.connectedAtUtc || String(message.connectedAtUtc || new Date().toISOString()),
    capabilities: Array.isArray(message.capabilities) ? message.capabilities : extensionState.capabilities,
    lastSeenUtc: new Date().toISOString(),
    lastError: null
  };
}

function refreshExtensionState() {
  if (!extensionState.connected || !extensionState.lastSeenUtc) {
    return;
  }

  const lastSeen = Date.parse(extensionState.lastSeenUtc);
  if (Number.isNaN(lastSeen)) {
    return;
  }

  if (Date.now() - lastSeen <= EXTENSION_STALE_AFTER_MS) {
    return;
  }

  extensionState = {
    ...extensionState,
    connected: false,
    transport: "none",
    lastError: extensionState.lastError || "Extension session timed out."
  };
}

function handleExtensionPoll(message, res) {
  refreshExtensionState();
  updateExtensionState(message || {}, "http");

  if (pendingPoll) {
    clearTimeout(pendingPoll.timer);
    safeWriteJson(pendingPoll.res, 200, { ok: true, idle: true });
    pendingPoll = null;
  }

  const request = queuedExtensionRequests.shift();
  if (request) {
    writeJson(res, 200, request);
    return;
  }

  const timer = setTimeout(() => {
    if (!pendingPoll || pendingPoll.res !== res) {
      return;
    }

    pendingPoll = null;
    safeWriteJson(res, 200, { ok: true, idle: true });
  }, EXTENSION_POLL_TIMEOUT_MS);

  pendingPoll = { res, timer };
}

function flushPendingPoll() {
  if (!pendingPoll) {
    return;
  }

  const request = queuedExtensionRequests.shift();
  if (!request) {
    return;
  }

  const current = pendingPoll;
  pendingPoll = null;
  clearTimeout(current.timer);
  safeWriteJson(current.res, 200, request);
}

function handleExtensionHttpResponse(message) {
  if (!message || typeof message !== "object" || !message.requestId) {
    return;
  }

  updateExtensionState(message, "http");
  const pending = pendingRequests.get(String(message.requestId));
  if (!pending) {
    return;
  }

  clearTimeout(pending.timer);
  pendingRequests.delete(String(message.requestId));

  if (message.ok) {
    pending.resolve(message.payload || {});
  } else {
    pending.reject(new Error(String(message.error || "The browser extension returned an error.")));
  }
}
