import { GoogleGenAI } from "@google/genai";

const DEFAULT_QUOTA_COOLDOWN_MS = 2 * 60 * 1000;
const DEFAULT_AUTH_COOLDOWN_MS = 15 * 60 * 1000;
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
  let lastError = null;

  for (const entry of candidates) {
    try {
      entry.lastUsedAt = Date.now();
      return await executor(entry.client, entry);
    } catch (error) {
      lastError = error;
      if (canRetryError(error)) {
        markEntryFailure(entry, error);
        continue;
      }

      throw error;
    }
  }

  throw new Error(
    `All Gemini API keys are temporarily unavailable or invalid. ${formatPoolFailureDetail(lastError)}`.trim(),
    { cause: lastError }
  );
}

export function markEntryFailure(entry, error) {
  entry.cooldownUntil = Date.now() + computeCooldownMs(error);
}

export function isRetriableApiKeyError(error) {
  const message = error instanceof Error ? error.message : String(error);
  return /RESOURCE_EXHAUSTED|quota exceeded|rate limit|429|api key was reported as leaked|blocked|invalid api key|api key not valid|API_KEY_INVALID|permission denied|401|403/i.test(message);
}

function computeCooldownMs(error) {
  const message = error instanceof Error ? error.message : String(error);
  const retryInSecondsMatch = message.match(/retry in ([0-9]+(?:\.[0-9]+)?)s/i);
  if (retryInSecondsMatch) {
    return Math.max(1000, Math.ceil(Number(retryInSecondsMatch[1]) * 1000));
  }

  const retryDelayMatch = message.match(/"retryDelay":"([0-9]+)s"/i);
  if (retryDelayMatch) {
    return Math.max(1000, Number(retryDelayMatch[1]) * 1000);
  }

  if (/api key was reported as leaked|blocked|invalid api key|api key not valid|API_KEY_INVALID|permission denied|401|403/i.test(message)) {
    return DEFAULT_AUTH_COOLDOWN_MS;
  }

  return DEFAULT_QUOTA_COOLDOWN_MS;
}

function formatPoolFailureDetail(error) {
  const message = error instanceof Error ? error.message : String(error || "");
  if (!message) {
    return "Replace exhausted/invalid keys or try again after the cooldown.";
  }

  if (/RESOURCE_EXHAUSTED|quota exceeded|rate limit|429/i.test(message)) {
    return "The saved key pool appears quota-limited or rate-limited.";
  }

  if (/api key was reported as leaked|blocked|invalid api key|api key not valid|API_KEY_INVALID|permission denied|401|403/i.test(message)) {
    return "The saved key pool contains invalid, blocked, or unauthorized keys.";
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
    entries: apiKeys.map((apiKey) => ({
      apiKey,
      client: new GoogleGenAI({ apiKey }),
      cooldownUntil: 0,
      lastUsedAt: 0
    }))
  };

  GLOBAL_POOL_CACHE.clear();
  GLOBAL_POOL_CACHE.set(signature, pool);
  return pool;
}

function getCandidateEntries(entries) {
  const now = Date.now();
  const available = entries
    .filter((entry) => entry.cooldownUntil <= now)
    .sort((left, right) => left.lastUsedAt - right.lastUsedAt);

  if (available.length > 0) {
    return available;
  }

  return [...entries].sort((left, right) => left.cooldownUntil - right.cooldownUntil);
}
