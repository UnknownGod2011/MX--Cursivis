import test from "node:test";
import assert from "node:assert/strict";

import { withGoogleGenAiClient } from "../src/apiKeyPool.js";

function clearApiKeyPoolState() {
  globalThis.__cursivisApiKeyPools?.clear?.();
  delete process.env.GOOGLE_API_KEY;
  delete process.env.GEMINI_API_KEY;
  delete process.env.GOOGLE_API_KEYS;
  delete process.env.GEMINI_API_KEYS;
}

test("rotates to the next configured API key when the current key is quota-limited", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "key-one,key-two,key-three";

  const attemptedKeys = [];

  const result = await withGoogleGenAiClient(async (_client, entry) => {
    attemptedKeys.push(entry.apiKey);

    if (entry.apiKey === "key-one") {
      throw new Error("RESOURCE_EXHAUSTED: retry in 12s");
    }

    return entry.apiKey;
  });

  assert.equal(result, "key-two");
  assert.deepEqual(attemptedKeys, ["key-one", "key-two"]);
});

test("keeps exhausted keys on cooldown for the next request and skips them immediately", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "alpha,beta";

  const firstPass = [];
  const secondPass = [];

  const firstResult = await withGoogleGenAiClient(async (_client, entry) => {
    firstPass.push(entry.apiKey);

    if (entry.apiKey === "alpha") {
      throw new Error("RESOURCE_EXHAUSTED: retry in 60s");
    }

    return entry.apiKey;
  });

  const secondResult = await withGoogleGenAiClient(async (_client, entry) => {
    secondPass.push(entry.apiKey);
    return entry.apiKey;
  });

  assert.equal(firstResult, "beta");
  assert.deepEqual(firstPass, ["alpha", "beta"]);
  assert.equal(secondResult, "beta");
  assert.deepEqual(secondPass, ["beta"]);
});

test("rotates past invalid API keys instead of breaking the whole pool", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "bad-key,good-key";

  const attemptedKeys = [];

  const result = await withGoogleGenAiClient(async (_client, entry) => {
    attemptedKeys.push(entry.apiKey);

    if (entry.apiKey === "bad-key") {
      throw new Error("API key not valid. Please pass a valid API key. status: API_KEY_INVALID");
    }

    return entry.apiKey;
  });

  assert.equal(result, "good-key");
  assert.deepEqual(attemptedKeys, ["bad-key", "good-key"]);
});

test("gives a clear setup error when no API keys are configured", async () => {
  clearApiKeyPoolState();

  await assert.rejects(
    () => withGoogleGenAiClient(async () => "unused"),
    /No Gemini API keys are configured/
  );
});

test("gives a clear pool error when every configured key fails", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "bad-one,bad-two";

  const attemptedKeys = [];

  await assert.rejects(
    () => withGoogleGenAiClient(async (_client, entry) => {
      attemptedKeys.push(entry.apiKey);
      throw new Error("RESOURCE_EXHAUSTED: quota exceeded");
    }),
    /All Gemini API keys are temporarily unavailable/
  );

  assert.deepEqual(attemptedKeys, ["bad-one", "bad-two"]);
});
