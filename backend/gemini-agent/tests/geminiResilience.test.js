import test from "node:test";
import assert from "node:assert/strict";
import { setTimeout as wait } from "node:timers/promises";

import { generateWithFallbackModels } from "../src/geminiService.js";
import { getGeminiErrorDiagnostics } from "../src/geminiDiagnostics.js";

function createClient(handler) {
  return {
    models: {
      generateContent: handler
    }
  };
}

function retryOptions(retryDelaysMs = [0, 0]) {
  return {
    keyIndex: 0,
    retryContext: { deadlineAt: Date.now() + 5000 },
    retryDelaysMs
  };
}

test("diagnostics redact provider credentials without hiding useful status", () => {
  const fakeGeminiKey = `AI${"za"}${"x".repeat(24)}`;
  const fakeProviderKey = `s${"k-"}${"y".repeat(24)}`;
  const fakeBearerToken = "b".repeat(24);
  const error = new Error(
    `503 UNAVAILABLE Authorization: Bearer ${fakeBearerToken} ` +
      `key=${fakeGeminiKey} ${fakeProviderKey}`
  );

  const diagnostics = getGeminiErrorDiagnostics(error);

  assert.equal(diagnostics.status, 503);
  assert.doesNotMatch(diagnostics.error, /b{20}|x{20}|y{20}/);
  assert.match(diagnostics.error, /Bearer \[REDACTED\]/);
  assert.match(diagnostics.error, /key=\[REDACTED(?:_API_KEY)?\]/);
});

test("retries the same model before falling back on a transient 503", async () => {
  const calls = [];
  const client = createClient(async (request) => {
    calls.push(request);
    if (request.model === "primary-model") {
      throw new Error("503 UNAVAILABLE: temporary capacity");
    }

    return { text: "fallback success" };
  });

  const result = await generateWithFallbackModels(
    client,
    { model: "primary-model", contents: "hello", config: {} },
    ["primary-model", "fallback-model"],
    false,
    retryOptions()
  );

  assert.equal(result.model, "fallback-model");
  assert.deepEqual(calls.map((call) => call.model), [
    "primary-model",
    "primary-model",
    "primary-model",
    "fallback-model"
  ]);
  assert.ok(calls.every((call) => call.config.httpOptions.retryOptions.attempts === 1));
  assert.ok(calls.every((call) => call.config.httpOptions.timeout >= 10_000));
  assert.ok(calls.every((call) => call.config.abortSignal instanceof AbortSignal));
});

test("uses client cancellation for recovery budgets below Google's minimum HTTP deadline", async () => {
  let capturedRequest = null;
  const client = createClient(async (request) => {
    capturedRequest = request;
    return { text: "quick response" };
  });

  await generateWithFallbackModels(
    client,
    { model: "primary-model", contents: "hello", config: {} },
    ["primary-model"],
    false,
    {
      keyIndex: 0,
      retryContext: { deadlineAt: Date.now() + 2200 },
      retryDelaysMs: []
    }
  );

  assert.ok(capturedRequest.config.httpOptions.timeout >= 10_000);
  assert.ok(capturedRequest.config.abortSignal instanceof AbortSignal);
});

test("a started operation respects a sub-second deadline without trying another model", async () => {
  const tried = [];
  const keepAlive = setTimeout(() => {}, 1500);
  const client = createClient(async ({ model, config }) => {
    tried.push(model);
    await wait(1200, undefined, { signal: config.abortSignal });
    return { text: "late response" };
  });
  try {
    await assert.rejects(
      generateWithFallbackModels(client, { contents: "fixture" }, ["primary", "fallback"], false, {
        retryContext: { deadlineAt: Date.now() + 200 },
        retryDelaysMs: []
      }),
      { code: "CURSIVIS_GEMINI_DEADLINE" }
    );
    assert.deepEqual(tried, ["primary"]);
  } finally {
    clearTimeout(keepAlive);
  }
});

test("still retries the same model for a per-attempt timeout when there is budget left", async (t) => {
  const previous = process.env.GEMINI_ATTEMPT_TIMEOUT_MS;
  process.env.GEMINI_ATTEMPT_TIMEOUT_MS = "50";
  t.after(() => {
    if (previous === undefined) delete process.env.GEMINI_ATTEMPT_TIMEOUT_MS;
    else process.env.GEMINI_ATTEMPT_TIMEOUT_MS = previous;
  });
  const tried = [];
  const keepAlive = setTimeout(() => {}, 1500);
  const client = createClient(async ({ model, config }) => {
    tried.push(model);
    if (tried.length === 1) {
      await wait(1200, undefined, { signal: config.abortSignal });
      assert.fail("the first attempt should be cancelled");
    }
    return { text: "retry went through" };
  });
  try {
    const result = await generateWithFallbackModels(
      client, { contents: "fixture" }, ["primary", "fallback"], false, retryOptions([0])
    );
    assert.equal(result.model, "primary");
    assert.deepEqual(tried, ["primary", "primary"]);
  } finally {
    clearTimeout(keepAlive);
  }
});

test("a cancelled caller stops both an in-flight attempt and a pending backoff", async () => {
  for (const inBackoff of [false, true]) {
    const controller = new AbortController();
    let calls = 0;
    let timer;
    const client = createClient(async ({ config }) => {
      calls += 1;
      timer = setTimeout(() => controller.abort(), 20);
      if (inBackoff) throw new Error("503 UNAVAILABLE");
      await wait(1000, undefined, { signal: config.abortSignal });
      assert.fail("the provider should receive the cancellation");
    });
    try {
      await assert.rejects(
        generateWithFallbackModels(
          client, { contents: "fixture", config: { abortSignal: controller.signal } },
          ["primary", "fallback"], false, retryOptions([1000])
        ),
        { code: "CURSIVIS_GEMINI_CANCELLED" }
      );
      assert.equal(calls, 1);
    } finally {
      clearTimeout(timer);
    }
  }
});

test("pre-cancelled requests never start a provider attempt", async () => {
  let calls = 0;
  await assert.rejects(
    generateWithFallbackModels(createClient(async () => { calls += 1; }), {
      contents: "fixture",
      config: { abortSignal: AbortSignal.abort() }
    }, ["primary"], false, retryOptions()),
    { code: "CURSIVIS_GEMINI_CANCELLED" }
  );
  assert.equal(calls, 0);
});

test("removes deprecated sampling fields for current stable Gemini models", async () => {
  let capturedRequest = null;
  const client = createClient(async (request) => {
    capturedRequest = request;
    return { text: "compatible response" };
  });

  await generateWithFallbackModels(
    client,
    {
      model: "gemini-3.6-flash",
      contents: "hello",
      config: { temperature: 0.2, topP: 0.8, topK: 20, numPredict: 24 }
    },
    ["gemini-3.6-flash"],
    false,
    retryOptions([])
  );

  assert.equal(capturedRequest.config.temperature, undefined);
  assert.equal(capturedRequest.config.topP, undefined);
  assert.equal(capturedRequest.config.topK, undefined);
  assert.equal(capturedRequest.config.numPredict, undefined);
});

test("keeps the current model when a same-model retry succeeds", async () => {
  let calls = 0;
  const client = createClient(async () => {
    calls += 1;
    if (calls === 1) {
      throw new Error("502 backend error");
    }

    return { text: "recovered" };
  });

  const result = await generateWithFallbackModels(
    client,
    { model: "primary-model", contents: "hello", config: {} },
    ["primary-model", "fallback-model"],
    false,
    retryOptions([0])
  );

  assert.equal(result.model, "primary-model");
  assert.equal(calls, 2);
});

test("falls back immediately when a configured model is retired or unavailable", async () => {
  const calls = [];
  const client = createClient(async (request) => {
    calls.push(request.model);
    if (request.model === "retired-model") {
      throw new Error("404 NOT_FOUND: model is retired");
    }

    return { text: "supported model" };
  });

  const result = await generateWithFallbackModels(
    client,
    { model: "retired-model", contents: "hello", config: {} },
    ["retired-model", "supported-model"],
    false,
    retryOptions()
  );

  assert.equal(result.model, "supported-model");
  assert.deepEqual(calls, ["retired-model", "supported-model"]);
});

test("does not retry or fall back for malformed requests", async () => {
  let calls = 0;
  const client = createClient(async () => {
    calls += 1;
    throw new Error("400 INVALID_ARGUMENT: malformed request");
  });

  await assert.rejects(
    () => generateWithFallbackModels(
      client,
      { model: "primary-model", contents: "hello", config: {} },
      ["primary-model", "fallback-model"],
      false,
      retryOptions()
    ),
    /INVALID_ARGUMENT/
  );
  assert.equal(calls, 1);
});

test("retries TLS failures but does not rotate through model fallbacks", async () => {
  const calls = [];
  const client = createClient(async (request) => {
    calls.push(request.model);
    throw new Error("CERT_HAS_EXPIRED: TLS certificate validation failed");
  });

  await assert.rejects(
    () => generateWithFallbackModels(
      client,
      { model: "primary-model", contents: "hello", config: {} },
      ["primary-model", "fallback-model"],
      false,
      retryOptions()
    ),
    /CERT_HAS_EXPIRED/
  );
  assert.deepEqual(calls, ["primary-model", "primary-model", "primary-model"]);
});
