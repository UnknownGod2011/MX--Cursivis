import test from "node:test";
import assert from "node:assert/strict";
import request from "supertest";

import {
  isPlausibleGeminiCredential,
  parseGeminiApiKeyPool,
  redactGeminiCredentials,
  validateGeminiApiKeyPool
} from "../src/geminiCredentials.js";
import { createApp } from "../src/app.js";

const legacyKey = `AIza${"Abc123_-".repeat(5)}`;
const authKey = `AQ.${"Zx9_.-".repeat(8)}`;

test("accepts both supported Gemini credential formats and a mixed pool", () => {
  assert.equal(isPlausibleGeminiCredential(legacyKey), true);
  assert.equal(isPlausibleGeminiCredential(authKey), true);

  const pool = validateGeminiApiKeyPool(`  ${legacyKey}\n; ${authKey}  `);
  assert.equal(pool.ok, true);
  assert.deepEqual(pool.keys, [legacyKey, authKey]);
  assert.deepEqual(parseGeminiApiKeyPool(` ${legacyKey},\r\n${authKey}; `), [legacyKey, authKey]);
});

test("rejects malformed or unsafe local credential input without overfitting to a prefix", () => {
  const invalidValues = [
    "paste_your_api_key_here",
    "this is a sentence instead of a credential",
    `AQ.${"x".repeat(36)}`,
    `AQ.${"a".repeat(18)}\u00e9${"b".repeat(18)}`,
    "AQ.short",
    `AQ.${"a".repeat(257)}`
  ];

  for (const value of invalidValues) {
    assert.equal(isPlausibleGeminiCredential(value), false, value);
  }

  const result = validateGeminiApiKeyPool(`${legacyKey}\nnot a key`);
  assert.equal(result.ok, false);
  assert.match(result.error, /Key 2/);
});

test("redacts legacy and auth credentials from provider diagnostics", () => {
  const text = `legacy=${legacyKey} auth=${authKey} https://example.test/?key=${authKey}`;
  const redacted = redactGeminiCredentials(text);

  assert.doesNotMatch(redacted, new RegExp(legacyKey));
  assert.doesNotMatch(redacted, new RegExp(authKey.replace(".", "\\.")));
  assert.match(redacted, /\[REDACTED_API_KEY\]/);
  assert.match(redacted, /key=\[REDACTED\]/);
});

test("runtime key updates accept mixed supported pools without returning a credential preview", async () => {
  const previousValues = Object.fromEntries([
    "GOOGLE_API_KEY",
    "GEMINI_API_KEY",
    "GOOGLE_API_KEYS",
    "GEMINI_API_KEYS"
  ].map((name) => [name, process.env[name]]));

  try {
    const response = await request(createApp())
      .post("/runtime/api-key")
      .send({ apiKey: `${legacyKey}\n${authKey}` })
      .expect(200);

    assert.deepEqual(response.body, { ok: true, totalKeys: 2 });
  } finally {
    for (const [name, value] of Object.entries(previousValues)) {
      if (value === undefined) {
        delete process.env[name];
      } else {
        process.env[name] = value;
      }
    }
  }
});
