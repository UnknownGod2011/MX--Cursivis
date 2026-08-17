import test from "node:test";
import assert from "node:assert/strict";

import {
  isRetriableGeminiRequestError,
  withGoogleGenAiClient
} from "../src/apiKeyPool.js";

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

test("rotates across a mixed legacy and auth-key pool", async () => {
  clearApiKeyPoolState();
  const legacyKey = `AIza${"Abc123_-".repeat(5)}`;
  const authKey = `AQ.${"Zx9_.-".repeat(8)}`;
  process.env.GOOGLE_API_KEYS = `${legacyKey},${authKey}`;

  const attemptedIndexes = [];
  const result = await withGoogleGenAiClient(async (_client, entry) => {
    attemptedIndexes.push(entry.index);
    if (entry.index === 0) {
      throw new Error("RESOURCE_EXHAUSTED: retry in 12s");
    }

    return entry.index;
  });

  assert.equal(result, 1);
  assert.deepEqual(attemptedIndexes, [0, 1]);
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

  const secondPass = [];
  const secondResult = await withGoogleGenAiClient(async (_client, entry) => {
    secondPass.push(entry.apiKey);
    return entry.apiKey;
  });

  assert.equal(secondResult, "good-key");
  assert.deepEqual(secondPass, ["good-key"]);
});

test("rotates to another key for a transient Gemini 503 without permanently poisoning the pool", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "temporary-key,healthy-key";

  const attemptedKeys = [];
  const result = await withGoogleGenAiClient(async (_client, entry) => {
    attemptedKeys.push(entry.apiKey);
    if (entry.apiKey === "temporary-key") {
      throw new Error("503 UNAVAILABLE: This model is currently experiencing high demand.");
    }

    return entry.apiKey;
  }, { canRetryError: isRetriableGeminiRequestError });

  assert.equal(result, "healthy-key");
  assert.deepEqual(attemptedKeys, ["temporary-key", "healthy-key"]);
});

test("does not rotate the key pool for a malformed Gemini request", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "first-key,second-key";

  const attemptedKeys = [];
  await assert.rejects(
    () => withGoogleGenAiClient(async (_client, entry) => {
      attemptedKeys.push(entry.apiKey);
      throw new Error("400 INVALID_ARGUMENT: request schema is invalid");
    }, { canRetryError: isRetriableGeminiRequestError }),
    /INVALID_ARGUMENT/
  );

  assert.deepEqual(attemptedKeys, ["first-key"]);
});

test("reloads the pool when saved API key configuration changes", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "initial-key";

  const first = await withGoogleGenAiClient(async (_client, entry) => entry.apiKey);
  process.env.GOOGLE_API_KEYS = "replacement-key";
  const second = await withGoogleGenAiClient(async (_client, entry) => entry.apiKey);

  assert.equal(first, "initial-key");
  assert.equal(second, "replacement-key");
});

test("keeps an exhausted transient pool on cooldown and recovers it automatically", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "recovering-key";

  await assert.rejects(
    () => withGoogleGenAiClient(async () => {
      throw new Error("503 UNAVAILABLE: retry in 1s");
    }, { canRetryError: isRetriableGeminiRequestError }),
    /temporarily unavailable/
  );

  let callsDuringCooldown = 0;
  await assert.rejects(
    () => withGoogleGenAiClient(async () => {
      callsDuringCooldown += 1;
      return "unexpected";
    }, { canRetryError: isRetriableGeminiRequestError }),
    /cooling down/
  );
  assert.equal(callsDuringCooldown, 0);

  await new Promise((resolve) => setTimeout(resolve, 1100));
  const result = await withGoogleGenAiClient(async () => "recovered", {
    canRetryError: isRetriableGeminiRequestError
  });
  assert.equal(result, "recovered");
});

test("distributes concurrent requests across healthy keys without deadlocking", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "key-a,key-b,key-c";
  const counts = new Map();

  await Promise.all(Array.from({ length: 60 }, () => withGoogleGenAiClient(async (_client, entry) => {
    counts.set(entry.index, (counts.get(entry.index) || 0) + 1);
    await new Promise((resolve) => setTimeout(resolve, 5));
    return entry.index;
  })));

  assert.equal(counts.size, 3);
  const distribution = [...counts.values()];
  assert.ok(Math.max(...distribution) - Math.min(...distribution) <= 1, JSON.stringify(distribution));
});

test("quarantines a fully invalid pool without repeatedly calling rejected keys", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "invalid-one,invalid-two";

  await assert.rejects(
    () => withGoogleGenAiClient(async () => {
      throw new Error("401 UNAUTHENTICATED: API key not valid");
    }),
    /temporarily unavailable or invalid/
  );

  let repeatedCalls = 0;
  await assert.rejects(
    () => withGoogleGenAiClient(async () => {
      repeatedCalls += 1;
      return "unexpected";
    }),
    /All saved Gemini API keys are invalid/
  );
  assert.equal(repeatedCalls, 0);
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
