import test from "node:test";
import assert from "node:assert/strict";

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
