import express from "express";
import {
  alternativesForType,
  buildPrompt,
  extendedAlternativesForType,
  fallbackActionForType,
  isMetaToolAction,
  isLikelyTimeSensitiveQuestion,
  normalizeActionHint,
  normalizeIntentDecision
} from "./contentClassifier.js";
import { createAiProviderFromEnv, normalizeProviderId } from "./aiProviderFactory.js";
import {
  classifyGeminiError,
  hasConfiguredApiKeys
} from "./apiKeyPool.js";
import { createBrowserActionPlanner, createBrowserActionPlanRefiner } from "./browserActionPlanner.js";
import { describeDominantColorsFromImage } from "./imageAnalysis.js";
import { createSchemaValidators } from "./schemas.js";

const CODE_OUTPUT_ACTIONS = new Set(["improve_code", "debug_code", "optimize_code", "explain_code"]);
const FALSE_VALUES = new Set(["0", "false", "off", "no"]);
const TEXT_SELECTION_KINDS = new Set(["text", "text_image"]);
const USEFUL_TRANSFORM_ACTIONS = new Set(["polish_email", "draft_reply", "rewrite", "rewrite_structured", "optimize_prompt", "translate", "bullet_points", "summarize", "explain", "extract_insights"]);
function withTimeout(promise, timeoutMs, fallbackValue) {
  let timer = null;
  const timeoutPromise = new Promise((resolve) => {
    timer = setTimeout(() => resolve(fallbackValue), timeoutMs);
  });

  return Promise.race([promise, timeoutPromise]).finally(() => {
    if (timer) {
      clearTimeout(timer);
    }
  });
}

function getErrorMessage(error) {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error);
}

function extractRetryAfterSeconds(message) {
  const explicitMatch = message.match(/retry in ([0-9]+(?:\.[0-9]+)?)s/i);
  if (explicitMatch) {
    return Math.ceil(Number(explicitMatch[1]));
  }

  const delayMatch = message.match(/"retryDelay":"([0-9]+)s"/i);
  if (delayMatch) {
    return Number(delayMatch[1]);
  }

  return 30;
}

function sendClassifiedProviderError(res, serviceName, details, operation) {
  const category = classifyGeminiError(details);
  const timestampUtc = new Date().toISOString();

  if (category === "auth") {
    return res.status(401).json({
      error: `${serviceName} could not authenticate any saved API key.`,
      details: "Open Cursivis Settings and replace invalid, revoked, blocked, or unauthorized API keys.",
      timestampUtc
    });
  }

  if (category === "quota") {
    return res.status(429).json({
      error: `${serviceName} quota/rate limit exceeded after trying the available key pool.`,
      details: `${operation} can continue after the provider cooldown.`,
      retryAfterSec: extractRetryAfterSeconds(details),
      timestampUtc
    });
  }

  if (category === "transient") {
    return res.status(503).json({
      error: `${serviceName} is temporarily unavailable. Cursivis retried automatically; please try again in a moment.`,
      details: `${operation} did not recover after retrying available models and API keys.`,
      retryAfterSec: extractRetryAfterSeconds(details),
      timestampUtc
    });
  }

  if (category === "transport_security") {
    return res.status(503).json({
      error: `${serviceName} could not establish a secure network connection.`,
      details: "Check the internet connection, VPN, proxy, or security software, then try again.",
      retryAfterSec: 30,
      timestampUtc
    });
  }

  if (category === "model") {
    return res.status(503).json({
      error: `${serviceName} could not reach a supported Gemini model.`,
      details: `${operation} exhausted the configured model fallback chain.`,
      retryAfterSec: 30,
      timestampUtc
    });
  }

  if (category === "malformed") {
    return res.status(502).json({
      error: `${serviceName} rejected the generated provider request.`,
      details: `${operation} could not be completed. No API key or stack trace was exposed.`,
      timestampUtc
    });
  }

  return null;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function estimateConfidence({
  routedConfidence,
  guardrailApplied,
  resolvedAction,
  selectionType
}) {
  let score = Number.isFinite(routedConfidence) ? routedConfidence : 0.76;

  if (guardrailApplied) {
    score -= 0.08;
  }

  if (resolvedAction === "answer_question" && selectionType === "question") {
    score += 0.05;
  }

  if (resolvedAction === "improve_code" && selectionType === "code") {
    score += 0.04;
  }

  return clamp(score, 0.5, 0.99);
}

function imageAlternatives() {
  return ["describe_image", "extract_key_details", "identify_objects", "generate_captions"];
}

function buildTextImagePrompt({
  text,
  action,
  contentType,
  voiceCommand,
  useGrounding
}) {
  const basePrompt = buildPrompt({
    text,
    action,
    contentType,
    voiceCommand,
    useGrounding
  });

  if (voiceCommand && voiceCommand.trim()) {
    return [
      "Apply the spoken command to the selected text.",
      "Use the attached screenshot as supporting context when it clarifies UI labels, nearby content, or visual meaning.",
      "Return only the final output.",
      `Spoken command: ${voiceCommand.trim()}`,
      "Selected text:",
      text
    ].join("\n\n");
  }

  return [
    basePrompt,
    "Also use the attached screenshot as supporting context when it helps disambiguate the selection.",
    "Treat the selected text as primary context and the screenshot as supplemental context."
  ].join("\n\n");
}

function buildImagePrompt(action) {
  switch (action) {
    case "describe_image":
      return "Describe and summarize this image in a concise paragraph.";
    case "extract_key_details":
      return "Extract the key details from this image and return concise bullet points.";
    case "identify_objects":
      return "Identify the main objects, entities, and relevant labels visible in this image.";
    case "generate_captions":
      return "Generate 5 concise captions for this image with varied tone.";
    case "extract_dominant_colors":
      return "Identify the dominant colors in this image with concise hex values and short color names.";
    case "ocr_extract_text":
      return "Extract all readable text from this image and preserve structure where possible.";
    case "extract_table_data":
      return "If this image contains tabular data, extract it into a clean table. Otherwise return key structured fields.";
    case "rewrite":
      return "Read the image content and rewrite the key information into a clearer concise paragraph.";
    case "translate":
      return "If the image contains text, translate it to English and summarize key points.";
    case "explain":
      return "Explain what this image contains in simple terms, including important details.";
    case "bullet_points":
      return "Extract key insights from this image and return concise bullet points.";
    default:
      return `Perform this operation on the image: ${action.replaceAll("_", " ")}. Return concise practical output only.`;
  }
}

function isGroundingEnabled() {
  const raw = process.env.CURSIVIS_ENABLE_LIVE_GROUNDING;
  if (!raw) {
    return true;
  }

  return !FALSE_VALUES.has(raw.trim().toLowerCase());
}

function buildDirectAnswerRetryPrompt({ question, useGrounding }) {
  return [
    "Answer this user query directly.",
    "Return only the answer, not instructions about how to search for it.",
    "Use 1-3 concise sentences unless a short bullet list is clearly better.",
    useGrounding
      ? "Use live web grounding and include the current date in the answer when the fact may change over time."
      : "If the fact may change over time, include a date qualifier and avoid fake certainty.",
    "User query:",
    question
  ].join("\n\n");
}

function isWeakQuestionResponse(resultText, selectedText) {
  if (!resultText || !resultText.trim()) {
    return true;
  }

  const normalized = resultText.trim().toLowerCase();
  if (
    /^(search|google|look up|lookup|browse)\b/.test(normalized) ||
    /^(the (text|phrase|question)|this (text|phrase|question)|the query)\b/.test(normalized) ||
    /^to find out\b/.test(normalized)
  ) {
    return true;
  }

  const normalizedSelection = (selectedText || "").trim().toLowerCase();
  if (normalizedSelection && normalized === normalizedSelection) {
    return true;
  }

  if (normalized.endsWith("?")) {
    return true;
  }

  return false;
}

function buildUsefulTransformRetryPrompt({ selectedText, action, contentType }) {
  if (action === "optimize_prompt") {
    return [
      "Rewrite this rough prompt, note, or idea dump into a clearly improved prompt for an LLM.",
      "Preserve the user's original meaning and constraints exactly.",
      "Do not invent missing details.",
      "Make it specific, structured, fluent, and ready to paste into another model.",
      "If this is not a prompt, clean it into fluent structured writing instead.",
      "Return only the improved prompt or cleaned text.",
      "Selected text:",
      selectedText
    ].join("\n\n");
  }

  if (action === "draft_reply") {
    return [
      "Draft the most useful reply to this selected email.",
      "Do not echo the original message back.",
      "Respond as if the user wants a ready-to-send reply or follow-up.",
      "Keep it concise, professional, and concrete.",
      "Do not include To:, From:, Cc:, or Bcc: metadata.",
      "Return only an optional Subject line plus the reply body.",
      "Selected email:",
      selectedText
    ].join("\n\n");
  }

  if (action === "polish_email" || contentType === "email") {
    return [
      "Rewrite this into a clearly improved final email draft.",
      "Do not echo the original nearly unchanged.",
      "Shorten where possible, improve clarity and professionalism, and remove repetition.",
      "Do not include To:, From:, Cc:, or Bcc: metadata.",
      "Return only an optional Subject line plus the final email body.",
      "Email draft:",
      selectedText
    ].join("\n\n");
  }

  return [
    `Perform a genuinely useful ${action.replaceAll("_", " ")} transformation.`,
    "Do not repeat the input with only trivial changes.",
    "Return a clearer, more useful final result.",
    "Selected text:",
    selectedText
  ].join("\n\n");
}

function normalizeComparableText(text) {
  return String(text || "")
    .replace(/^```[\w-]*\n?/i, "")
    .replace(/```$/i, "")
    .replace(/^\s*(to|from|cc|bcc):[^\n]*$/gim, "")
    .replace(/\s+/g, " ")
    .replace(/[^a-z0-9\s:]/gi, "")
    .trim()
    .toLowerCase();
}

function computeTokenSimilarity(sourceText, resultText) {
  const sourceTokens = new Set(normalizeComparableText(sourceText).split(" ").filter(Boolean));
  const resultTokens = new Set(normalizeComparableText(resultText).split(" ").filter(Boolean));
  if (sourceTokens.size === 0 || resultTokens.size === 0) {
    return 0;
  }

  let overlap = 0;
  for (const token of sourceTokens) {
    if (resultTokens.has(token)) {
      overlap += 1;
    }
  }

  return overlap / Math.max(sourceTokens.size, resultTokens.size);
}

function isWeakTransformResponse(resultText, selectedText, action, contentType) {
  if (!resultText || !resultText.trim() || !selectedText || !selectedText.trim()) {
    return true;
  }

    if (!USEFUL_TRANSFORM_ACTIONS.has(action) && contentType !== "email") {
      return false;
    }

  const normalizedResult = normalizeComparableText(resultText);
  const normalizedSelection = normalizeComparableText(selectedText);
  if (!normalizedResult || !normalizedSelection) {
    return false;
  }

  if (normalizedResult === normalizedSelection) {
    return true;
  }

  const tokenSimilarity = computeTokenSimilarity(selectedText, resultText);
  const lengthRatio = normalizedResult.length / Math.max(1, normalizedSelection.length);
  const hasMailboxMetadata = /(^|\n)\s*(to|from|cc|bcc):/i.test(resultText);
  const isEmailTransform = action === "polish_email" || contentType === "email";

  if (isEmailTransform && hasMailboxMetadata) {
    return true;
  }

  if (isEmailTransform) {
    return tokenSimilarity >= 0.82 && lengthRatio >= 0.75 && lengthRatio <= 1.45;
  }

  return tokenSimilarity >= 0.9 && lengthRatio >= 0.8 && lengthRatio <= 1.25;
}

function resolveAction({
  mode,
  actionHint,
  intentDecision
}) {
  const hasRequestedAction = Boolean(actionHint && actionHint.trim());
  const requestedAction = hasRequestedAction ? normalizeActionHint(actionHint) : "";
  const shouldTrustRouter = !hasRequestedAction || (mode === "smart" && requestedAction === "summarize");

  let action = shouldTrustRouter ? intentDecision.bestAction : requestedAction;
  let guardrailApplied = false;

  if (!action || !String(action).trim()) {
    action = fallbackActionForType(intentDecision.contentType);
    guardrailApplied = true;
  }

  if (isMetaToolAction(action)) {
    action = fallbackActionForType(intentDecision.contentType);
    guardrailApplied = true;
  }

  return {
    action,
    guardrailApplied
  };
}

function ensureValidationError(res, validator, message) {
  return res.status(400).json({
    error: message,
    details: validator.errors ?? []
  });
}

function toSuggestionResponse(intentDecision, extendedAlternatives = []) {
  return {
    contentType: intentDecision.contentType,
    recommendedAction: intentDecision.bestAction,
    bestAction: intentDecision.bestAction,
    confidence: intentDecision.confidence,
    alternatives: intentDecision.alternatives,
    extendedAlternatives
  };
}

function buildRuntimeProviderStatus(provider) {
  const providerId = provider.id || normalizeProviderId(process.env.CURSIVIS_AI_PROVIDER || "gemini");
  return {
    ok: true,
    provider: providerId,
    displayName: provider.displayName || "AI provider",
    backendMode: providerId === "local_ollama" ? "local" : "api",
    apiProvider: providerId === "local_ollama" ? "gemini" : providerId,
    localModel: process.env.CURSIVIS_LOCAL_MODEL || "granite3.2-vision:2b",
    ollamaUrl: process.env.CURSIVIS_OLLAMA_URL || "http://127.0.0.1:11434",
    openAiBaseUrl: process.env.CURSIVIS_OPENAI_BASE_URL || process.env.OPENAI_BASE_URL || "https://api.openai.com/v1",
    openAiModel: process.env.CURSIVIS_OPENAI_MODEL || process.env.OPENAI_MODEL || "gpt-4.1-mini",
    hostedApiUrl: process.env.CURSIVIS_HOSTED_API_URL || process.env.CURSIVIS_AI_API_URL || "",
    hasGeminiApiKey: Boolean(process.env.GOOGLE_API_KEY || process.env.GEMINI_API_KEY || process.env.GOOGLE_API_KEYS || process.env.GEMINI_API_KEYS),
    hasOpenAiApiKey: Boolean(process.env.CURSIVIS_OPENAI_API_KEY || process.env.OPENAI_API_KEY),
    hasHostedToken: Boolean(process.env.CURSIVIS_HOSTED_TOKEN || process.env.CURSIVIS_LICENSE_TOKEN)
  };
}

async function getLocalOllamaStatus({ ollamaUrl, localModel } = {}) {
  const baseUrl = normalizeBaseUrlForRuntime(ollamaUrl || process.env.CURSIVIS_OLLAMA_URL || "http://127.0.0.1:11434");
  const model = String(localModel || process.env.CURSIVIS_LOCAL_MODEL || "granite3.2-vision:2b").trim();

  try {
    const response = await fetch(`${baseUrl}/api/tags`);
    const bodyText = await response.text();
    let body = {};
    if (bodyText.trim()) {
      try {
        body = JSON.parse(bodyText);
      } catch {
        body = {};
      }
    }

    if (!response.ok) {
      return {
        ok: false,
        reachable: false,
        model,
        ollamaUrl: baseUrl,
        modelInstalled: false,
        installedModels: [],
        error: body?.error || body?.message || bodyText || response.statusText
      };
    }

    const installedModels = Array.isArray(body?.models)
      ? body.models.map((entry) => entry.model || entry.name).filter(Boolean)
      : [];
    const modelInstalled = installedModels.some((installed) => modelMatches(installed, model));

    return {
      ok: true,
      reachable: true,
      model,
      ollamaUrl: baseUrl,
      modelInstalled,
      installedModels
    };
  } catch (error) {
    return {
      ok: false,
      reachable: false,
      model,
      ollamaUrl: baseUrl,
      modelInstalled: false,
      installedModels: [],
      error: getErrorMessage(error)
    };
  }
}

async function unloadLocalOllamaModel({ ollamaUrl, localModel } = {}) {
  const baseUrl = normalizeBaseUrlForRuntime(ollamaUrl || process.env.CURSIVIS_OLLAMA_URL || "http://127.0.0.1:11434");
  const model = String(localModel || "").trim();
  if (!model) {
    return false;
  }

  try {
    const runningResponse = await fetch(`${baseUrl}/api/ps`);
    if (!runningResponse.ok) {
      return false;
    }

    const runningBody = await runningResponse.json();
    const loadedModels = Array.isArray(runningBody?.models)
      ? runningBody.models.map((entry) => entry.model || entry.name).filter(Boolean)
      : [];
    if (!loadedModels.some((loadedModel) => modelMatches(loadedModel, model))) {
      return false;
    }

    await fetch(`${baseUrl}/api/generate`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        model,
        prompt: "",
        stream: false,
        keep_alive: 0
      })
    });
    return true;
  } catch {
    return false;
  }
}

function modelMatches(installed, requested) {
  const left = String(installed || "").trim().toLowerCase();
  const right = String(requested || "").trim().toLowerCase();
  if (!left || !right) {
    return false;
  }

  return left === right || left === `${right}:latest` || `${left}:latest` === right;
}

function normalizeBaseUrlForRuntime(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

export function createApp({ aiProvider, textGenerator, intentRouter, optionGenerator, browserActionPlanner } = {}) {
  const app = express();
  const { validateRequest, validateResponse } = createSchemaValidators();
  let provider = aiProvider ?? createAiProviderFromEnv();
  const aiServiceName = () => provider.displayName || "AI provider";
  const generateText = textGenerator ?? ((request) => provider.generateText(request));
  const routeIntent = intentRouter ?? ((request) => provider.routeIntent(request));
  const generateDynamicOptions = optionGenerator ?? ((request) => provider.generateDynamicOptions(request));
  const planBrowserAction = browserActionPlanner ?? createBrowserActionPlanner({ generateText });
  const refineBrowserActionPlan = createBrowserActionPlanRefiner({ generateText, planBrowserAction });

  app.use(express.json({ limit: "8mb" }));

  app.get("/health", (_req, res) => {
    res.json({
      ok: true,
      service: "cursivis-reasoning-backend",
      aiProvider: provider.id,
      aiProviderDisplayName: provider.displayName || "AI provider",
      ts: new Date().toISOString()
    });
  });

  app.get("/runtime/ai-provider", (_req, res) => {
    res.json(buildRuntimeProviderStatus(provider));
  });

  app.post("/runtime/ai-provider", async (req, res) => {
    const previousProviderId = provider.id || normalizeProviderId(process.env.CURSIVIS_AI_PROVIDER || "gemini");
    const previousLocalModel = process.env.CURSIVIS_LOCAL_MODEL || "granite3.2-vision:2b";
    const previousOllamaUrl = process.env.CURSIVIS_OLLAMA_URL || "http://127.0.0.1:11434";
    const providerId = normalizeProviderId(req.body?.provider);
    const nextLocalModel = typeof req.body?.localModel === "string" && req.body.localModel.trim()
      ? req.body.localModel.trim()
      : previousLocalModel;
    const nextOllamaUrl = typeof req.body?.ollamaUrl === "string" && req.body.ollamaUrl.trim()
      ? req.body.ollamaUrl.trim()
      : previousOllamaUrl;
    const shouldUnloadPreviousLocal =
      previousProviderId === "local_ollama" &&
      (
        providerId !== "local_ollama" ||
        !modelMatches(previousLocalModel, nextLocalModel) ||
        normalizeBaseUrlForRuntime(previousOllamaUrl) !== normalizeBaseUrlForRuntime(nextOllamaUrl)
      );

    process.env.CURSIVIS_AI_PROVIDER = providerId;

    if (typeof req.body?.openAiBaseUrl === "string") {
      process.env.CURSIVIS_OPENAI_BASE_URL = req.body.openAiBaseUrl.trim();
    }

    if (typeof req.body?.openAiApiKey === "string") {
      process.env.CURSIVIS_OPENAI_API_KEY = req.body.openAiApiKey.trim();
    }

    if (typeof req.body?.openAiModel === "string") {
      process.env.CURSIVIS_OPENAI_MODEL = req.body.openAiModel.trim();
    }

    if (typeof req.body?.hostedApiUrl === "string") {
      process.env.CURSIVIS_HOSTED_API_URL = req.body.hostedApiUrl.trim();
    }

    if (typeof req.body?.hostedToken === "string") {
      process.env.CURSIVIS_HOSTED_TOKEN = req.body.hostedToken.trim();
    }

    if (typeof req.body?.localModel === "string") {
      process.env.CURSIVIS_LOCAL_MODEL = req.body.localModel.trim();
    }

    if (typeof req.body?.ollamaUrl === "string") {
      process.env.CURSIVIS_OLLAMA_URL = req.body.ollamaUrl.trim();
    }

    if (shouldUnloadPreviousLocal) {
      await unloadLocalOllamaModel({
        ollamaUrl: previousOllamaUrl,
        localModel: previousLocalModel
      });
    }

    if (!aiProvider) {
      provider = createAiProviderFromEnv();
    }

    res.json({
      ok: true,
      provider: providerId,
      displayName: provider.displayName || "AI provider",
      note: "Provider changes are active for new requests."
    });
  });

  app.post("/runtime/ai-provider/test", async (req, res) => {
    const providerId = normalizeProviderId(req.body?.provider || provider.id);
    const testEnv = {
      ...process.env,
      CURSIVIS_AI_PROVIDER: providerId
    };

    if (typeof req.body?.openAiBaseUrl === "string") {
      testEnv.CURSIVIS_OPENAI_BASE_URL = req.body.openAiBaseUrl.trim();
    }

    if (typeof req.body?.openAiApiKey === "string") {
      testEnv.CURSIVIS_OPENAI_API_KEY = req.body.openAiApiKey.trim();
    }

    if (typeof req.body?.openAiModel === "string") {
      testEnv.CURSIVIS_OPENAI_MODEL = req.body.openAiModel.trim();
    }

    if (typeof req.body?.hostedApiUrl === "string") {
      testEnv.CURSIVIS_HOSTED_API_URL = req.body.hostedApiUrl.trim();
    }

    if (typeof req.body?.hostedToken === "string") {
      testEnv.CURSIVIS_HOSTED_TOKEN = req.body.hostedToken.trim();
    }

    if (typeof req.body?.localModel === "string") {
      testEnv.CURSIVIS_LOCAL_MODEL = req.body.localModel.trim();
    }

    if (typeof req.body?.ollamaUrl === "string") {
      testEnv.CURSIVIS_OLLAMA_URL = req.body.ollamaUrl.trim();
    }

    try {
      if (providerId === "local_ollama") {
        const localStatus = await getLocalOllamaStatus({
          ollamaUrl: testEnv.CURSIVIS_OLLAMA_URL,
          localModel: testEnv.CURSIVIS_LOCAL_MODEL
        });

        if (!localStatus.reachable) {
          return res.status(503).json({
            ok: false,
            provider: providerId,
            error: "Ollama is not reachable on the configured local URL.",
            details: localStatus.error,
            ...localStatus
          });
        }

        if (!localStatus.modelInstalled) {
          return res.status(409).json({
            ok: false,
            provider: providerId,
            error: `Local model '${localStatus.model}' is not installed yet.`,
            ...localStatus
          });
        }
      }

      const testProvider = createAiProviderFromEnv(testEnv);
      const startedAt = Date.now();
      const providerTestTimeoutMs = providerId === "local_ollama"
        ? Number(process.env.CURSIVIS_LOCAL_TEST_TIMEOUT_MS || 240000)
        : 45000;
      const generated = await withTimeout(
        testProvider.generateText({
          prompt: "Reply with exactly: Cursivis local setup ready.",
          action: "provider_test",
          selectionType: "provider_test",
          config: { temperature: 0, numPredict: 24 }
        }),
        providerTestTimeoutMs,
        null
      );

      if (!generated) {
        return res.status(504).json({
          ok: false,
          provider: providerId,
          error: "Provider test timed out."
        });
      }

      return res.json({
        ok: true,
        provider: providerId,
        displayName: testProvider.displayName || "AI provider",
        model: generated.model,
        latencyMs: generated.latencyMs ?? Math.max(1, Date.now() - startedAt),
        preview: generated.text.slice(0, 160)
      });
    } catch (error) {
      return res.status(500).json({
        ok: false,
        provider: providerId,
        error: `${providerId === "local_ollama" ? "Local LLM" : "API LLM"} test failed.`,
        details: getErrorMessage(error)
      });
    }
  });

  app.post("/runtime/local-llm/status", async (req, res) => {
    const status = await getLocalOllamaStatus({
      ollamaUrl: req.body?.ollamaUrl,
      localModel: req.body?.localModel
    });

    res.status(status.reachable ? 200 : 503).json(status);
  });

  app.post("/runtime/local-llm/pull", async (req, res) => {
    const ollamaUrl = normalizeBaseUrlForRuntime(req.body?.ollamaUrl || process.env.CURSIVIS_OLLAMA_URL || "http://127.0.0.1:11434");
    const localModel = String(req.body?.localModel || process.env.CURSIVIS_LOCAL_MODEL || "granite3.2-vision:2b").trim();

    if (!localModel) {
      return res.status(400).json({ ok: false, error: "localModel is required." });
    }

    try {
      const response = await fetch(`${ollamaUrl}/api/pull`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ model: localModel, stream: false })
      });

      const bodyText = await response.text();
      let body = {};
      if (bodyText.trim()) {
        try {
          body = JSON.parse(bodyText);
        } catch {
          body = { status: bodyText };
        }
      }

      if (!response.ok) {
        return res.status(response.status).json({
          ok: false,
          error: "Ollama model download failed.",
          details: body?.error || body?.message || bodyText || response.statusText
        });
      }

      return res.json({
        ok: true,
        model: localModel,
        status: body?.status || "success"
      });
    } catch (error) {
      return res.status(503).json({
        ok: false,
        error: "Ollama is not reachable for model download.",
        details: getErrorMessage(error)
      });
    }
  });

  app.post("/runtime/local-llm/pull-stream", async (req, res) => {
    const ollamaUrl = normalizeBaseUrlForRuntime(req.body?.ollamaUrl || process.env.CURSIVIS_OLLAMA_URL || "http://127.0.0.1:11434");
    const localModel = String(req.body?.localModel || process.env.CURSIVIS_LOCAL_MODEL || "granite3.2-vision:2b").trim();

    if (!localModel) {
      return res.status(400).json({ ok: false, error: "localModel is required." });
    }

    const abortController = new AbortController();
    res.on("close", () => {
      if (!res.writableEnded) {
        abortController.abort();
      }
    });

    try {
      const response = await fetch(`${ollamaUrl}/api/pull`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        signal: abortController.signal,
        body: JSON.stringify({ model: localModel, stream: true })
      });

      res.status(response.ok ? 200 : response.status);
      res.setHeader("content-type", "application/x-ndjson; charset=utf-8");
      res.setHeader("cache-control", "no-cache");

      if (!response.body) {
        res.write(`${JSON.stringify({
          ok: false,
          model: localModel,
          error: "Ollama returned an empty download stream."
        })}\n`);
        return res.end();
      }

      if (!response.ok) {
        const bodyText = await response.text();
        res.write(`${JSON.stringify({
          ok: false,
          model: localModel,
          error: "Ollama model download failed.",
          details: bodyText || response.statusText
        })}\n`);
        return res.end();
      }

      const decoder = new TextDecoder();
      for await (const chunk of response.body) {
        if (abortController.signal.aborted) {
          break;
        }

        const text = decoder.decode(chunk, { stream: true });
        if (text) {
          res.write(text);
        }
      }

      if (!abortController.signal.aborted) {
        res.end();
      }
    } catch (error) {
      if (abortController.signal.aborted || res.destroyed) {
        return;
      }

      res.status(503);
      res.setHeader("content-type", "application/x-ndjson; charset=utf-8");
      res.write(`${JSON.stringify({
        ok: false,
        model: localModel,
        error: "Ollama is not reachable for model download.",
        details: getErrorMessage(error)
      })}\n`);
      res.end();
    }
  });

  app.post("/runtime/api-key", (req, res) => {
    const rawApiKey = String(req.body?.apiKey || "").trim();
    const apiKeys = rawApiKey
      .split(/[,\n;\r]+/)
      .map((value) => value.trim())
      .filter(Boolean);

    if (apiKeys.length === 0) {
      res.status(400).json({ error: "A valid Gemini API key is required." });
      return;
    }

    process.env.GOOGLE_API_KEY = apiKeys[0];
    process.env.GEMINI_API_KEY = apiKeys[0];
    process.env.GOOGLE_API_KEYS = apiKeys.join(",");
    process.env.GEMINI_API_KEYS = apiKeys.join(",");

    res.json({
      ok: true,
      activeKeyPreview: `${apiKeys[0].slice(0, 6)}...${apiKeys[0].slice(-4)}`,
      totalKeys: apiKeys.length
    });
  });

  async function analyzeHandler(req, res) {
    if (!validateRequest(req.body)) {
      return ensureValidationError(res, validateRequest, "Request failed schema validation.");
    }

    const { requestId, actionHint, selection, voiceCommand } = req.body;
    if (selection.kind === "none") {
      return res.status(400).json({
        error: "Selection kind 'none' is not actionable."
      });
    }

    const requestedAction = normalizeActionHint(actionHint);
    let selectionType = "general_text";
    let alternatives = alternativesForType(selectionType);
    let generationInput = null;
    let localResultOverride = null;
    let resolvedAction = requestedAction;
    let routedConfidence = 0.74;
    let guardrailApplied = false;
    let generationUseGrounding = false;

    if (TEXT_SELECTION_KINDS.has(selection.kind)) {
      if (!selection.text || !selection.text.trim()) {
        return res.status(400).json({
          error: "Text selection is empty."
        });
      }

      const selectionKind = selection.kind === "text_image" ? "text_image" : "text";
      const hasImageContext =
        selectionKind === "text_image" &&
        Boolean(selection.imageBase64 && selection.imageMimeType);

      if (requestedAction === "optimize_prompt") {
        selectionType = "prompt";
        resolvedAction = "optimize_prompt";
        routedConfidence = 0.88;
        alternatives = alternativesForType(selectionType);
      } else {
        let intentDecision;
        try {
          intentDecision = normalizeIntentDecision(
            await routeIntent({
              selectionKind,
              text: selection.text,
              imageBase64: selection.imageBase64,
              imageMimeType: selection.imageMimeType,
              mode: req.body.mode,
              actionHint,
              voiceCommand
            }),
            selection.text
          );
        } catch (error) {
          const details = getErrorMessage(error);
          const classifiedError = sendClassifiedProviderError(res, aiServiceName(), details, "Intent routing");
          if (classifiedError) {
            return classifiedError;
          }

          return res.status(500).json({
            error: `${aiServiceName()} intent routing failed.`,
            details
          });
        }

        const resolution = resolveAction({
          mode: req.body.mode,
          actionHint,
          intentDecision
        });

        selectionType = intentDecision.contentType;
        alternatives = intentDecision.alternatives.length > 0
          ? intentDecision.alternatives
          : alternativesForType(selectionType);
        routedConfidence = intentDecision.confidence;
        resolvedAction = resolution.action;
        guardrailApplied = resolution.guardrailApplied;
        alternatives = [resolvedAction, ...alternatives]
          .filter((value, index, array) => array.indexOf(value) === index)
          .slice(0, 5);

        const useGrounding =
          isGroundingEnabled() &&
          resolvedAction === "answer_question" &&
          isLikelyTimeSensitiveQuestion(selection.text);
        generationUseGrounding = useGrounding;
      }

      const prompt = hasImageContext
        ? buildTextImagePrompt({
            text: selection.text,
            action: resolvedAction,
            contentType: selectionType,
            voiceCommand,
            useGrounding: generationUseGrounding
          })
        : buildPrompt({
            text: selection.text,
            action: resolvedAction,
            contentType: selectionType,
            voiceCommand,
            useGrounding: generationUseGrounding
          });

      generationInput = hasImageContext
        ? {
            contents: [
              {
                role: "user",
                parts: [
                  {
                    text: prompt
                  },
                  {
                    inlineData: {
                      mimeType: selection.imageMimeType,
                      data: selection.imageBase64
                    }
                  }
                ]
              }
            ],
            useGrounding: generationUseGrounding
          }
        : {
            prompt,
            useGrounding: generationUseGrounding
          };

      if (hasImageContext && resolvedAction === "extract_dominant_colors") {
        localResultOverride = describeDominantColorsFromImage(selection.imageBase64, selection.imageMimeType);
      }
    } else if (selection.kind === "image") {
      if (!selection.imageBase64 || !selection.imageMimeType) {
        return res.status(400).json({
          error: "Image selection payload is incomplete."
        });
      }

      let intentDecision;
      try {
        intentDecision = normalizeIntentDecision(
          await routeIntent({
            selectionKind: "image",
            imageBase64: selection.imageBase64,
            imageMimeType: selection.imageMimeType,
            mode: req.body.mode,
            actionHint,
            voiceCommand
          }),
          ""
        );
      } catch (error) {
        const details = getErrorMessage(error);
        const classifiedError = sendClassifiedProviderError(res, aiServiceName(), details, "Image intent routing");
        if (classifiedError) {
          return classifiedError;
        }

        return res.status(500).json({
            error: `${aiServiceName()} intent routing failed.`,
          details
        });
      }
      intentDecision = {
        ...intentDecision,
        contentType: "image"
      };

      const resolution = resolveAction({
        mode: req.body.mode,
        actionHint,
        intentDecision
      });

      selectionType = "image";
      alternatives = intentDecision.alternatives.length > 0 ? intentDecision.alternatives : imageAlternatives();
      routedConfidence = intentDecision.confidence;
      resolvedAction = resolution.action;
      guardrailApplied = resolution.guardrailApplied;

      generationInput = {
        contents: [
          {
            role: "user",
            parts: [
              {
                text: voiceCommand && voiceCommand.trim()
                  ? [
                      "Apply the user's spoken command to this image content.",
                      `Voice command: ${voiceCommand.trim()}`,
                      "Return only the transformed result."
                    ].join("\n\n")
                  : buildImagePrompt(resolvedAction)
              },
              {
                inlineData: {
                  mimeType: selection.imageMimeType,
                  data: selection.imageBase64
                }
              }
            ]
          }
        ]
      };

      if (resolvedAction === "extract_dominant_colors") {
        localResultOverride = describeDominantColorsFromImage(selection.imageBase64, selection.imageMimeType);
      }
    } else {
      return res.status(400).json({
        error: `Unsupported selection kind: ${selection.kind}`
      });
    }

    const startedAt = Date.now();

    try {
      let generated;
      let resultText;

      if (localResultOverride) {
        generated = {
          text: localResultOverride,
          model: "local-image-analysis",
          latencyMs: Math.max(1, Date.now() - startedAt),
          usage: undefined
        };
        resultText = sanitizeResultText(localResultOverride, resolvedAction);
      } else {
        generated = await generateText({
          ...generationInput,
          selectionType,
          action: resolvedAction,
          text: selection.text
        });

        resultText = sanitizeResultText(generated.text, resolvedAction);
      }

      if (
        TEXT_SELECTION_KINDS.has(selection.kind) &&
        resolvedAction === "answer_question" &&
        isWeakQuestionResponse(resultText, selection.text)
      ) {
        generated = await generateText({
          prompt: buildDirectAnswerRetryPrompt({
            question: selection.text,
            useGrounding: generationUseGrounding
          }),
          useGrounding: generationUseGrounding,
          action: resolvedAction,
          text: selection.text
        });
        resultText = sanitizeResultText(generated.text, resolvedAction);
      }

      if (
        TEXT_SELECTION_KINDS.has(selection.kind) &&
        isWeakTransformResponse(resultText, selection.text, resolvedAction, selectionType)
      ) {
        generated = await generateText({
          prompt: buildUsefulTransformRetryPrompt({
            selectedText: selection.text,
            action: resolvedAction,
            contentType: selectionType
          }),
          useGrounding: false,
          action: resolvedAction,
          text: selection.text
        });
        resultText = sanitizeResultText(generated.text, resolvedAction);
      }

      const response = {
        protocolVersion: "1.0.0",
        requestId,
        action: resolvedAction,
        result: resultText,
        confidence: estimateConfidence({
          routedConfidence,
          guardrailApplied,
          resolvedAction,
          selectionType
        }),
        alternatives,
        latencyMs: generated.latencyMs ?? Date.now() - startedAt,
        model: generated.model,
        usage: generated.usage,
        timestampUtc: new Date().toISOString()
      };

      if (!validateResponse(response)) {
        return res.status(500).json({
          error: "Response failed schema validation.",
          details: validateResponse.errors ?? []
        });
      }

      return res.json(response);
    } catch (error) {
      const details = getErrorMessage(error);
      const classifiedError = sendClassifiedProviderError(res, aiServiceName(), details, "Analysis");
      if (classifiedError) {
        return classifiedError;
      }

      return res.status(500).json({
        error: `${aiServiceName()} analysis failed.`,
        details
      });
    }
  }

  app.post("/analyze", analyzeHandler);
  app.post("/api/intent", analyzeHandler);

  app.post("/suggest-actions", async (req, res) => {
    if (!validateRequest(req.body)) {
      return ensureValidationError(res, validateRequest, "Request failed schema validation.");
    }

    const { selection } = req.body;
    if (TEXT_SELECTION_KINDS.has(selection.kind) && (!selection.text || !selection.text.trim())) {
      return res.status(400).json({
        error: "Text selection is empty."
      });
    }

    if ((selection.kind === "image" || selection.kind === "text_image") && (!selection.imageBase64 || !selection.imageMimeType)) {
      return res.status(400).json({
        error: "Image selection payload is incomplete."
      });
    }

    if (selection.kind === "none") {
      return res.status(400).json({
        error: "Selection kind 'none' is not actionable."
      });
    }

    try {
      const selectionKind = selection.kind === "image"
        ? "image"
        : selection.kind === "text_image"
          ? "text_image"
          : "text";
      const selectionText = selection.text || "";
      let intentDecision = normalizeIntentDecision(
        await routeIntent({
          selectionKind,
          text: selectionText,
          imageBase64: selection.imageBase64,
          imageMimeType: selection.imageMimeType,
          mode: req.body.mode,
          actionHint: req.body.actionHint,
          voiceCommand: req.body.voiceCommand
        }),
        selectionText
      );

      if (selectionKind === "image") {
        intentDecision = {
          ...intentDecision,
          contentType: "image"
        };
      }

      const seededExtended = extendedAlternativesForType(intentDecision.contentType, selectionText)
        .filter((action) => !intentDecision.alternatives.includes(action));

      const generatedExtended = await withTimeout(
        generateDynamicOptions({
          selectionKind,
          text: selectionText,
          imageBase64: selection.imageBase64,
          imageMimeType: selection.imageMimeType,
          contentType: intentDecision.contentType,
          currentOptions: intentDecision.alternatives
        }),
        2500,
        []
      );

      const extendedAlternatives = [...generatedExtended, ...seededExtended]
        .filter(Boolean)
        .filter((action, index, array) => array.indexOf(action) === index)
        .filter((action) => !intentDecision.alternatives.includes(action))
        .slice(0, 10);

      return res.json(toSuggestionResponse(intentDecision, extendedAlternatives));
    } catch (error) {
      const details = getErrorMessage(error);
      const classifiedError = sendClassifiedProviderError(res, aiServiceName(), details, "Suggestion routing");
      if (classifiedError) {
        return classifiedError;
      }

      return res.status(500).json({
        error: `${aiServiceName()} suggestion routing failed.`,
        details
      });
    }
  });

  app.post("/transcribe", async (req, res) => {
    const { audioBase64, mimeType } = req.body ?? {};
    if (!audioBase64 || typeof audioBase64 !== "string") {
      return res.status(400).json({
        error: "audioBase64 is required."
      });
    }

    let transcribeGenerateText = generateText;
    let transcribeServiceName = aiServiceName();
    if (provider.id === "local_ollama") {
      if (!hasConfiguredApiKeys()) {
        return res.status(501).json({
          error: "Voice transcription needs an API transcriber in Local LLM mode.",
          details: "Local LLM can answer after speech is transcribed, but Ollama is not used for raw audio transcription yet. Add a Gemini/API key for transcription or use the Text Trigger fallback."
        });
      }

      const transcriptionProvider = createAiProviderFromEnv({
        ...process.env,
        CURSIVIS_AI_PROVIDER: "gemini"
      });
      transcribeGenerateText = (request) => transcriptionProvider.generateText(request);
      transcribeServiceName = transcriptionProvider.displayName || "Gemini";
    }

    const resolvedMimeType =
      typeof mimeType === "string" && mimeType.trim()
        ? mimeType.trim()
        : "audio/wav";

    const startedAt = Date.now();
    try {
      const generated = await transcribeGenerateText({
        modelOverride:
          process.env.GEMINI_TRANSCRIBE_MODEL ||
          process.env.GEMINI_MODEL ||
          "gemini-3.6-flash",
        contents: [
          {
            role: "user",
            parts: [
              {
                text: [
                  "Transcribe this spoken command accurately.",
                  "Return only the transcribed command text.",
                  "No extra commentary.",
                  "Do not describe the audio or say that you are transcribing it."
                ].join("\n")
              },
              {
                inlineData: {
                  mimeType: resolvedMimeType,
                  data: audioBase64
                }
              }
            ]
          }
        ],
        config: {
          systemInstruction: [
            "You are a speech-to-text transcriber for Cursivis.",
            "Transcribe the spoken command accurately.",
            "Return only the spoken words as plain text.",
            "Do not summarize, explain, label, or add commentary."
          ].join(" ")
        }
      });

      const text = sanitizeTranscriptionText(generated.text);
      return res.json({
        text,
        latencyMs: generated.latencyMs ?? Date.now() - startedAt,
        model: generated.model,
        timestampUtc: new Date().toISOString()
      });
    } catch (error) {
      const details = getErrorMessage(error);
      const classifiedError = sendClassifiedProviderError(res, transcribeServiceName, details, "Transcription");
      if (classifiedError) {
        return classifiedError;
      }

      return res.status(500).json({
        error: "Transcription failed.",
        details
      });
    }
  });

  app.post("/plan-browser-action", async (req, res) => {
    const {
      originalText,
      resultText,
      action,
      voiceCommand,
      executionInstruction,
      contentType,
      browserContext
    } = req.body ?? {};

    if (!browserContext || typeof browserContext !== "object") {
      return res.status(400).json({
        error: "browserContext is required."
      });
    }

    if (!resultText || typeof resultText !== "string" || !resultText.trim()) {
      return res.status(400).json({
        error: "resultText is required."
      });
    }

    try {
      const plan = await planBrowserAction({
        originalText: typeof originalText === "string" ? originalText : "",
        resultText: resultText.trim(),
        action: typeof action === "string" ? action : "",
        voiceCommand: typeof voiceCommand === "string" ? voiceCommand : "",
        executionInstruction: typeof executionInstruction === "string" ? executionInstruction : "",
        contentType: typeof contentType === "string" ? contentType : "general_text",
        browserContext
      });

      return res.json(plan);
    } catch (error) {
      const details = getErrorMessage(error);
      const classifiedError = sendClassifiedProviderError(res, aiServiceName(), details, "Browser action planning");
      if (classifiedError) {
        return classifiedError;
      }

      return res.status(500).json({
        error: "Browser action planning failed.",
        details
      });
    }
  });

  app.post("/refine-browser-action", async (req, res) => {
    const {
      originalText,
      resultText,
      action,
      voiceCommand,
      executionInstruction,
      contentType,
      browserContext,
      currentPlan
    } = req.body ?? {};

    if (!browserContext || typeof browserContext !== "object") {
      return res.status(400).json({
        error: "browserContext is required."
      });
    }

    if (!currentPlan || typeof currentPlan !== "object") {
      return res.status(400).json({
        error: "currentPlan is required."
      });
    }

    if (!resultText || typeof resultText !== "string" || !resultText.trim()) {
      return res.status(400).json({
        error: "resultText is required."
      });
    }

    if (!executionInstruction || typeof executionInstruction !== "string" || !executionInstruction.trim()) {
      return res.status(400).json({
        error: "executionInstruction is required."
      });
    }

    try {
      const plan = await refineBrowserActionPlan({
        originalText: typeof originalText === "string" ? originalText : "",
        resultText: resultText.trim(),
        action: typeof action === "string" ? action : "",
        voiceCommand: typeof voiceCommand === "string" ? voiceCommand : "",
        executionInstruction: executionInstruction.trim(),
        contentType: typeof contentType === "string" ? contentType : "general_text",
        browserContext,
        currentPlan
      });

      return res.json(plan);
    } catch (error) {
      const details = getErrorMessage(error);
      const classifiedError = sendClassifiedProviderError(res, aiServiceName(), details, "Browser action refinement");
      if (classifiedError) {
        return classifiedError;
      }

      return res.status(500).json({
        error: "Browser action refinement failed.",
        details
      });
    }
  });

  return app;
}

function sanitizeResultText(text, action = "summarize") {
  if (!text || !text.trim()) {
    return "No useful response returned.";
  }

  let cleaned = text
    .replace(/\u0000/g, "")
    .replace(/\r/g, "")
    .trim();

  if (!CODE_OUTPUT_ACTIONS.has(action)) {
    cleaned = cleaned.replace(/^```[\w-]*\n?/i, "").replace(/```$/i, "").trim();
  }

  const blocks = cleaned
    .split(/\n{2,}/)
    .map((chunk) => chunk.trim())
    .filter(Boolean);

  const uniqueBlocks = [];
  const seen = new Set();
  for (const block of blocks) {
    const normalized = block.toLowerCase();
    if (!seen.has(normalized)) {
      seen.add(normalized);
      uniqueBlocks.push(block);
    }
  }

  cleaned = (uniqueBlocks.length > 0 ? uniqueBlocks : [cleaned]).join("\n\n").trim();

  if (!CODE_OUTPUT_ACTIONS.has(action)) {
    cleaned = cleaned
      .replace(/^(sure|certainly|here('| i)?s)\b[^\n]*\n?/i, "")
      .replace(/\n{3,}/g, "\n\n")
      .trim();
  }

  if (action === "polish_email" || action === "draft_reply") {
    cleaned = cleaned
      .replace(/^\s*(to|from|cc|bcc):[^\n]*\n?/gim, "")
      .replace(/\n{3,}/g, "\n\n")
      .trim();
  }

  return cleaned;
}

function sanitizeTranscriptionText(text) {
  if (!text || !text.trim()) {
    return "";
  }

  let cleaned = text
    .replace(/\u0000/g, "")
    .replace(/\r/g, "")
    .replace(/^```[\w-]*\n?/i, "")
    .replace(/```$/i, "")
    .trim();

  cleaned = cleaned
    .replace(/^["'`]+|["'`]+$/g, "")
    .replace(/^here('| i)?s\s+(the\s+)?(transcribed|spoken)\s+(command|text)\s*[:\-]?\s*/i, "")
    .replace(/^(transcribed|spoken)\s+(command|text)\s*[:\-]?\s*/i, "")
    .replace(/^transcription\s*[:\-]?\s*/i, "")
    .trim();

  return cleaned;
}
