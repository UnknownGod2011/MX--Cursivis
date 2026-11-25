import test from "node:test";
import assert from "node:assert/strict";

import {
  isRetriableGeminiRequestError,
  withGoogleGenAiClient
} from "../src/apiKeyPool.js";
import { generateWithFallbackModels } from "../src/geminiService.js";

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

test("an expired recovery budget makes no provider calls and leaves the whole key pool usable", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "key-a,key-b,key-c";
  const retryContext = { deadlineAt: Date.now() - 1 };
  let calls = 0;

  await assert.rejects(
    () => withGoogleGenAiClient(
      () => generateWithFallbackModels(
        { models: { generateContent: async () => { calls += 1; } } },
        { contents: "a synthetic image-intent request" },
        ["primary", "fallback"],
        false,
        { retryContext, retryDelaysMs: [] }
      ),
      { canRetryError: isRetriableGeminiRequestError, retryContext }
    ),
    { code: "CURSIVIS_GEMINI_DEADLINE" }
  );
  assert.equal(calls, 0);
  const next = [];
  for (let i = 0; i < 3; i++) {
    await withGoogleGenAiClient(async (_client, entry) => next.push(entry.index));
  }
  assert.deepEqual(next, [0, 1, 2]);
});

test("a request rotation skips a key another request cooled after the rotation started", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "key-a,key-b,key-c";
  const firstKey = Promise.withResolvers();
  const tried = [];
  const original = withGoogleGenAiClient(async (_client, entry) => {
    tried.push(entry.index);
    if (entry.index === 0) {
      await firstKey.promise;
      throw new Error("503 UNAVAILABLE");
    }
    return entry.index;
  }, { canRetryError: isRetriableGeminiRequestError });
  // The first call holds key zero; a concurrent call hits quota on key one.
  try {
    const concurrent = await withGoogleGenAiClient(async (_client, entry) => {
      if (entry.index === 1) throw new Error("429 RESOURCE_EXHAUSTED: retry in 60s");
      return entry.index;
    });
    assert.equal(concurrent, 2);
  } finally {
    firstKey.resolve();
  }
  assert.equal(await original, 2);
  assert.deepEqual(tried, [0, 2]);
});

test("a late success cannot clear a cooldown imposed by a concurrent provider error", async () => {
  clearApiKeyPoolState();
  process.env.GOOGLE_API_KEYS = "shared-key";
  const pending = Promise.withResolvers();
  const successful = withGoogleGenAiClient(() => pending.promise);
  try {
    await assert.rejects(
      withGoogleGenAiClient(async () => { throw new Error("429 RESOURCE_EXHAUSTED: retry in 60s"); }),
      /quota-limited/
    );
  } finally {
    pending.resolve("successful in-flight request");
  }
  await successful;
  let calls = 0;
  await assert.rejects(
    withGoogleGenAiClient(async () => { calls += 1; }),
    /cooling down/
  );
  assert.equal(calls, 0);
});

test("a later transient error cannot shorten an active quota cooldown or re-enable an invalid key", async () => {
  for (const firstFailure of [
    "429 RESOURCE_EXHAUSTED: retry in 60s",
    "401 UNAUTHENTICATED: invalid API key"
  ]) {
    clearApiKeyPoolState();
    process.env.GOOGLE_API_KEYS = "shared-key";
    const pending = Promise.withResolvers();
    const concurrent = withGoogleGenAiClient(
      () => pending.promise, { canRetryError: isRetriableGeminiRequestError }
    );
    await assert.rejects(withGoogleGenAiClient(async () => { throw new Error(firstFailure); }));
    pending.reject(new Error("503 UNAVAILABLE: retry in 1s"));
    await assert.rejects(concurrent);
    await assert.rejects(
      withGoogleGenAiClient(async () => assert.fail("the key is still unavailable")),
      firstFailure.startsWith("429") ? /RESOURCE_EXHAUSTED.*Retry in (60|59)s/ : /invalid, revoked/
    );
  }
});
