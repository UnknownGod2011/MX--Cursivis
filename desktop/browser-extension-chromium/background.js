const HOST_NAME = "com.cursivis.browser_bridge";
const REQUEST_TIMEOUT_MS = 90000;
const DIRECT_BRIDGE_URL = "http://127.0.0.1:48830";
const HTTP_RECONNECT_DELAY_MS = 3000;
const KEEPALIVE_ALARM_NAME = "cursivis-bridge-keepalive";
const KEEPALIVE_INTERVAL_MINUTES = 0.5;

let nativePort = null;
let reconnectTimer = null;
let connectedAtUtc = null;
let lastNativeError = null;
let bridgeTransport = "none";
let httpBridgeLoopStarted = false;
let httpBridgeAvailable = false;

bootstrap();

chrome.runtime.onInstalled.addListener(() => {
  bootstrap();
});

chrome.runtime.onStartup.addListener(() => {
  bootstrap();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm?.name !== KEEPALIVE_ALARM_NAME) {
    return;
  }

  void ensureBridgeConnection();
});

chrome.action.onClicked.addListener(async () => {
  await ensureBridgeConnection();
});

async function bootstrap() {
  await ensureKeepAliveAlarm();
  await ensureBridgeConnection();
}

async function ensureKeepAliveAlarm() {
  try {
    const existing = await chrome.alarms.get(KEEPALIVE_ALARM_NAME);
    if (existing) {
      return;
    }

    await chrome.alarms.create(KEEPALIVE_ALARM_NAME, {
      periodInMinutes: KEEPALIVE_INTERVAL_MINUTES
    });
  } catch (error) {
    lastNativeError = error instanceof Error ? error.message : String(error);
  }
}

async function ensureBridgeConnection() {
  if (bridgeTransport === "http" && httpBridgeLoopStarted) {
    return null;
  }

  if (await isDirectBridgeAvailable()) {
    httpBridgeAvailable = true;
    bridgeTransport = "http";
    connectedAtUtc = connectedAtUtc || new Date().toISOString();
    startHttpBridgeLoop();
    return null;
  }

  httpBridgeAvailable = false;
  return await ensureNativeConnection();
}

async function ensureNativeConnection() {
  if (bridgeTransport === "http") {
    return null;
  }

  if (nativePort) {
    return nativePort;
  }

  try {
    nativePort = chrome.runtime.connectNative(HOST_NAME);
    connectedAtUtc = new Date().toISOString();
    lastNativeError = null;
    nativePort.onMessage.addListener(handleNativeMessage);
    nativePort.onDisconnect.addListener(() => {
      const runtimeError = chrome.runtime.lastError;
      lastNativeError = runtimeError?.message || "Native host disconnected.";
      nativePort = null;
      connectedAtUtc = null;
      bridgeTransport = "none";
      scheduleReconnect();
    });
    bridgeTransport = "native";

    postNativeMessage({
      type: "hello",
      browserName: detectBrowserName(),
      extensionId: chrome.runtime.id,
      connectedAtUtc,
      capabilities: [
        "get_active_tab_context",
        "execute_plan",
        "open_new_tab",
        "switch_tab",
        "scroll",
        "extract_dom"
      ]
    });
  } catch (error) {
    lastNativeError = error instanceof Error ? error.message : String(error);
    bridgeTransport = "none";
    scheduleReconnect();
  }

  return nativePort;
}

function scheduleReconnect() {
  if (reconnectTimer) {
    return;
  }

  reconnectTimer = setTimeout(async () => {
    reconnectTimer = null;
    await ensureBridgeConnection();
  }, 3000);
}

function postNativeMessage(message) {
  if (!nativePort) {
    return;
  }

  try {
    nativePort.postMessage(message);
  } catch (error) {
    lastNativeError = error instanceof Error ? error.message : String(error);
  }
}

async function handleNativeMessage(message) {
  if (!message || message.type !== "request" || !message.requestId || !message.action) {
    return;
  }

  try {
    await ensureNativeConnection();
    const payload = await processRequest(message.action, message.payload || {});
    postNativeMessage({
      type: "response",
      requestId: message.requestId,
      ok: true,
      payload
    });
  } catch (error) {
    postNativeMessage({
      type: "response",
      requestId: message.requestId,
      ok: false,
      error: error instanceof Error ? error.message : String(error)
    });
  }
}

function startHttpBridgeLoop() {
  if (httpBridgeLoopStarted) {
    return;
  }

  httpBridgeLoopStarted = true;
  void pollDirectBridge();
}

async function pollDirectBridge() {
  while (bridgeTransport === "http") {
    try {
      const response = await fetch(`${DIRECT_BRIDGE_URL}/extension/pull`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify(buildHelloPayload())
      });

      if (!response.ok) {
        throw new Error(`HTTP bridge returned ${response.status}.`);
      }

      const message = await response.json();
      if (message?.requestId && message?.action) {
        await processDirectBridgeRequest(message);
      }
    } catch (error) {
      lastNativeError = error instanceof Error ? error.message : String(error);
      bridgeTransport = "none";
      httpBridgeAvailable = false;
      httpBridgeLoopStarted = false;
      setTimeout(() => {
        void ensureBridgeConnection();
      }, HTTP_RECONNECT_DELAY_MS);
      return;
    }
  }

  httpBridgeLoopStarted = false;
}

async function processDirectBridgeRequest(message) {
  try {
    const payload = await processRequest(message.action, message.payload || {});
    await fetch(`${DIRECT_BRIDGE_URL}/extension/response`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        ...buildHelloPayload(),
        requestId: message.requestId,
        ok: true,
        payload
      })
    });
  } catch (error) {
    await fetch(`${DIRECT_BRIDGE_URL}/extension/response`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        ...buildHelloPayload(),
        requestId: message.requestId,
        ok: false,
        error: error instanceof Error ? error.message : String(error)
      })
    });
  }
}

function buildHelloPayload() {
  return {
    browserName: detectBrowserName(),
    extensionId: chrome.runtime.id,
    connectedAtUtc: connectedAtUtc || new Date().toISOString(),
    capabilities: [
      "get_active_tab_context",
      "execute_plan",
      "open_new_tab",
      "switch_tab",
      "scroll",
      "extract_dom"
    ]
  };
}

async function isDirectBridgeAvailable() {
  try {
    const response = await fetch(`${DIRECT_BRIDGE_URL}/health`, {
      method: "GET"
    });
    return response.ok;
  } catch {
    return false;
  }
}

async function processRequest(action, payload) {
  switch (String(action || "").trim().toLowerCase()) {
    case "ping":
      return {
        ok: true,
        browserName: detectBrowserName(),
        extensionId: chrome.runtime.id,
        connectedAtUtc,
        lastNativeError
      };
    case "get_active_tab_context":
      return await getActiveTabContext();
    case "execute_plan":
      return await executePlan(payload);
    default:
      throw new Error(`Unsupported native request: ${action}`);
  }
}

async function getActiveTabContext() {
  const tab = await getActiveTab();
  const pageContext = await collectContextFromTab(tab.id);

  return {
    ok: true,
    browserName: detectBrowserName(),
    extensionId: chrome.runtime.id,
    tabId: tab.id,
    pageContext
  };
}

async function executePlan(payload) {
  const steps = Array.isArray(payload?.steps) ? payload.steps : [];
  let tab = await getActiveTab();
  const logs = [];
  const detailLines = [];
  let executedSteps = 0;

  for (const step of steps) {
    const normalized = normalizeStep(step);
    if (!normalized) {
      continue;
    }

    logs.push(normalized.tool);
    const execution = await executeStep(tab, normalized);
    tab = execution.tab;
    executedSteps += 1;

    if (execution.payload?.warning) {
      detailLines.push(String(execution.payload.warning));
    }
  }

  const pageContext = await collectContextFromTab(tab.id);
  const message = detailLines.length > 0
    ? detailLines[detailLines.length - 1]
    : executedSteps > 0
      ? "Applied in the current logged-in browser tab."
      : "No browser actions were executed.";
  return {
    ok: true,
    success: true,
    executedSteps,
    message,
    details: detailLines.length > 0 ? detailLines.join("\n") : undefined,
    logs,
    pageContext
  };
}

async function executeStep(tab, step) {
  switch (step.tool) {
    case "navigate":
      if (!step.url) {
        throw new Error("navigate step requires url.");
      }

      return {
        tab: await updateTabUrl(tab.id, step.url),
        payload: null
      };
    case "open_new_tab":
      return {
        tab: await createTab(step.url || "about:blank"),
        payload: null
      };
    case "switch_tab":
      return {
        tab: await activateMatchingTab(step),
        payload: null
      };
    case "wait_ms":
      await delay(step.waitMs || 250);
      return {
        tab: await getTab(tab.id),
        payload: null
      };
    default:
      await ensureContentScript(tab.id);
      return await executeStepInTab(tab.id, step);
  }
}

async function executeStepInTab(tabId, step) {
  const frameContexts = await getFrameContexts(tabId);
  const frameIds = rankFramesForStep(step, frameContexts);
  let lastError = null;

  for (const frameId of frameIds) {
    try {
      const response = await sendMessageWithTimeout(tabId, {
        type: "execute_step",
        step
      }, REQUEST_TIMEOUT_MS, { frameId });

      if (!response?.ok) {
        lastError = response?.error || `Step failed in frame ${frameId}: ${step.tool}`;
        continue;
      }

      if (response?.requiresReloadWait) {
        await waitForTabComplete(tabId, REQUEST_TIMEOUT_MS);
      }

      return {
        tab: await getTab(tabId),
        payload: response?.payload || null
      };
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }
  }

  throw new Error(lastError || `Step failed: ${step.tool}`);
}

async function collectContextFromTab(tabId) {
  await ensureContentScript(tabId);
  const frameContexts = await getFrameContexts(tabId);
  if (frameContexts.length === 0) {
    throw new Error("Could not collect active tab context.");
  }

  const primary = frameContexts.find((item) => item.frameId === 0)?.payload || frameContexts[0].payload;
  const visibleParts = [];
  const seenVisible = new Set();
  const interactiveElements = [];

  for (const frameContext of frameContexts) {
    const visible = String(frameContext.payload?.visibleText || "").trim();
    if (visible && !seenVisible.has(visible)) {
      seenVisible.add(visible);
      visibleParts.push(visible);
    }

    for (const element of frameContext.payload?.interactiveElements || []) {
      interactiveElements.push(element);
    }
  }

  return {
    ...primary,
    visibleText: visibleParts.join(" ").slice(0, 10000),
    interactiveElements: interactiveElements.slice(0, 160)
  };
}

async function ensureContentScript(tabId) {
  const frameIds = await getFrameIds(tabId);
  for (const frameId of frameIds) {
    try {
      await sendMessageWithTimeout(tabId, { type: "ping" }, 1200, { frameId });
      return;
    } catch {
      // Try another frame or inject below.
    }
  }

  await chrome.scripting.executeScript({
    target: { tabId, allFrames: true },
    files: ["content.js"]
  });
  await delay(120);
}

async function sendMessageWithTimeout(tabId, message, timeoutMs = REQUEST_TIMEOUT_MS, options = undefined) {
  return await Promise.race([
    chrome.tabs.sendMessage(tabId, message, options),
    new Promise((_, reject) => {
      setTimeout(() => reject(new Error(`Timed out waiting for tab ${tabId} response.`)), timeoutMs);
    })
  ]);
}

async function getFrameIds(tabId) {
  try {
    const frames = await chrome.webNavigation.getAllFrames({ tabId });
    const ids = frames
      .map((frame) => frame.frameId)
      .filter((frameId) => Number.isInteger(frameId));
    return ids.length > 0 ? [...new Set(ids)] : [0];
  } catch {
    return [0];
  }
}

async function getFrameContexts(tabId) {
  const frameIds = await getFrameIds(tabId);
  const contexts = [];

  for (const frameId of frameIds) {
    try {
      const response = await sendMessageWithTimeout(tabId, {
        type: "collect_context"
      }, 2000, { frameId });

      if (response?.ok && response?.payload) {
        contexts.push({
          frameId,
          payload: response.payload
        });
      }
    } catch {
      // Ignore inaccessible frames and continue.
    }
  }

  return contexts;
}

function rankFramesForStep(step, frameContexts) {
  if (frameContexts.length === 0) {
    return [0];
  }

  const queryParts = [step.question, step.option, step.text, step.name, step.label, step.placeholder]
    .map((value) => String(value || "").toLowerCase().trim())
    .filter(Boolean);

  const scored = frameContexts.map((frameContext) => {
    const haystack = `${frameContext.payload?.title || ""} ${frameContext.payload?.visibleText || ""}`.toLowerCase();
    let score = frameContext.frameId === 0 ? 2 : 0;

    for (const query of queryParts) {
      if (haystack.includes(query)) {
        score += 10;
      }
    }

    score += Math.min((frameContext.payload?.interactiveElements || []).length, 40);

    return {
      frameId: frameContext.frameId,
      score
    };
  });

  return scored
    .sort((left, right) => right.score - left.score)
    .map((item) => item.frameId);
}

async function getActiveTab() {
  const tabs = await chrome.tabs.query({
    active: true,
    lastFocusedWindow: true
  });

  const tab = tabs[0];
  if (!tab?.id) {
    throw new Error("No active browser tab is available.");
  }

  if (isRestrictedUrl(tab.url)) {
    throw new Error("The current tab cannot be automated from the browser extension.");
  }

  return tab;
}

async function getTab(tabId) {
  const tab = await chrome.tabs.get(tabId);
  if (!tab?.id) {
    throw new Error("The target tab is no longer available.");
  }

  return tab;
}

async function updateTabUrl(tabId, url) {
  await chrome.tabs.update(tabId, {
    url,
    active: true
  });
  await waitForTabComplete(tabId, REQUEST_TIMEOUT_MS);
  return await getTab(tabId);
}

async function createTab(url) {
  const tab = await chrome.tabs.create({
    url,
    active: true
  });
  if (!tab?.id) {
    throw new Error("Could not open a new browser tab.");
  }

  await waitForTabComplete(tab.id, REQUEST_TIMEOUT_MS);
  return await getTab(tab.id);
}

async function activateMatchingTab(step) {
  const query = String(step.name || step.text || step.url || "").trim().toLowerCase();
  const tabs = await chrome.tabs.query({
    currentWindow: true
  });

  if (!query) {
    const currentTab = await getActiveTab();
    const currentIndex = typeof currentTab.index === "number" ? currentTab.index : 0;
    const nextTab = tabs[(currentIndex + 1) % Math.max(tabs.length, 1)];
    if (!nextTab?.id) {
      throw new Error("Could not find another tab to switch to.");
    }

    await chrome.tabs.update(nextTab.id, { active: true });
    return await getTab(nextTab.id);
  }

  const match = tabs.find((tab) => {
    const haystack = `${tab.title || ""} ${tab.url || ""}`.toLowerCase();
    return haystack.includes(query);
  });

  if (!match?.id) {
    throw new Error(`Could not find a matching browser tab for '${query}'.`);
  }

  await chrome.tabs.update(match.id, { active: true });
  return await getTab(match.id);
}

function waitForTabComplete(tabId, timeoutMs) {
  return new Promise((resolve, reject) => {
    let done = false;
    const finish = () => {
      if (done) {
        return;
      }

      done = true;
      clearTimeout(timer);
      chrome.tabs.onUpdated.removeListener(handleUpdated);
      resolve();
    };

    const handleUpdated = (updatedTabId, changeInfo) => {
      if (updatedTabId !== tabId || changeInfo.status !== "complete" || done) {
        return;
      }

      finish();
    };

    chrome.tabs.get(tabId, (tab) => {
      if (done) {
        return;
      }

      if (!chrome.runtime.lastError && tab?.status === "complete") {
        finish();
      }
    });

    const timer = setTimeout(() => {
      if (done) {
        return;
      }

      done = true;
      chrome.tabs.onUpdated.removeListener(handleUpdated);
      reject(new Error("Timed out waiting for the browser tab to finish loading."));
    }, timeoutMs);

    chrome.tabs.onUpdated.addListener(handleUpdated);
  });
}

function normalizeStep(step) {
  if (!step || typeof step !== "object" || typeof step.tool !== "string") {
    return null;
  }

  const normalized = {
    tool: step.tool.trim().toLowerCase()
  };

  for (const key of ["role", "name", "text", "label", "nameAttribute", "placeholder", "question", "option", "url", "key"]) {
    if (typeof step[key] === "string" && step[key].trim()) {
      normalized[key] = step[key].trim();
    }
  }

  if (Array.isArray(step.answers)) {
    const answers = step.answers
      .map((answer) => ({
        question: typeof answer?.question === "string" && answer.question.trim() ? answer.question.trim() : undefined,
        option: typeof answer?.option === "string" ? answer.option.trim() : "",
        questionIndex: Number.isInteger(answer?.questionIndex) && answer.questionIndex > 0
          ? answer.questionIndex
          : undefined,
        choiceIndex: Number.isInteger(answer?.choiceIndex) && answer.choiceIndex >= 0
          ? answer.choiceIndex
          : undefined
      }))
      .filter((answer) => answer.option)
      .slice(0, 128);

    if (answers.length > 0) {
      normalized.answers = answers;
    }
  }

  if (typeof step.advancePages === "boolean") {
    normalized.advancePages = step.advancePages;
  }

  if (Number.isFinite(step.waitMs) && step.waitMs > 0) {
    normalized.waitMs = Math.min(10000, Math.round(step.waitMs));
  }

  return normalized;
}

function isRestrictedUrl(url) {
  const value = String(url || "");
  return value.startsWith("chrome://") ||
    value.startsWith("edge://") ||
    value.startsWith("brave://") ||
    value.startsWith("vivaldi://") ||
    value.startsWith("opera://") ||
    value.startsWith("about:") ||
    value.startsWith("chrome-extension://");
}

function detectBrowserName() {
  const ua = navigator.userAgent.toLowerCase();
  if (ua.includes("edg/")) {
    return "edge";
  }

  if (ua.includes("brave")) {
    return "brave";
  }

  if (ua.includes("vivaldi")) {
    return "vivaldi";
  }

  if (ua.includes("opr/") || ua.includes("opera")) {
    return "opera";
  }

  if (ua.includes("arc")) {
    return "arc";
  }

  return "chrome";
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
