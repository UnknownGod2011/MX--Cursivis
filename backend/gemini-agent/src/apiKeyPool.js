import { GoogleGenAI } from "@google/genai";
import { getGeminiErrorDiagnostics, traceGeminiEvent } from "./geminiDiagnostics.js";

const DEFAULT_QUOTA_COOLDOWN_MS = 2 * 60 * 1000;
const DEFAULT_AUTH_COOLDOWN_MS = 15 * 60 * 1000;
const DEFAULT_TRANSIENT_COOLDOWN_MS = 15 * 1000;
const GLOBAL_POOL_CACHE = globalThis.__cursivisApiKeyPools ??= new Map();

export function getConfiguredApiKeys() {
  const candidates = [
    process.env.GOOGLE_API_KEY || "",
    process.env.GEMINI_API_KEY || "",
    ...(process.env.GOOGLE_API_KEYS || "").split(","),
    ...(process.env.GEMINI_API_KEYS || "").split(",")
  ];

  return candidates
    .map((value) => String(value || "").trim())
    .filter(Boolean)
    .filter((value, index, array) => array.indexOf(value) === index);
}

export function hasConfiguredApiKeys() {
  return getConfiguredApiKeys().length > 0;
}

export async function withGoogleGenAiClient(executor, { canRetryError = isRetriableApiKeyError } = {}) {
  const pool = getPool();
  if (pool.entries.length === 0) {
    throw new Error("No Gemini API keys are configured. Paste one or more API keys in Cursivis Settings to use API LLM mode.");
  }

  const candidates = getCandidateEntries(pool.entries);
  if (candidates.length === 0) {
    throw createUnavailablePoolError(pool.entries);
  }

  let lastError = null;

  for (const entry of candidates) {
    entry.inFlight += 1;
    try {
      entry.lastUsedAt = Date.now();
      traceGeminiEvent("key_selected", { keyIndex: entry.index + 1 });
      const result = await executor(entry.client, entry);
      markEntrySuccess(entry);
      return result;
    } catch (error) {
      lastError = error;
      if (canRetryError(error)) {
        const cooldownMs = markEntryFailure(entry, error);
        traceGeminiEvent("key_rotated", {
          keyIndex: entry.index + 1,
          cooldownMs,
          ...getGeminiErrorDiagnostics(error)
        });
        continue;
      }

      traceGeminiEvent("key_failure_final", {
        keyIndex: entry.index + 1,
        ...getGeminiErrorDiagnostics(error)
      });

      throw error;
    } finally {
      entry.inFlight = Math.max(0, entry.inFlight - 1);
    }
  }

  traceGeminiEvent("key_pool_exhausted", getGeminiErrorDiagnostics(lastError));

  throw new Error(
    `All Gemini API keys are temporarily unavailable or invalid. ${formatPoolFailureDetail(lastError)}`.trim(),
    { cause: lastError }
  );
}

export function markEntryFailure(entry, error) {
  const cooldownMs = computeCooldownMs(error);
  const failureCategory = classifyGeminiError(error);
  const now = Date.now();
  entry.consecutiveFailures += 1;
  entry.lastFailureAt = now;
  entry.failureCategory = failureCategory;
  entry.disabled = failureCategory === "auth";
  entry.cooldownUntil = entry.disabled ? Number.POSITIVE_INFINITY : now + cooldownMs;
  return cooldownMs;
}

export function isRetriableApiKeyError(error) {
  const message = getErrorMessage(error);
  return isQuotaOrRateLimitError(message) || isAuthOrPermissionError(message);
}

export function isRetriableGeminiRequestError(error) {
  const message = getErrorMessage(error);
  return isRetriableApiKeyError(message) || (isTransientProviderError(message) && !isTransportSecurityError(message));
}

export function isQuotaOrRateLimitError(error) {
  return /RESOURCE_EXHAUSTED|quota exceeded|rate limit|429/i.test(getErrorMessage(error));
}

export function isTransientProviderError(error) {
  const message = getErrorMessage(error);
  if (isAuthOrPermissionError(message) || isQuotaOrRateLimitError(message)) {
    return false;
  }

  return /\b500\b|\b502\b|\b503\b|\b504\b|UNAVAILABLE|service unavailable|temporar(?:y|ily)|overload(?:ed)?|out of capacity|capacity exhausted|timeout|timed out|AbortError|aborted|ETIMEDOUT|ECONNABORTED|ECONNRESET|ECONNREFUSED|EAI_AGAIN|ENOTFOUND|UND_ERR_CONNECT_TIMEOUT|socket hang up|network (?:error|interruption)|fetch failed|TLS|SSL|CERT_|certificate/i.test(message);
}

export function isAuthOrPermissionError(error) {
  return /api key was reported as leaked|api key .*?(?:blocked|revoked|deleted|expired)|invalid api key|api key not valid|API_KEY_INVALID|UNAUTHENTICATED|permission denied|PERMISSION_DENIED|\b401\b|\b403\b/i.test(getErrorMessage(error));
}

export function isModelAvailabilityError(error) {
  return /\b404\b|NOT_FOUND|model .*?(?:not found|not available|unsupported|retired|deprecated)|unsupported model/i.test(getErrorMessage(error));
}

export function isMalformedRequestError(error) {
  return /\b400\b|INVALID_ARGUMENT|malformed request|invalid request/i.test(getErrorMessage(error));
}

export function classifyGeminiError(error) {
  if (isAuthOrPermissionError(error)) {
    return "auth";
  }

  if (isQuotaOrRateLimitError(error)) {
    return "quota";
  }

  if (isModelAvailabilityError(error)) {
    return "model";
  }

  if (isMalformedRequestError(error)) {
    return "malformed";
  }

  if (isTransportSecurityError(error)) {
    return "transport_security";
  }

  if (isTransientProviderError(error)) {
    return "transient";
  }

  return "unknown";
}

function computeCooldownMs(error) {
  const message = getErrorMessage(error);
  const retryInSecondsMatch = message.match(/retry in ([0-9]+(?:\.[0-9]+)?)s/i);
  if (retryInSecondsMatch) {
    return Math.max(1000, Math.ceil(Number(retryInSecondsMatch[1]) * 1000));
  }

  const retryDelayMatch = message.match(/"retryDelay":"([0-9]+)s"/i);
  if (retryDelayMatch) {
    return Math.max(1000, Number(retryDelayMatch[1]) * 1000);
  }

  if (isAuthOrPermissionError(message)) {
    return DEFAULT_AUTH_COOLDOWN_MS;
  }

  if (isTransientProviderError(message)) {
    return DEFAULT_TRANSIENT_COOLDOWN_MS;
  }

  return DEFAULT_QUOTA_COOLDOWN_MS;
}

function formatPoolFailureDetail(error) {
  const message = getErrorMessage(error);
  if (!message) {
    return "Replace exhausted/invalid keys or try again after the cooldown.";
  }

  if (isQuotaOrRateLimitError(message)) {
    return "The saved key pool appears quota-limited or rate-limited.";
  }

  if (isAuthOrPermissionError(message)) {
    return "The saved key pool contains invalid, blocked, or unauthorized keys.";
  }

  if (isTransientProviderError(message)) {
    return "Gemini is temporarily unavailable or capacity-limited. Cursivis retried the saved key pool; try again shortly.";
  }

  return "Replace exhausted/invalid keys or try again after the cooldown.";
}

function getPool() {
  const apiKeys = getConfiguredApiKeys();
  const signature = apiKeys.join("|");
  if (GLOBAL_POOL_CACHE.has(signature)) {
    return GLOBAL_POOL_CACHE.get(signature);
  }

  const pool = {
    entries: apiKeys.map((apiKey, index) => ({
      apiKey,
      index,
      client: new GoogleGenAI({ apiKey }),
      cooldownUntil: 0,
      lastUsedAt: 0,
      lastSuccessAt: 0,
      lastFailureAt: 0,
      consecutiveFailures: 0,
      failureCategory: null,
      disabled: false,
      inFlight: 0
    }))
  };

  GLOBAL_POOL_CACHE.clear();
  GLOBAL_POOL_CACHE.set(signature, pool);
  return pool;
}

function markEntrySuccess(entry) {
  entry.cooldownUntil = 0;
  entry.lastSuccessAt = Date.now();
  entry.consecutiveFailures = 0;
  entry.failureCategory = null;
  entry.disabled = false;
}

function isTransportSecurityError(error) {
  return /TLS|SSL|CERT_|certificate|self signed|unable to verify/i.test(getErrorMessage(error));
}

function getErrorMessage(error) {
  if (error instanceof Error) {
    return [error.message, error.code, error.status, error.statusText]
      .filter(Boolean)
      .join(" ");
  }

  if (error && typeof error === "object") {
    return [error.message, error.code, error.status, error.statusText]
      .filter(Boolean)
      .join(" ");
  }

  return String(error || "");
}

function getCandidateEntries(entries) {
  const now = Date.now();
  return entries
    .filter((entry) => !entry.disabled && entry.cooldownUntil <= now)
    .sort((left, right) => left.inFlight - right.inFlight || left.lastUsedAt - right.lastUsedAt);
}

function createUnavailablePoolError(entries) {
  const enabledEntries = entries.filter((entry) => !entry.disabled);
  if (enabledEntries.length === 0) {
    const error = new Error("All saved Gemini API keys are invalid, revoked, blocked, or unauthorized. Update the API key pool in Cursivis Settings.");
    error.code = "CURSIVIS_GEMINI_KEY_POOL_INVALID";
    return error;
  }

  const now = Date.now();
  const nextEntry = [...enabledEntries].sort((left, right) => left.cooldownUntil - right.cooldownUntil)[0];
  const retryAfterSeconds = Math.max(1, Math.ceil((nextEntry.cooldownUntil - now) / 1000));
  const quotaLimited = enabledEntries.some((entry) => entry.failureCategory === "quota");
  const prefix = quotaLimited ? "RESOURCE_EXHAUSTED" : "503 UNAVAILABLE";
  const error = new Error(`${prefix}: The saved Gemini API key pool is cooling down. Retry in ${retryAfterSeconds}s.`);
  error.code = quotaLimited ? "CURSIVIS_GEMINI_KEY_POOL_QUOTA" : "CURSIVIS_GEMINI_KEY_POOL_COOLDOWN";
  return error;
}
