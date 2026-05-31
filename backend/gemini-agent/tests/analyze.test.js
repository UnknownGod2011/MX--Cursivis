import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import request from "supertest";
import { createAiProviderFromEnv, normalizeProviderId } from "../src/aiProviderFactory.js";
import { createApp } from "../src/app.js";
import { createBrowserActionPlanner } from "../src/browserActionPlanner.js";
import { detectBrowserTaskPack } from "../src/browserTaskPacks.js";
import {
  buildPrompt,
  describeIntentRoutingConcern,
  inferFallbackType,
  inferUsefulCodeAction,
  inferUsefulEmailAction,
  looksLikeQuestionSet,
  normalizeIntentDecision
} from "../src/contentClassifier.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, "..", "..", "..");

const requestSchema = JSON.parse(
  fs.readFileSync(path.join(repoRoot, "shared", "ipc-protocol", "schema", "agent-request.schema.json"), "utf8")
);
const responseSchema = JSON.parse(
  fs.readFileSync(path.join(repoRoot, "shared", "ipc-protocol", "schema", "agent-response.schema.json"), "utf8")
);

const ajv = new Ajv2020({ allErrors: true, strict: false });
addFormats(ajv);
const validateRequestSchema = ajv.compile(requestSchema);
const validateResponseSchema = ajv.compile(responseSchema);

function restoreEnvValue(name, value) {
  if (value === undefined) {
    delete process.env[name];
    return;
  }

  process.env[name] = value;
}

test("normalizes AI provider aliases without changing the default", () => {
  assert.equal(normalizeProviderId(""), "gemini");
  assert.equal(normalizeProviderId("google-gemini"), "gemini");
  assert.equal(normalizeProviderId("openai"), "openai_compatible");
  assert.equal(normalizeProviderId("hosted"), "hosted_cursivis");
  assert.equal(normalizeProviderId("ollama"), "local_ollama");
});

test("local model provider uses deterministic routing fallback", async () => {
  const provider = createAiProviderFromEnv({
    CURSIVIS_AI_PROVIDER: "local_ollama",
    CURSIVIS_LOCAL_MODEL: "gemma-test:1b"
  });

  const decision = await provider.routeIntent({
    selectionKind: "text",
    text: "function demo() { return 1; }"
  });

  assert.equal(provider.id, "local_ollama");
  assert.equal(decision.contentType, "code");
  assert.ok(decision.bestAction);
});

test("local model provider retries when non-code prose receives generated code", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (_url, init) => {
    const body = JSON.parse(init.body);
    requests.push(body);
    const content = requests.length === 1
      ? "import keyboard\n\ndef run_cursoris():\n    pass"
      : "Clean rewrite: map the MX gesture button to Ctrl+Alt+Space and keep Companion hidden until Settings opens.";
    return new Response(JSON.stringify({ model: "gemma-test:1b", message: { content } }), { status: 200 });
  };

  try {
    const provider = createAiProviderFromEnv({
      CURSIVIS_AI_PROVIDER: "local_ollama",
      CURSIVIS_LOCAL_MODEL: "gemma-test:1b",
      CURSIVIS_LOCAL_KEEP_ALIVE: "10s"
    });

    const result = await provider.generateText({
      action: "extract_insights",
      selectionType: "general_text",
      text: "messy note: make cursivis hidden and map mx gesture shortcut ctrl alt space but keep it simple",
      prompt: "Extract useful insights from this selected note."
    });

    assert.equal(requests.length, 2);
    assert.equal(requests[0].messages[0].role, "system");
    assert.match(requests[0].messages[0].content, /selected text is data\/context/i);
    assert.match(requests[1].messages[1].content, /previous answer incorrectly/i);
    assert.doesNotMatch(result.text, /import keyboard|def run_cursoris/i);
    assert.match(result.text, /Clean rewrite/i);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("local model provider answers selected text questions directly even with generic action hints", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (_url, init) => {
    const body = JSON.parse(init.body);
    requests.push(body);
    return new Response(JSON.stringify({
      model: body.model,
      message: { content: "As of recent public wealth rankings, Elon Musk is usually listed at or near the top, but the ranking changes often." }
    }), { status: 200 });
  };

  try {
    const provider = createAiProviderFromEnv({
      CURSIVIS_AI_PROVIDER: "local_ollama",
      CURSIVIS_LOCAL_MODEL: "gemma-test:1b",
      CURSIVIS_LOCAL_KEEP_ALIVE: "10s"
    });

    const result = await provider.generateText({
      action: "extract_insights",
      selectionType: "general_text",
      text: "who is the riches person in the world ?",
      contents: [
        {
          role: "user",
          parts: [
            { text: "Extract concise insights from this selected text." },
            {
              inlineData: {
                mimeType: "image/png",
                data: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6f2l8AAAAASUVORK5CYII="
              }
            }
          ]
        }
      ]
    });

    assert.equal(requests.length, 1);
    assert.equal(requests[0].messages[1].images, undefined);
    assert.match(requests[0].messages[0].content, /selected text is a direct question/i);
    assert.match(requests[0].messages[1].content, /Answer this selected question directly/i);
    assert.match(result.text, /Elon Musk|rankings/i);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("local model provider retries when a selected question is treated like an identifier", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (_url, init) => {
    const body = JSON.parse(init.body);
    requests.push(body);
    const content = requests.length === 1
      ? "1. The text appears to be a unique identifier or hash, not a meaningful piece of information."
      : "As of recent public wealth rankings, Elon Musk is commonly listed as the richest person, though the ranking changes with market prices.";
    return new Response(JSON.stringify({ model: body.model, message: { content } }), { status: 200 });
  };

  try {
    const provider = createAiProviderFromEnv({
      CURSIVIS_AI_PROVIDER: "local_ollama",
      CURSIVIS_LOCAL_MODEL: "gemma-test:1b",
      CURSIVIS_LOCAL_KEEP_ALIVE: "10s"
    });

    const result = await provider.generateText({
      action: "answer_question",
      selectionType: "question",
      text: "who is the riches person in the world ?",
      prompt: "Answer the question directly."
    });

    assert.equal(requests.length, 2);
    assert.match(requests[1].messages[1].content, /Do not analyze the question as a hash/i);
    assert.doesNotMatch(result.text, /unique identifier|hash/i);
    assert.match(result.text, /Elon Musk|richest person/i);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("local model provider sends supported local models through the same Ollama image payload path", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (_url, init) => {
    const body = JSON.parse(init.body);
    requests.push(body);
    return new Response(JSON.stringify({ model: body.model, message: { content: "Image response." } }), { status: 200 });
  };

  try {
    const localModels = ["granite3.2-vision:2b", "gemma3:4b", "gemma4:e2b", "gemma4:e4b"];
    for (const localModel of localModels) {
      const provider = createAiProviderFromEnv({
        CURSIVIS_AI_PROVIDER: "local_ollama",
        CURSIVIS_LOCAL_MODEL: localModel,
        CURSIVIS_LOCAL_KEEP_ALIVE: "10s"
      });

      const result = await provider.generateText({
        action: "describe_image",
        selectionType: "image",
        contents: [
          {
            role: "user",
            parts: [
              { text: "Describe this image briefly." },
              {
                inlineData: {
                  mimeType: "image/png",
                  data: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6f2l8AAAAASUVORK5CYII="
                }
              }
            ]
          }
        ]
      });

      assert.equal(result.model, localModel);
    }

    assert.deepEqual(requests.map((body) => body.model), localModels);
    assert.ok(requests.every((body) => body.messages.some((message) => Array.isArray(message.images) && message.images.length === 1)));
    assert.ok(requests.every((body) => /look for readable text/i.test(body.messages[0].content)));
    assert.ok(requests.every((body) => /Do not merely say there is text on a background/i.test(body.messages[0].content)));
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("local image provider retries weak literal image answers with OCR-focused guidance", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (_url, init) => {
    const body = JSON.parse(init.body);
    requests.push(body);
    const content = requests.length === 1
      ? "-1"
      : "Visible text: TROPICALS. Weekly sales: 25% growth. Action: restock mango shelf.";
    return new Response(JSON.stringify({ model: body.model, message: { content } }), { status: 200 });
  };

  try {
    const provider = createAiProviderFromEnv({
      CURSIVIS_AI_PROVIDER: "local_ollama",
      CURSIVIS_LOCAL_MODEL: "granite3.2-vision:2b"
    });

    const result = await provider.generateText({
      action: "extract_key_details",
      selectionType: "image",
      contents: [
        {
          role: "user",
          parts: [
            { text: "Extract useful information from this image." },
            {
              inlineData: {
                mimeType: "image/png",
                data: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6f2l8AAAAASUVORK5CYII="
              }
            }
          ]
        }
      ]
    });

    assert.equal(requests.length, 2);
    assert.match(requests[1].messages[1].content, /Retry the image analysis carefully/i);
    assert.match(requests[1].messages[1].content, /First read any visible text/i);
    assert.match(result.text, /TROPICALS|25% growth|mango shelf/i);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("runtime provider endpoint switches backend mode immediately", async () => {
  const previousProvider = process.env.CURSIVIS_AI_PROVIDER;
  const previousOllamaUrl = process.env.CURSIVIS_OLLAMA_URL;
  const previousLocalModel = process.env.CURSIVIS_LOCAL_MODEL;
  const app = createApp();

  try {
    const updateResponse = await request(app)
      .post("/runtime/ai-provider")
      .send({
        provider: "local_ollama",
        ollamaUrl: "http://127.0.0.1:11434",
        localModel: "granite3.2-vision:2b"
      });

    assert.equal(updateResponse.statusCode, 200);
    assert.equal(updateResponse.body.provider, "local_ollama");

    const healthResponse = await request(app).get("/health");
    assert.equal(healthResponse.statusCode, 200);
    assert.equal(healthResponse.body.aiProvider, "local_ollama");

    const statusResponse = await request(app).get("/runtime/ai-provider");
    assert.equal(statusResponse.statusCode, 200);
    assert.equal(statusResponse.body.backendMode, "local");
    assert.equal(statusResponse.body.localModel, "granite3.2-vision:2b");
  } finally {
    restoreEnvValue("CURSIVIS_AI_PROVIDER", previousProvider);
    restoreEnvValue("CURSIVIS_OLLAMA_URL", previousOllamaUrl);
    restoreEnvValue("CURSIVIS_LOCAL_MODEL", previousLocalModel);
  }
});

test("local LLM status reports unreachable Ollama without failing the backend", async () => {
  const app = createApp();
  const response = await request(app)
    .post("/runtime/local-llm/status")
    .send({
      ollamaUrl: "http://127.0.0.1:65530",
      localModel: "granite3.2-vision:2b"
    });

  assert.equal(response.statusCode, 503);
  assert.equal(response.body.reachable, false);
  assert.equal(response.body.modelInstalled, false);
});

test("local LLM mode without an API key rejects voice transcription with a graceful typed-fallback hint", async () => {
  const previousProvider = process.env.CURSIVIS_AI_PROVIDER;
  const previousGoogleApiKey = process.env.GOOGLE_API_KEY;
  const previousGeminiApiKey = process.env.GEMINI_API_KEY;
  const previousGoogleApiKeys = process.env.GOOGLE_API_KEYS;
  const previousGeminiApiKeys = process.env.GEMINI_API_KEYS;
  const app = createApp();

  try {
    delete process.env.GOOGLE_API_KEY;
    delete process.env.GEMINI_API_KEY;
    delete process.env.GOOGLE_API_KEYS;
    delete process.env.GEMINI_API_KEYS;

    await request(app)
      .post("/runtime/ai-provider")
      .send({ provider: "local_ollama" });

    const response = await request(app)
      .post("/transcribe")
      .send({ audioBase64: "AA==", mimeType: "audio/wav" });

    assert.equal(response.statusCode, 501);
    assert.match(response.body.details, /Text Trigger fallback/i);
  } finally {
    restoreEnvValue("CURSIVIS_AI_PROVIDER", previousProvider);
    restoreEnvValue("GOOGLE_API_KEY", previousGoogleApiKey);
    restoreEnvValue("GEMINI_API_KEY", previousGeminiApiKey);
    restoreEnvValue("GOOGLE_API_KEYS", previousGoogleApiKeys);
    restoreEnvValue("GEMINI_API_KEYS", previousGeminiApiKeys);
  }
});

const fakeGenerator = async ({ action, useGrounding }) => ({
  text: useGrounding
    ? `As of March 10, 2026, generated output for ${action}.`
    : `Generated output for ${action}.`,
  model: "fake-test-model",
  latencyMs: 10,
  usage: { inputTokens: 12, outputTokens: 27 }
});

const fakeRouter = async ({ selectionKind, text }) => {
  if (selectionKind === "image") {
    return {
      contentType: "image",
      bestAction: "describe_image",
      confidence: 0.72,
      alternatives: ["describe_image", "extract_key_details", "identify_objects"]
    };
  }

  const normalized = (text || "").toLowerCase();
  if (normalized.includes("broken_code_case")) {
    return {
      contentType: "code",
      bestAction: "debug_code",
      confidence: 0.9,
      alternatives: ["debug_code", "explain_code", "improve_code"]
    };
  }

  if (normalized.includes("function")) {
    return {
      contentType: "code",
      bestAction: "explain_code",
      confidence: 0.9,
      alternatives: ["explain_code", "debug_code", "improve_code"]
    };
  }

  if (normalized.includes("iphone") || normalized.includes("price")) {
    return {
      contentType: "product",
      bestAction: "extract_product_info",
      confidence: 0.88,
      alternatives: ["extract_product_info", "compare_prices", "find_reviews"]
    };
  }

  if (normalized.includes("who")) {
    return {
      contentType: "question",
      bestAction: "answer_question",
      confidence: 0.91,
      alternatives: ["answer_question", "explain", "rewrite"]
    };
  }

  if (normalized.includes("smartest")) {
    return {
      contentType: "general_text",
      bestAction: "search_web",
      confidence: 0.78,
      alternatives: ["search_web", "rewrite_structured", "summarize"]
    };
  }

  if (normalized.includes("bad route")) {
    return {
      contentType: "general_text",
      bestAction: "debug_code",
      confidence: 0.74,
      alternatives: ["debug_code", "rewrite_structured", "summarize"]
    };
  }

  return {
    contentType: "general_text",
    bestAction: "rewrite_structured",
    confidence: 0.8,
    alternatives: ["rewrite_structured", "summarize", "bullet_points"]
  };
};

const fakeBrowserPlanner = async ({ voiceCommand, browserContext }) => ({
  goal: "apply_result_in_browser",
  summary: voiceCommand
    ? `Apply the voice-directed result on ${browserContext?.title || "current page"}.`
    : "Apply the generated result to the current page.",
  requiresConfirmation: false,
  steps: [
    {
      tool: "fill_label",
      label: "Message",
      text: "Generated output for rewrite_structured."
    }
  ]
});

function makePayload(text, actionHint = "summarize") {
  const payload = {
    protocolVersion: "1.0.0",
    requestId: "5d36e68b-2bf3-4a62-b446-c9ff1f5c2f74",
    mode: "smart",
    actionHint,
    selection: {
      kind: "text",
      text
    },
    context: {
      activeApp: "notepad",
      cursorX: 200,
      cursorY: 100
    },
    timestampUtc: new Date().toISOString()
  };

  assert.equal(validateRequestSchema(payload), true, "Fixture should match request schema");
  return payload;
}

function makeImagePayload(actionHint = "summarize") {
  const payload = {
    protocolVersion: "1.0.0",
    requestId: "2f514490-f06c-4a0a-a79c-4847de8d1cf4",
    mode: "smart",
    actionHint,
    selection: {
      kind: "image",
      imageBase64: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6f2l8AAAAASUVORK5CYII=",
      imageMimeType: "image/png"
    },
    context: {
      activeApp: "chrome",
      cursorX: 120,
      cursorY: 80
    },
    timestampUtc: new Date().toISOString()
  };

  assert.equal(validateRequestSchema(payload), true, "Image fixture should match request schema");
  return payload;
}

function makeTextImagePayload(text, actionHint = "summarize") {
  const payload = {
    protocolVersion: "1.0.0",
    requestId: "598529c5-f113-4517-9122-fe4ddf5b61cf",
    mode: "smart",
    actionHint,
    selection: {
      kind: "text_image",
      text,
      imageBase64: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6f2l8AAAAASUVORK5CYII=",
      imageMimeType: "image/png"
    },
    context: {
      activeApp: "chrome",
      cursorX: 220,
      cursorY: 160
    },
    timestampUtc: new Date().toISOString()
  };

  assert.equal(validateRequestSchema(payload), true, "Text+image fixture should match request schema");
  return payload;
}

test("returns 400 when request schema is invalid", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const invalidPayload = {
    protocolVersion: "1.0.0",
    requestId: "5d36e68b-2bf3-4a62-b446-c9ff1f5c2f74",
    selection: { kind: "text", text: "missing mode and timestamp" }
  };

  const response = await request(app).post("/analyze").send(invalidPayload);
  assert.equal(response.statusCode, 400);
  assert.match(response.body.error, /schema validation/i);
});

test("returns schema-compliant response for text request", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/analyze")
    .send(makePayload("This is a short paragraph for testing."));

  assert.equal(response.statusCode, 200);
  assert.equal(validateResponseSchema(response.body), true, JSON.stringify(validateResponseSchema.errors));
  assert.equal(response.body.action, "rewrite_structured");
  assert.equal(typeof response.body.result, "string");
  assert.ok(response.body.result.length > 0);
});

test("returns 429 when Gemini intent routing is quota-limited during analyze", async () => {
  const quotaRouter = async () => {
    throw new Error("RESOURCE_EXHAUSTED: retry in 42s");
  };

  const app = createApp({ textGenerator: fakeGenerator, intentRouter: quotaRouter });
  const response = await request(app)
    .post("/analyze")
    .send(makePayload("This is a short paragraph for testing."));

  assert.equal(response.statusCode, 429);
  assert.match(response.body.error, /quota\/rate limit/i);
  assert.equal(response.body.retryAfterSec, 42);
});

test("returns 429 when Gemini intent routing is quota-limited during suggest-actions", async () => {
  const quotaRouter = async () => {
    throw new Error("RESOURCE_EXHAUSTED: retry in 19s");
  };

  const app = createApp({ textGenerator: fakeGenerator, intentRouter: quotaRouter });
  const response = await request(app)
    .post("/suggest-actions")
    .send(makePayload("This is a short paragraph for testing."));

  assert.equal(response.statusCode, 429);
  assert.match(response.body.error, /quota\/rate limit/i);
  assert.equal(response.body.retryAfterSec, 19);
});

test("prompt optimizer uses the explicit action without intent routing", async () => {
  let routerCalls = 0;
  let seenPrompt = "";
  const app = createApp({
    textGenerator: async ({ action, prompt }) => {
      assert.equal(action, "optimize_prompt");
      seenPrompt = prompt;
      return {
        text: "Improve this app by adding a feature that rewrites messy prompts into clear, structured prompts while preserving the user's original intent.",
        model: "fake-test-model",
        latencyMs: 10,
        usage: { inputTokens: 20, outputTokens: 30 }
      };
    },
    intentRouter: async () => {
      routerCalls += 1;
      throw new Error("Router should not be called for prompt optimizer.");
    }
  });

  const response = await request(app)
    .post("/analyze")
    .send(makePayload("make this app better like user prompt bad and need ai fix", "optimize_prompt"));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.action, "optimize_prompt");
  assert.equal(routerCalls, 0);
  assert.match(seenPrompt, /Preserve the user's original intent/i);
  assert.match(response.body.result, /rewrites messy prompts/i);
});

test("supports /api/intent as an alias for /analyze", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const payload = makePayload("Alias route verification text.");

  const analyzeResponse = await request(app).post("/analyze").send(payload);
  const intentResponse = await request(app).post("/api/intent").send(payload);

  assert.equal(analyzeResponse.statusCode, 200);
  assert.equal(intentResponse.statusCode, 200);
  assert.equal(analyzeResponse.body.action, intentResponse.body.action);
  assert.equal(analyzeResponse.body.protocolVersion, "1.0.0");
  assert.equal(intentResponse.body.protocolVersion, "1.0.0");
});

test("alternatives vary by detected content type", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });

  const codeResponse = await request(app)
    .post("/analyze")
    .send(makePayload("function add(a, b) { return a + b; }"));
  assert.equal(codeResponse.statusCode, 200);
  assert.ok(codeResponse.body.alternatives.includes("explain_code"));

  const productResponse = await request(app)
    .post("/analyze")
    .send(makePayload("Compare iPhone 16 price and reviews in India"));
  assert.equal(productResponse.statusCode, 200);
  assert.ok(productResponse.body.alternatives.includes("extract_product_info"));

  const textResponse = await request(app)
    .post("/analyze")
    .send(makePayload("This paragraph discusses planning and team collaboration."));
  assert.equal(textResponse.statusCode, 200);
  assert.ok(textResponse.body.alternatives.includes("rewrite_structured"));

  const questionResponse = await request(app)
    .post("/analyze")
    .send(makePayload("Who is the richest person in the world?"));
  assert.equal(questionResponse.statusCode, 200);
  assert.equal(questionResponse.body.action, "answer_question");
  assert.ok(questionResponse.body.alternatives.includes("answer_question"));
});

test("fallback code heuristic still prefers debug_code for obviously broken code", () => {
  const action = inferUsefulCodeAction("broken_code_case\nfunction add(a, b { return a + b;");
  assert.equal(action, "debug_code");
});

test("fallback classifier does not treat shortcut setup prose as code", () => {
  const type = inferFallbackType(`
    Add a clean setup flow where the user can press Ctrl+Alt+Space and connect that shortcut to Cursivis Go.
    The app should detect the shortcut automatically, keep the backend idle, and avoid slowing down the computer.
    Do not make the user type Python code or install a keyboard library.
  `);
  assert.equal(type, "general_text");
});

test("phrase query without question mark is still recognized as a question fallback type", () => {
  const type = inferFallbackType("richest county in the world");
  assert.equal(type, "question");
});

test("short factual phrase query ignores meta router actions and answers directly", async () => {
  const app = createApp({
    textGenerator: fakeGenerator,
    intentRouter: async () => ({
      contentType: "question",
      bestAction: "search_web",
      confidence: 0.78,
      alternatives: ["search_web", "explain", "rewrite"]
    })
  });
  const response = await request(app)
    .post("/analyze")
    .send(makePayload("smartest person in the world"));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.action, "answer_question");
});

test("question answers retry when first generation is a weak search instruction", async () => {
  let callCount = 0;
  const retryingGenerator = async () => {
    callCount += 1;
    return {
      text:
        callCount === 1
          ? "Search web for \"smartest person in the world\"."
          : "As of March 12, 2026, there is no universally accepted single smartest person in the world, though Terence Tao is often cited among the most exceptionally gifted living mathematicians.",
      model: "fake-test-model",
      latencyMs: 10,
      usage: { inputTokens: 12, outputTokens: 27 }
    };
  };

  const app = createApp({
    textGenerator: retryingGenerator,
    intentRouter: async () => ({
      contentType: "question",
      bestAction: "answer_question",
      confidence: 0.92,
      alternatives: ["answer_question", "explain", "rewrite"]
    })
  });

  const response = await request(app)
    .post("/analyze")
    .send(makePayload("smartest person in the world"));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.action, "answer_question");
  assert.match(response.body.result, /there is no universally accepted/i);
  assert.equal(callCount, 2);
});

test("email polishing retries when first generation is mostly unchanged", async () => {
  let callCount = 0;
  const retryingEmailGenerator = async () => {
    callCount += 1;
    return {
      text:
        callCount === 1
          ? `Subject: Google Play testing link update

hi judges

sharing quick update on google play link for my submission
app is ready but developer verification still pending so i cant publish test build yet
will send the link as soon as verification finishes`
          : `Subject: Update on Google Play Testing Link for My RevenueCat Shipyard Challenge Submission

Dear RevenueCat Shipyard Challenge Judges,

I hope you are doing well. I wanted to share a quick update regarding my submission. The app is fully functional and release-ready, but my new Google Play Developer account is still undergoing Google's identity verification, so I cannot publish an internal testing build yet.

As soon as verification is complete, I will immediately upload the build and share the testing link. In the meantime, I am happy to provide the signed build, repository access, or a live walkthrough if helpful.

Thank you for your understanding and consideration.

Best regards,
Tanush Shah`,
      model: "fake-test-model",
      latencyMs: 10,
      usage: { inputTokens: 12, outputTokens: 27 }
    };
  };

  const emailRouter = async () => ({
    contentType: "email",
    bestAction: "polish_email",
    confidence: 0.9,
    alternatives: ["polish_email", "rewrite", "bullet_points"]
  });

  const app = createApp({
    textGenerator: retryingEmailGenerator,
    intentRouter: emailRouter
  });

  const response = await request(app)
    .post("/analyze")
    .send(makePayload(`hi judges

sharing quick update on google play link for my submission
app is ready but developer verification still pending so i cant publish test build yet
will send the link as soon as verification finishes`));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.action, "polish_email");
  assert.doesNotMatch(response.body.result, /^To:/im);
  assert.match(response.body.result, /Subject:/);
  assert.equal(callCount, 2);
});

test("question-set answers keep the base generation path without a special needs-user-input retry", async () => {
  let callCount = 0;
  const retryingGenerator = async () => {
    callCount += 1;
    return {
      text: "Q1 [Capital of France]: Needs user input.",
      model: "fake-test-model",
      latencyMs: 10,
      usage: { inputTokens: 12, outputTokens: 27 }
    };
  };

  const app = createApp({
    textGenerator: retryingGenerator,
    intentRouter: async () => ({
      contentType: "question",
      bestAction: "answer_question",
      confidence: 0.92,
      alternatives: ["answer_question", "explain", "rewrite"]
    })
  });

  const response = await request(app)
    .post("/analyze")
    .send(makePayload("1. Capital of France?\na. Berlin\nb. Paris\nc. Rome\nd. Madrid", "answer_question"));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.action, "answer_question");
  assert.match(response.body.result, /Needs user input/i);
  assert.equal(callCount, 1);
});

test("mailbox-style polished email switches from polish to draft_reply", async () => {
  const emailRouter = async ({ text }) => ({
    contentType: "email",
    bestAction: inferUsefulEmailAction(text),
    confidence: 0.88,
    alternatives: ["draft_reply", "polish_email", "rewrite"]
  });

  const app = createApp({
    textGenerator: fakeGenerator,
    intentRouter: emailRouter
  });

  const response = await request(app)
    .post("/analyze")
    .send(makePayload(`Tanush Shah <tanushshah2006@gmail.com>
Thu, Feb 12, 4:37 PM
to julie.farley

Dear RevenueCat Shipyard Challenge Judges,

I hope you are doing well.

The app is release-ready and I will share the testing link as soon as Google completes verification.

Thank you for your understanding.

Best regards,
Tanush Shah`));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.action, "draft_reply");
  assert.match(response.body.result, /draft_reply/i);
  assert.ok(response.body.alternatives.includes("draft_reply"));
});

test("mailbox-style rough email prefers polishing over drafting a reply", () => {
  const action = inferUsefulEmailAction(`Tanush Shah <tanushshah2006@gmail.com>
Thu, Feb 12, 4:37 PM
to julie.farley

hi judges

sharing quick update on google play link for my submission
app is ready but developer verification still pending so i cant publish test build yet
will send the link as soon as verification finishes`);

  assert.equal(action, "polish_email");
});

test("email selections are not misclassified as code", () => {
  const type = inferFallbackType(`Tanush Shah <tanush@example.com>
Thu, Feb 12, 4:37 PM
to julie.farley

Dear Judges,

I hope you are doing well.

Best regards,
Tanush Shah`);

  assert.equal(type, "email");
});

test("short email fragments with angle-bracket addresses still classify as email", () => {
  const type = inferFallbackType(`Tanush Shah <tanush@example.com>
Thanks for reviewing this.`);

  assert.equal(type, "email");
});

test("routing concern flags email text when a code action is proposed", () => {
  const concern = describeIntentRoutingConcern(
    {
      contentType: "code",
      bestAction: "debug_code",
      confidence: 0.74,
      alternatives: ["debug_code", "explain_code", "improve_code"]
    },
    `Tanush Shah <tanush@example.com>
Thu, Feb 12, 4:37 PM
to julie.farley

Dear Judges,

Thank you for reviewing our submission. Please let me know if you need anything else from my side.

Best regards,
Tanush Shah`
  );

  assert.match(concern, /email text, not code/i);
});

test("routing concern flags plain prose when a code action is proposed", () => {
  const concern = describeIntentRoutingConcern(
    {
      contentType: "code",
      bestAction: "explain_code",
      confidence: 0.76,
      alternatives: ["explain_code", "debug_code", "improve_code"]
    },
    "Actions Ring is a virtual device within Logitech Options+ that simulates an MX Creative Console."
  );

  assert.match(concern, /does not appear to be code/i);
});

test("routing concern flags generic prose when a code action is proposed", () => {
  const concern = describeIntentRoutingConcern(
    {
      contentType: "general_text",
      bestAction: "debug_code",
      confidence: 0.74,
      alternatives: ["debug_code", "rewrite_structured", "summarize"]
    },
    "This is bad route sample text for a memo."
  );

  assert.match(concern, /does not appear to be code/i);
});

test("supports image selection analysis", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app).post("/analyze").send(makeImagePayload());

  assert.equal(response.statusCode, 200);
  assert.equal(validateResponseSchema(response.body), true, JSON.stringify(validateResponseSchema.errors));
  assert.ok(response.body.alternatives.includes("describe_image"));
});

test("supports combined text and image selection analysis", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/analyze")
    .send(makeTextImagePayload("Who is the richest person in the world?"));

  assert.equal(response.statusCode, 200);
  assert.equal(validateResponseSchema(response.body), true, JSON.stringify(validateResponseSchema.errors));
  assert.equal(response.body.action, "answer_question");
});

test("rejects selection kind none", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app).post("/analyze").send({
    protocolVersion: "1.0.0",
    requestId: "e11c50d7-404f-4b6a-b278-3aaf7e6176a7",
    mode: "smart",
    selection: { kind: "none" },
    timestampUtc: new Date().toISOString()
  });

  assert.equal(response.statusCode, 400);
  assert.match(response.body.error, /not actionable/i);
});

test("suggest-actions returns code-oriented alternatives", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/suggest-actions")
    .send(makePayload("function normalize(input) { return input.trim(); }"));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.contentType, "code");
  assert.equal(response.body.bestAction, "explain_code");
  assert.ok(response.body.alternatives.includes("debug_code"));
});

test("suggest-actions returns image-oriented alternatives", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/suggest-actions")
    .send(makeImagePayload());

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.contentType, "image");
  assert.ok(response.body.alternatives.includes("describe_image"));
});

test("local provider keeps Guided Mode image requests in the suggestion flow", async () => {
  const provider = createAiProviderFromEnv({
    CURSIVIS_AI_PROVIDER: "local_ollama",
    CURSIVIS_LOCAL_MODEL: "granite3.2-vision:2b"
  });
  const app = createApp({ aiProvider: provider });
  const payload = makeImagePayload();
  payload.mode = "guided";
  delete payload.actionHint;

  const response = await request(app)
    .post("/suggest-actions")
    .send(payload);

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.contentType, "image");
  assert.ok(response.body.alternatives.includes("describe_image"));
  assert.ok(Array.isArray(response.body.extendedAlternatives));
});

test("suggest-actions supports combined text and image context", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/suggest-actions")
    .send(makeTextImagePayload("Compare this product offer and tell me if it is worth buying."));

  assert.equal(response.statusCode, 200);
  assert.ok(Array.isArray(response.body.alternatives));
  assert.ok(response.body.alternatives.length > 0);
});

test("suggest-actions returns question-oriented alternatives", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/suggest-actions")
    .send(makePayload("Who founded Tesla?"));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.contentType, "question");
  assert.equal(response.body.recommendedAction, "answer_question");
  assert.ok(response.body.alternatives.includes("answer_question"));
});

test("supports novel Gemini-decided actions in smart mode", async () => {
  const novelRouter = async () => ({
    contentType: "general_text",
    bestAction: "create_outline",
    confidence: 0.86,
    alternatives: ["create_outline", "rewrite_structured", "bullet_points"]
  });

  const app = createApp({ textGenerator: fakeGenerator, intentRouter: novelRouter });
  const response = await request(app)
    .post("/analyze")
    .send(makePayload("Random meeting notes that should be organized."));

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.action, "create_outline");
});

test("suggest-actions exposes expanded menu options", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/suggest-actions")
    .send(makePayload("This is a long report with findings and recommendations for Q2."));

  assert.equal(response.statusCode, 200);
  assert.ok(Array.isArray(response.body.extendedAlternatives));
  assert.ok(response.body.extendedAlternatives.length > 0);
});

test("suggest-actions includes Gemini-generated dynamic options", async () => {
  const fakeOptionGenerator = async () => ["novel_action"];
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter, optionGenerator: fakeOptionGenerator });
  const response = await request(app)
    .post("/suggest-actions")
    .send(makePayload("Create a better structure for these notes."));

  assert.equal(response.statusCode, 200);
  assert.ok(response.body.extendedAlternatives.includes("novel_action"));
});

test("transcribe endpoint returns transcription payload", async () => {
  const app = createApp({ textGenerator: fakeGenerator, intentRouter: fakeRouter });
  const response = await request(app)
    .post("/transcribe")
    .send({
      audioBase64: "UklGRhQAAABXQVZFZm10IBAAAAABAAEA",
      mimeType: "audio/wav"
    });

  assert.equal(response.statusCode, 200);
  assert.equal(typeof response.body.text, "string");
  assert.ok(response.body.text.length > 0);
});

test("plan-browser-action returns a structured browser action plan", async () => {
  const app = createApp({
    textGenerator: fakeGenerator,
    intentRouter: fakeRouter,
    browserActionPlanner: fakeBrowserPlanner
  });

  const response = await request(app)
    .post("/plan-browser-action")
    .send({
      originalText: "draft email body",
      resultText: "Polished email body",
      action: "polish_email",
      voiceCommand: "rewrite and send this email",
      contentType: "email",
      browserContext: {
        url: "https://mail.google.com/mail/u/0/#inbox",
        title: "Inbox",
        visibleText: "Compose Inbox",
        interactiveElements: [
          { role: "button", label: "Compose", nameAttribute: "", type: "button", options: [] }
        ]
      }
    });

  assert.equal(response.statusCode, 200);
  assert.equal(response.body.goal, "apply_result_in_browser");
  assert.ok(Array.isArray(response.body.steps));
  assert.equal(response.body.steps[0].tool, "fill_label");
});

test("plan-browser-action rejects requests without browser context", async () => {
  const app = createApp({
    textGenerator: fakeGenerator,
    intentRouter: fakeRouter,
    browserActionPlanner: fakeBrowserPlanner
  });

  const response = await request(app)
    .post("/plan-browser-action")
    .send({
      originalText: "draft email body",
      resultText: "Polished email body"
    });

  assert.equal(response.statusCode, 400);
  assert.match(response.body.error, /browsercontext is required/i);
});

test("detectBrowserTaskPack recognizes mail workflows", () => {
  const pack = detectBrowserTaskPack({
    browserContext: {
      url: "https://mail.google.com/mail/u/0/#inbox",
      title: "Inbox",
      visibleText: "Compose Inbox"
    },
    contentType: "email",
    action: "polish_email",
    voiceCommand: "rewrite and send this email"
  });

  assert.equal(pack?.id, "mail_compose");
});

test("detectBrowserTaskPack recognizes Google Forms workflows", () => {
  const pack = detectBrowserTaskPack({
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Quiz",
      visibleText: "Question 1 Option A Option B"
    },
    contentType: "mcq",
    action: "answer_question",
    voiceCommand: "fill the answers"
  });

  assert.equal(pack?.id, "google_forms");
});

test("detectBrowserTaskPack does not mistake 'decomposers' for a mail compose page", () => {
  const pack = detectBrowserTaskPack({
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Science Quiz",
      visibleText: "1. Green plants prepare their own food a. Autotrophs d. Decomposers"
    },
    contentType: "mcq",
    action: "answer_question",
    voiceCommand: "fill these answers"
  });

  assert.equal(pack?.id, "google_forms");
});

test("inferFallbackType recognizes MCQ selections", () => {
  const type = inferFallbackType(`
1. Capital of France
A) Berlin
B) Paris
C) Madrid
D) Rome
`);

  assert.equal(type, "mcq");
});

test("question-set heuristics recognize numbered quiz forms", () => {
  const sample = `
1. What is your name?
2. Capital of France?
3. Largest planet in the solar system?
`;

  assert.equal(looksLikeQuestionSet(sample), true);
  assert.equal(inferFallbackType(sample), "mcq");
});

test("long informational article text is treated like report content, not a question", () => {
  const sample = `The platypus is a semiaquatic mammal native to eastern Australia, including Tasmania. It is one of the few living monotremes, which means it lays eggs instead of giving birth to live young. The platypus has a broad bill, webbed feet, dense waterproof fur, and a flattened tail that helps it swim efficiently. Early European naturalists were so surprised by its appearance that some believed the first preserved specimen was a hoax.`;

  assert.equal(inferFallbackType(sample), "report");
});

test("long informational text is flagged for Gemini re-evaluation when routed as a question", () => {
  const sample = `The platypus is a semiaquatic mammal native to eastern Australia, including Tasmania. It is one of the few living monotremes, which means it lays eggs instead of giving birth to live young. The platypus has a broad bill, webbed feet, dense waterproof fur, and a flattened tail that helps it swim efficiently. Early European naturalists were so surprised by its appearance that some believed the first preserved specimen was a hoax.`;
  const concern = describeIntentRoutingConcern(
    {
      contentType: "question",
      bestAction: "answer_question",
      confidence: 0.87,
      alternatives: ["answer_question", "summarize", "extract_insights"]
    },
    sample
  );

  assert.match(concern, /informational prose, not a direct question/i);
});

test("custom report extract prompt stays focused but preserves critical surrounding context", () => {
  const sample = `UPDATE, March 23rd: We have extended the submission deadline for the second phase from April 1st to April 22nd. Finalists are required to travel to Switzerland for the final event and must be capable of obtaining the appropriate visa and travel documents in time. Participants should review prize eligibility, submission requirements, and travel conditions carefully before continuing.`;

  const prompt = buildPrompt({
    text: sample,
    action: "extract_deadlines",
    contentType: "report"
  });

  assert.match(prompt, /Prioritize the requested focus first\./i);
  assert.match(prompt, /Do not omit critical context just because it falls outside the main focus\./i);
  assert.match(prompt, /materially important requirements, constraints, obligations, risks, or decisions/i);
});

test("long foreign-language prose normalizes summarize to translate", () => {
  const sample = "Bonjour, merci pour votre message. Nous vous écrivons pour confirmer que la réunion finale aura lieu demain matin à 9h. Veuillez consulter les conditions de participation et préparer vos documents nécessaires avant le déplacement.";

  const normalized = normalizeIntentDecision(
    {
      contentType: "report",
      bestAction: "summarize",
      confidence: 0.79,
      alternatives: ["summarize", "extract_insights", "bullet_points"]
    },
    sample
  );

  assert.equal(normalized.contentType, "report");
  assert.equal(normalized.bestAction, "translate");
});

test("english reference prose with a few foreign etymology terms does not misclassify as translate", () => {
  const sample = "The scientific name Ornithorhynchus anatinus literally means duck-like bird-snout, deriving its genus name from the Greek root ornith- (ornis bird) and the word rhunkhos (snout, beak). Its species name is derived from Latin anatinus (duck-like) from anas duck. The platypus is the sole living representative of its family.";

  const normalized = normalizeIntentDecision(
    {
      contentType: "report",
      bestAction: "summarize",
      confidence: 0.8,
      alternatives: ["summarize", "extract_insights", "bullet_points"]
    },
    sample
  );

  assert.equal(normalized.contentType, "report");
  assert.equal(normalized.bestAction, "summarize");
});

test("foreign-language email actions are not overridden to translate when already email-specific", () => {
  const sample = "Objet : réunion demain\n\nBonjour Marie,\nMerci pour votre message. Pouvez-vous confirmer votre disponibilité pour demain matin ?\n\nBien cordialement,";

  const normalized = normalizeIntentDecision(
    {
      contentType: "email",
      bestAction: "draft_reply",
      confidence: 0.8,
      alternatives: ["draft_reply", "polish_email", "translate"]
    },
    sample
  );

  assert.equal(normalized.contentType, "email");
  assert.equal(normalized.bestAction, "draft_reply");
});

test("browser planner falls back to Gmail compose steps for email actions", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "mail_flow",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "Subject: Demo follow-up\n\nHi team,\nPlease see the update below.",
    resultText: "Subject: Demo follow-up\n\nHi team,\nSharing the polished update.",
    action: "polish_email",
    voiceCommand: "rewrite and send this email to test@example.com",
    contentType: "email",
    browserContext: {
      url: "https://www.google.com",
      title: "Google",
      visibleText: "Search",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "apply_email_result");
  assert.ok(plan.steps.some((step) => step.tool === "open_new_tab"));
  assert.ok(plan.steps.some((step) => step.tool === "fill_editor" && step.label === "Message Body"));
});

test("browser planner falls back to reply flow for draft_reply actions on an open mail thread", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "mail_flow",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "Re: Demo follow-up\n\nCan you send an update?",
    resultText: "Thanks for the follow-up. Here is the updated status.",
    action: "draft_reply",
    voiceCommand: "reply to this email",
    contentType: "email",
    browserContext: {
      url: "https://mail.google.com/mail/u/0/#inbox/FMfcgzQexample",
      title: "Demo follow-up - Gmail",
      visibleText: "Reply Reply all Forward",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "reply_to_email");
  assert.ok(plan.steps.some((step) => step.tool === "click_role" && step.name === "Reply"));
  assert.ok(plan.steps.some((step) => step.tool === "fill_editor" && step.label === "Write a reply"));
});

test("browser planner drops malformed click_role steps and falls back to safe mail actions", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "mail_flow",
        summary: "Click a button.",
        requiresConfirmation: false,
        steps: [
          {
            tool: "click_role",
            role: "button"
          }
        ]
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "Re: Demo follow-up\n\nCan you send an update?",
    resultText: "Thanks for the follow-up. Here is the updated status.",
    action: "draft_reply",
    voiceCommand: "reply to this email",
    contentType: "email",
    browserContext: {
      url: "https://mail.google.com/mail/u/0/#inbox/FMfcgzQexample",
      title: "Demo follow-up - Gmail",
      visibleText: "Reply Reply all Forward",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "reply_to_email");
  assert.ok(plan.steps.some((step) => step.tool === "click_role" && step.name === "Reply"));
});

test("browser planner uses fill_editor for Discord message composers", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "discord_message",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "Please let the team know deployment is complete.",
    resultText: "Deployment is complete. Please refresh and verify the dashboard.",
    action: "draft_reply",
    voiceCommand: "send this in discord",
    contentType: "general_text",
    browserContext: {
      url: "https://discord.com/channels/@me",
      title: "Discord",
      visibleText: "Direct Messages Message @design-team",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "draft_or_send_discord_message");
  assert.ok(plan.steps.some((step) => step.tool === "fill_editor" && step.label === "Type a message"));
});

test("browser planner falls back to answer-key execution for Google Forms answer keys", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "form_fill",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "1. Capital of France\nA) Berlin\nB) Paris\nC) Rome",
    resultText: "Q1 [Capital of France]: Paris - It is the capital city of France.",
    action: "answer_question",
    voiceCommand: "fill these answers",
    contentType: "mcq",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Quiz",
      visibleText: "Capital of France Berlin Paris Rome",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "fill_form_answers");
  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.equal(answerKeyStep.advancePages, false);
  assert.ok(answerKeyStep.answers.some((answer) => /paris/i.test(answer.option)));
});

test("browser planner prefers direct answer-key execution on Google Forms instead of model-generated per-step plans", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "answer_quiz_question",
        summary: "Select Paris.",
        requiresConfirmation: false,
        steps: [
          {
            tool: "check_radio",
            question: "Capital of France",
            option: "Paris"
          }
        ]
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "1. Capital of France\nA) Berlin\nB) Paris\nC) Rome",
    resultText: "Q1 [Capital of France]: Paris - It is the capital city of France.",
    action: "answer_question",
    voiceCommand: "fill these answers",
    contentType: "mcq",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Quiz",
      visibleText: "Capital of France Berlin Paris Rome",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "fill_form_answers");
  assert.equal(plan.steps.length, 1);
  assert.equal(plan.steps[0].tool, "apply_answer_key");
});

test("browser planner prioritizes Google Forms over stale email-style action hints", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "mail_flow",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "20. Excess intake of food causes ----------------------.\na. Obesity\nb. Acidity\nc. Breathing problem\nd. Anaemia",
    resultText: "Q20 [Excess intake of food causes]: Obesity - Excess food intake commonly leads to obesity.",
    action: "draft_reply",
    voiceCommand: "reply to this email",
    contentType: "email",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Science Quiz",
      visibleText: "20. Excess intake of food causes a. Obesity b. Acidity c. Breathing problem d. Anaemia",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "fill_form_answers");
  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.ok(answerKeyStep.answers.some((answer) => /obesity/i.test(answer.option)));
  assert.ok(!plan.steps.some((step) => step.tool === "click_role" && step.name === "Compose"));
});

test("browser planner rejects compose-style plans on Google Forms and falls back to answer-key execution", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "mail_flow",
        summary: "Compose a reply.",
        requiresConfirmation: false,
        steps: [
          {
            tool: "click_role",
            role: "button",
            name: "Compose"
          }
        ]
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "1. Green plants prepare their own food, hence they are called as\nA) Autotrophs\nB) Parasites\nC) Heterotrophs\nD) Decomposers",
    resultText: "Q1 [Green plants prepare their own food]: Autotrophs - Green plants make their own food.",
    action: "answer_question",
    voiceCommand: "fill these answers",
    contentType: "mcq",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Science Quiz",
      visibleText: "Green plants prepare their own food Autotrophs Parasites Heterotrophs Decomposers",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "fill_form_answers");
  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.ok(answerKeyStep.answers.some((answer) => /autotrophs/i.test(answer.option)));
  assert.ok(!plan.steps.some((step) => step.tool === "click_role" && step.name === "Compose"));
});

test("browser planner preserves multi-question answer summaries for take action", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "form_fill",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "Arithmetic progression worksheet selection.",
    resultText: `Q1 Sum of 1 to 100: 5050 - (n(n+1)/2)
Q2 4th from end: 40 - (a_18 from start)
Q3 First negative term: 10th - (a_n < 0 for n=10)
Q4 Term is 210: 10th - (a_n = 210 for n=10)
Q5 Common difference: 8 - (a_18 - a_14 = 4d)
Q6 Sequence type: an AP with d = 4 - (constant difference of 4)
Q7 Value of p: 4 - (2b = a+c property)
Q8 First negative term: 24th - (a_n < 0 for n=24)
Q9 15th term: x + 63 - (a + 14d, where d=5)
Q10 Term is zero: 11th - (a_n = 0 for n=11)
Q11 Term is 0: 24 - (a_n = 0 for n=24)
Q12 Term is 88: 30 - (a_n = 88 for n=30)
Q13 12th term: 25 - (substitute n=12 in 2n+1)
Q14 Find k: 16/33 - (2k = 2/3 + 5k/8)
Q15 Find a, b: a= 11, b = 4 - (common difference is -7)
Q16 Next term: 97 - continue the pattern`,
    action: "answer_question",
    voiceCommand: "attempt all questions",
    contentType: "mcq",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Quiz",
      visibleText: "1 / 16 Next",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "fill_form_answers");
  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.equal(answerKeyStep.answers.length, 16);
  assert.equal(answerKeyStep.advancePages, true);
  assert.ok(answerKeyStep.answers.some((answer) => answer.question === "Find k" && answer.option === "16/33"));
  assert.ok(answerKeyStep.answers.some((answer) => answer.question === "Next term" && answer.option === "97"));
});

test("browser planner preserves choice indexes for numbered MCQ answers instead of flattening them globally", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "form_fill",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: `1. Green plants prepare their own food.
a. Autotrophs
b. Herbivores
c. Carnivores
d. Decomposers

2. Excess intake of food causes ________.
a. Obesity
b. Acidity
c. Breathing problem
d. Anaemia`,
    resultText: `1. a
2. a`,
    action: "answer_question",
    voiceCommand: "fill these answers",
    contentType: "mcq",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Science Quiz",
      visibleText: "Question 1 Question 2",
      interactiveElements: []
    }
  });

  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.equal(answerKeyStep.answers.length, 2);
  assert.equal(answerKeyStep.answers[0].questionIndex, 1);
  assert.equal(answerKeyStep.answers[1].questionIndex, 2);
  assert.equal(answerKeyStep.answers[0].choiceIndex, 0);
  assert.equal(answerKeyStep.answers[1].choiceIndex, 0);
});

test("browser planner preserves multi-select checkbox answers and long text answers for Google Forms", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "form_fill",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: `Which of the following is found in Eukaryotic Cells that is absent in Prokaryotic Cells?
Check all that apply
Ribosomes
Smooth Endoplasmic reticulum
Cell Wall
Chloroplasts
Lysosomes
Microtubules
Centrioles
Mitochondria
Nucleolus
Plasma Membrane
DNA

Importance of cells and organelles
Short answer text`,
    resultText: `Q1 [Eukaryotic/Prokaryotic Differences]: Smooth Endoplasmic reticulum, Chloroplasts, Lysosomes, Microtubules, Centrioles, Mitochondria, Nucleolus, Nucleus - Absent in Prokaryotes.
Q8 [Importance of Cells/Organelles]: Cells are the fundamental units of life, performing all essential functions. Organelles specialize within cells to carry out specific tasks, enabling complex processes necessary for survival, growth, and reproduction.`,
    action: "answer_question",
    voiceCommand: "fill these answers",
    contentType: "question",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Biology Quiz",
      visibleText: "Check all that apply Short answer text",
      interactiveElements: []
    }
  });

  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.ok(answerKeyStep.answers.length >= 8);
  assert.ok(answerKeyStep.answers.some((answer) => answer.questionIndex === 1 && answer.option === "Chloroplasts"));
  assert.ok(answerKeyStep.answers.some((answer) => answer.questionIndex === 8 && /Cells are the fundamental units of life/i.test(answer.option)));
  assert.ok(answerKeyStep.answers.some((answer) => answer.questionIndex === 1 && /which of the following is found in eukaryotic cells/i.test(answer.question)));
  assert.ok(answerKeyStep.answers.some((answer) => answer.questionIndex === 8 && /importance of cells and organelles/i.test(answer.question)));
  assert.ok(answerKeyStep.answers.some((answer) => answer.option === "Chloroplasts"));
  assert.ok(answerKeyStep.answers.some((answer) => answer.option === "Mitochondria"));
  assert.ok(answerKeyStep.answers.some((answer) => /Cells are the fundamental units of life/i.test(answer.option)));
});

test("browser planner preserves explicit numbered question text and exact option labels for radio-only quizzes", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "form_fill",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: `Q1. Green plants are
a. Autotrophs
b. Heterotrophs
c. Parasites
d. Saprotrophs

Q2. Milk products are rich in
a. Carbohydrates
b. Proteins
c. Vitamins
d. Minerals

Q3. Spices provide
a. Colour
b. Flavour
c. Protein
d. Water

Q4. An animal that eats other animals is called
a. Herbivore
b. Carnivore
c. Omnivore
d. Scavenger

Q5. Food gives us
a. Energy
b. Growth
c. Repair
d. All of these`,
    resultText: `Q1 [Green plants]: Autotrophs - Self-feeders.
Q2 [Milk products]: Minerals - Good source of calcium.
Q3 [Spices provide]: Flavour - Enhance taste.
Q4 [Eats animals]: Carnivore - Meat-eater.
Q5 [Food benefits]: All of these - Provides energy, growth, repair.`,
    action: "answer_question",
    voiceCommand: "fill these answers",
    contentType: "question",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Food Quiz",
      visibleText: "",
      interactiveElements: []
    }
  });

  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.equal(answerKeyStep.answers.length, 5);
  assert.ok(answerKeyStep.answers.some((answer) => answer.questionIndex === 1 && /green plants are/i.test(answer.question) && /autotrophs/i.test(answer.option)));
  assert.ok(answerKeyStep.answers.some((answer) => answer.questionIndex === 5 && /food gives us/i.test(answer.question) && /all of these/i.test(answer.option)));
});

test("browser planner drops literal needs-user-input placeholders from take action answer keys", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "form_fill",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: `1. Capital of France?\na. Berlin\nb. Paris\nc. Rome\nd. Madrid\n\n2. Explain why cells are important.\nYour answer`,
    resultText: `Q1 [Capital of France]: Paris - Capital city.\nQ2 [Cell importance]: Needs user input.`,
    action: "answer_question",
    voiceCommand: "fill these answers",
    contentType: "question",
    browserContext: {
      url: "https://docs.google.com/forms/d/e/example/viewform",
      title: "Biology Quiz",
      visibleText: "Capital of France Your answer",
      interactiveElements: []
    }
  });

  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.equal(answerKeyStep.answers.length, 1);
  assert.ok(/paris/i.test(answerKeyStep.answers[0].option));
});

test("browser planner uses deterministic mail plan on mail surfaces instead of empty model plans", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "mail_flow",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "Re: Demo follow-up\n\nCan you send an update?",
    resultText: "Thanks for the follow-up. Here is the updated status.",
    action: "draft_reply",
    voiceCommand: "reply to this email",
    contentType: "email",
    browserContext: {
      url: "https://mail.google.com/mail/u/0/#inbox/FMfcgzQ...",
      title: "Inbox",
      visibleText: "Reply Reply all Forward",
      interactiveElements: [
        { role: "button", label: "Reply", nameAttribute: "", type: "button", options: [] }
      ]
    }
  });

  assert.equal(plan.goal, "reply_to_email");
  assert.ok(plan.steps.some((step) => step.tool === "click_role" && step.name === "Reply"));
  assert.ok(plan.steps.some((step) => step.tool === "fill_editor"));
});

test("browser planner uses direct editor insertion for document-style editor surfaces", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "generic_plan",
        summary: "No safe action.",
        requiresConfirmation: false,
        steps: []
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: "Short note",
    resultText: "Refined note for the document.",
    action: "rewrite_structured",
    voiceCommand: "insert this into the page",
    contentType: "general_text",
    browserContext: {
      url: "https://www.notion.so/example",
      title: "Workspace",
      visibleText: "Untitled",
      interactiveElements: [
        { role: "textbox", label: "Page content", nameAttribute: "", type: "textbox", options: [] }
      ]
    }
  });

  assert.equal(plan.goal, "insert_generated_result");
  assert.deepEqual(plan.steps.map((step) => step.tool), ["fill_editor"]);
});
