import {
  buildIntentRouterPrompt,
  describeIntentRoutingConcern,
  inferUsefulCodeAction,
  inferFallbackType,
  normalizeActionHint,
  normalizeIntentDecision
} from "./contentClassifier.js";
import {
  hasConfiguredApiKeys,
  classifyGeminiError,
  isModelAvailabilityError,
  isQuotaOrRateLimitError,
  isRetriableGeminiRequestError,
  isTransientProviderError,
  withGoogleGenAiClient
} from "./apiKeyPool.js";
import { getGeminiErrorDiagnostics, traceGeminiEvent } from "./geminiDiagnostics.js";

const DEFAULT_GEMINI_MODEL = "gemini-3.6-flash";
const DEFAULT_FALLBACK_MODELS = ["gemini-3.5-flash", "gemini-3.5-flash-lite"];
const RETIRED_FALLBACK_MODELS = new Set(["gemini-2.0-flash"]);
const DEFAULT_CACHE_TTL_MS = 10 * 60 * 1000;
const DEFAULT_CACHE_LIMIT = 200;
const TRANSIENT_RETRY_DELAYS_MS = [500, 1500];
const DEFAULT_OPERATION_BUDGET_MS = 110 * 1000;
const DEFAULT_ATTEMPT_TIMEOUT_MS = 25 * 1000;
const DEFAULT_OPTIONS_BUDGET_MS = 2200;
const CONTEXTUAL_EXECUTION_SYSTEM_INSTRUCTION = [
  "You are Cursivis, a cursor-native AI assistant.",
  "Selection is the context, trigger press is the user's intent, and your job is to return the most useful result for that selection.",
  "Understand the selected content before responding.",
  "Honor the chosen action when provided, but execute it intelligently and practically rather than mechanically.",
  "The chosen action is a routing hint, not a rigid template. Optimize for the most useful user-facing result.",
  "If the selection is a polished email or an email thread, replying is often more useful than rewriting it.",
  "If the selection is a rough or poorly punctuated email draft, improve grammar, punctuation, clarity, and tone instead of replying.",
  "If the selection is valid code, explain it clearly unless a different action is explicitly better.",
  "If the selection is broken, incomplete, or syntactically invalid code, debug and fix it before explaining.",
  "Be decisive, concise, and useful by default.",
  "Do not output internal reasoning, tool chatter, or generic advice unless the user explicitly asks for it.",
  "If content is time-sensitive and grounding is enabled, use grounded facts and include an explicit date."
].join(" ");
const INTENT_ROUTER_SYSTEM_INSTRUCTION = [
  "You are the Cursivis intent router.",
  "Your job is to infer the most useful action from the user's current selection.",
  "First identify the content type, then infer likely user intent, then choose the single best action, then suggest realistic follow-up actions.",
  "Prefer usefulness over rigid labels.",
  "Do not mechanically default to summarize or rewrite when a more context-aware action would help more.",
  "Selection is the context and the trigger press is the user's intent.",
  "You are not limited to any predefined menu. Use any concise snake_case action that best serves the selection.",
  "Examples of useful routing: a report -> summarize or extract insights; foreign-language text -> translate; broken code -> debug_code; correct code -> explain_code; rough raw prose or rough email -> rewrite/polish; polished email or thread -> draft_reply; a direct question or factual phrase query -> answer_question; a draft YouTube description or caption seed -> expand_text or suggest_captions when that is more useful.",
  "If a predefined action is not ideal, create a concise new snake_case action that better fits the selection.",
  "For code, decide whether the code needs fixing, improving, or explanation.",
  "If code looks broken, incomplete, or likely to fail, prefer debug_code or another fix-first action over explain_code.",
  "For email, distinguish rough drafts from already-polished or mailbox-style messages, and prefer draft_reply when replying would be more useful than polishing.",
  "A rough or badly punctuated email draft should usually be polished, not replied to.",
  "A polished email or mailbox-style thread should usually get a concise reply draft, not another rewrite.",
  "For reports and long factual text, prefer insights and concise summaries over cosmetic rewrites.",
  "Use answer_question only when the selection itself is asking something or is a short factual phrase query. Long article excerpts, encyclopedia text, biographies, and reports are usually not questions.",
  "Return strict JSON only."
].join(" ");
const DYNAMIC_OPTIONS_SYSTEM_INSTRUCTION = [
  "You generate additional action options for Guided Mode in Cursivis.",
  "Start from the selected content, infer what other useful operations a user may want next, and return only practical, concise, executable follow-up actions.",
  "Do not repeat existing options, and prefer specific context-aware actions over generic ones.",
  "Return strict JSON only."
].join(" ");

const INTENT_ROUTER_SCHEMA = {
  type: "object",
  additionalProperties: false,
  properties: {
    contentType: { type: "string" },
    bestAction: { type: "string" },
    confidence: { type: "number" },
    alternatives: {
      type: "array",
      items: { type: "string" },
      minItems: 0,
      maxItems: 8
    }
  },
  required: ["contentType", "bestAction", "confidence", "alternatives"]
};

const DYNAMIC_OPTIONS_SCHEMA = {
  type: "object",
  additionalProperties: false,
  properties: {
    extraActions: {
      type: "array",
      items: { type: "string" },
      minItems: 0,
      maxItems: 8
    }
  },
  required: ["extraActions"]
};

export function createGeminiTextGenerator({
  model = process.env.GEMINI_MODEL || DEFAULT_GEMINI_MODEL
} = {}) {
  const cache = new Map();

  return async ({ prompt, contents, useGrounding = false, modelOverride, config = {}, selectionType, action }) => {
    if (!hasConfiguredApiKeys()) {
      throw new Error("GOOGLE_API_KEY or GOOGLE_API_KEYS is required to call Gemini.");
    }

    const startedAt = Date.now();
    const preferredModel = modelOverride || model;
    const cacheKey = buildTextCacheKey({ model: preferredModel, prompt, useGrounding, config });
    const cached = cacheKey ? readCache(cache, cacheKey) : null;
    if (cached) {
      return {
        ...cached,
        latencyMs: Math.max(1, Date.now() - startedAt),
        cached: true
      };
    }

    const request = {
      model: preferredModel,
      contents: contents ?? prompt,
      config: {
        systemInstruction: buildExecutionSystemInstruction({
          selectionType,
          action
        }),
        ...config
      }
    };

    if (useGrounding) {
      request.config.tools = [...(request.config.tools ?? []), { googleSearch: {} }];
    }

    const candidateModels = buildModelCandidates(preferredModel);
    const retryContext = createRetryContext();
    let response;
    let resolvedModel = preferredModel;
    try {
      const generated = await withGoogleGenAiClient(
        (client, entry) => generateWithFallbackModels(client, request, candidateModels, useGrounding, {
          keyIndex: entry.index,
          retryContext
        }),
        { canRetryError: isRetriableGeminiRequestError }
      );
      response = generated.response;
      resolvedModel = generated.model;
    } catch (error) {
      if (useGrounding) {
        // Fallback gracefully if grounding tool is unavailable in the current API mode.
        const fallbackRequest = {
          ...request,
          config: {
            ...request.config
          }
        };
        delete fallbackRequest.config.tools;
        const generated = await withGoogleGenAiClient(
          (client, entry) => generateWithFallbackModels(client, fallbackRequest, candidateModels, false, {
            keyIndex: entry.index,
            retryContext
          }),
          { canRetryError: isRetriableGeminiRequestError }
        );
        response = generated.response;
        resolvedModel = generated.model;
      } else {
        throw error;
      }
    }

    const text = response.text?.trim();
    if (!text) {
      throw new Error("Gemini returned no text result.");
    }

    const usage = response.usageMetadata
      ? {
          inputTokens: response.usageMetadata.promptTokenCount ?? 0,
          outputTokens: response.usageMetadata.candidatesTokenCount ?? 0
        }
      : undefined;

    const result = {
      text,
      usage,
      model: resolvedModel,
      latencyMs: Date.now() - startedAt
    };

    if (cacheKey) {
      writeCache(cache, cacheKey, result);
    }

    return result;
  };
}

function parseJsonObject(rawText) {
  if (!rawText || !rawText.trim()) {
    return null;
  }

  const trimmed = rawText.trim();
  const fencedMatch = trimmed.match(/```(?:json)?\s*([\s\S]*?)\s*```/i);
  const candidate = fencedMatch?.[1]?.trim() || trimmed;

  try {
    return JSON.parse(candidate);
  } catch {
    const startIndex = candidate.indexOf("{");
    const endIndex = candidate.lastIndexOf("}");
    if (startIndex < 0 || endIndex <= startIndex) {
      return null;
    }

    const sliced = candidate.slice(startIndex, endIndex + 1);
    try {
      return JSON.parse(sliced);
    } catch {
      return null;
    }
  }
}

function parseActionListFromJson(rawJson) {
  const candidates = Array.isArray(rawJson)
    ? rawJson
    : Array.isArray(rawJson?.extraActions)
      ? rawJson.extraActions
      : Array.isArray(rawJson?.actions)
        ? rawJson.actions
        : Array.isArray(rawJson?.alternatives)
          ? rawJson.alternatives
          : [];

  return candidates
    .map((value) => normalizeActionHint(String(value)))
    .filter(Boolean)
    .filter((value, index, array) => array.indexOf(value) === index);
}

function buildImageIntentRouterPrompt({ mode, actionHint, voiceCommand }) {
  return [
    "You are the Cursivis image intent router.",
    "Analyze the image and decide the most useful next AI action.",
    "You are not restricted to a fixed action list. Use any concise snake_case action that genuinely fits the image.",
    "Return strict JSON only.",
    JSON.stringify(
      {
        contentType: "image",
        bestAction:
          "short snake_case action name; examples include describe_image, extract_key_details, identify_objects, extract_dominant_colors, generate_captions, ocr_extract_text, extract_table_data, translate",
        confidence: 0.0,
        alternatives: ["3-8 distinct snake_case actions"]
      },
      null,
      2
    ),
    `Mode: ${mode || "smart"}`,
    `Action hint: ${actionHint || "none"}`,
    `Voice command: ${voiceCommand || "none"}`,
    "Rules:",
    "- Prefer describe_image, identify_objects, extract_key_details, extract_dominant_colors, or generate_captions for natural photos, animals, people, scenes, and non-document images.",
    "- Use ocr_extract_text only if the image clearly contains readable text as a primary element.",
    "- Use extract_table_data only if the image clearly shows a table, chart, form, or structured document.",
    "- Do not suggest document-style actions for ordinary photos unless the image obviously supports them.",
    "Prioritize action utility for the user's likely intent."
  ].join("\n\n");
}

function buildTextImageIntentRouterPrompt({ text, mode, actionHint, voiceCommand }) {
  return [
    "You are the Cursivis multimodal intent router.",
    "The user has selected text and there is also an attached screenshot context from the same moment.",
    "Use the text as primary context and the screenshot as supporting context when it adds clarity.",
    "You are not choosing from a fixed action menu. bestAction may be any concise snake_case action that would genuinely help.",
    "Return strict JSON only.",
    JSON.stringify(
      {
        contentType: "broad content label such as question, mcq, code, email, report, product, social_caption, or general_text",
        bestAction: "short snake_case action name",
        confidence: 0.0,
        alternatives: ["3-8 distinct snake_case actions"]
      },
      null,
      2
    ),
    `Mode: ${mode || "smart"}`,
    `Action hint: ${actionHint || "none"}`,
    `Voice command: ${voiceCommand || "none"}`,
    "Selected text:",
    text.trim().slice(0, 7000)
  ].join("\n\n");
}

function buildTextDynamicOptionsPrompt({ text, contentType, currentOptions }) {
  const options = currentOptions.join(", ") || "none";
  return [
    "You generate additional executable action options for a contextual AI menu.",
    "Return strict JSON only.",
    JSON.stringify(
      {
        extraActions: ["3-8 new snake_case action names, different from current options"]
      },
      null,
      2
    ),
    `Content type: ${contentType}`,
    `Current options: ${options}`,
    "Rules:",
    "- Do not repeat existing options.",
    "- Keep actions concise and executable.",
    "- Prefer actions that are specifically useful for this exact selection.",
    "Selection text:",
    text.trim().slice(0, 7000)
  ].join("\n\n");
}

function buildImageDynamicOptionsPrompt({ contentType, currentOptions }) {
  const options = currentOptions.join(", ") || "none";
  return [
    "You generate additional executable action options for an image AI menu.",
    "Return strict JSON only.",
    JSON.stringify(
      {
        extraActions: ["3-8 new snake_case actions for this specific image"]
      },
      null,
      2
    ),
    `Image content type: ${contentType || "image"}`,
    `Current options: ${options}`,
    "Rules:",
    "- Do not repeat current options.",
    "- Suggest only actions that clearly fit the actual visible image content.",
    "- Prefer actions like identify_objects, extract_dominant_colors, describe_scene, generate_alt_text, or generate_captions for ordinary photos.",
    "- Suggest ocr_extract_text only when readable text is clearly present.",
    "- Suggest extract_table_data only for table/chart/document-like images.",
    "- Keep actions executable and concise."
  ].join("\n\n");
}

function buildTextImageDynamicOptionsPrompt({ text, contentType, currentOptions }) {
  const options = currentOptions.join(", ") || "none";
  return [
    "You generate additional executable action options for a multimodal AI menu.",
    "The user selected text and there is an attached screenshot context.",
    "Return strict JSON only.",
    JSON.stringify(
      {
        extraActions: ["3-8 new snake_case action names, different from current options"]
      },
      null,
      2
    ),
    `Content type: ${contentType}`,
    `Current options: ${options}`,
    "Rules:",
    "- Do not repeat existing options.",
    "- Use both the selected text and screenshot context when proposing new actions.",
    "- Keep actions concise and executable.",
    "Selected text:",
    text.trim().slice(0, 7000)
  ].join("\n\n");
}

function buildIntentRouterRecheckPrompt(concern) {
  return [
    "Self-check before final answer:",
    concern,
    "Re-evaluate the same selection from scratch.",
    "Return strict JSON only."
  ].join("\n\n");
}

function buildIntentRouterRequest({
  model,
  selectionKind,
  text,
  imageBase64,
  imageMimeType,
  mode,
  actionHint,
  voiceCommand,
  rerouteConcern = ""
}) {
  const recheckPrompt = rerouteConcern ? buildIntentRouterRecheckPrompt(rerouteConcern) : "";

  if (selectionKind === "image" && imageBase64) {
    return {
      model,
      contents: [
        {
          role: "user",
          parts: [
            {
              text: buildImageIntentRouterPrompt({
                mode,
                actionHint,
                voiceCommand
              })
            },
            {
              inlineData: {
                mimeType: imageMimeType || "image/png",
                data: imageBase64
              }
            }
          ]
        }
      ],
      config: {
        systemInstruction: INTENT_ROUTER_SYSTEM_INSTRUCTION,
        responseMimeType: "application/json",
        responseJsonSchema: INTENT_ROUTER_SCHEMA,
        temperature: 0.15
      }
    };
  }

  if (selectionKind === "text_image" && imageBase64) {
    return {
      model,
      contents: [
        {
          role: "user",
          parts: [
            {
              text: [
                buildTextImageIntentRouterPrompt({
                  text,
                  mode,
                  actionHint,
                  voiceCommand
                }),
                recheckPrompt
              ].filter(Boolean).join("\n\n")
            },
            {
              inlineData: {
                mimeType: imageMimeType || "image/png",
                data: imageBase64
              }
            }
          ]
        }
      ],
      config: {
        systemInstruction: INTENT_ROUTER_SYSTEM_INSTRUCTION,
        responseMimeType: "application/json",
        responseJsonSchema: INTENT_ROUTER_SCHEMA,
        temperature: 0.12
      }
    };
  }

  return {
    model,
    contents: [
      buildIntentRouterPrompt({
        text,
        mode,
        actionHint,
        voiceCommand
      }),
      recheckPrompt
    ].filter(Boolean).join("\n\n"),
    config: {
      systemInstruction: INTENT_ROUTER_SYSTEM_INSTRUCTION,
      responseMimeType: "application/json",
      responseJsonSchema: INTENT_ROUTER_SCHEMA,
      temperature: rerouteConcern ? 0.08 : 0.1
    }
  };
}

export function fallbackIntentDecision({ selectionKind, text }) {
  if (selectionKind === "image") {
      return {
        contentType: "image",
        bestAction: "describe_image",
        confidence: 0.7,
        alternatives: ["describe_image", "extract_key_details", "identify_objects", "extract_dominant_colors"]
      };
  }

  const contentType = inferFallbackType(text || "");
  const bestAction =
    contentType === "code"
      ? inferUsefulCodeAction(text || "")
      : null;
  return normalizeIntentDecision(
    {
      contentType,
      bestAction,
      confidence: 0.7,
      alternatives: []
    },
    text || ""
  );
}

export function createGeminiIntentRouter({
  model = process.env.GEMINI_ROUTER_MODEL || process.env.GEMINI_MODEL || DEFAULT_GEMINI_MODEL
} = {}) {
  const cache = new Map();

  return async ({
    selectionKind = "text",
    text = "",
    imageBase64 = "",
    imageMimeType = "image/png",
    mode = "smart",
    actionHint = "",
    voiceCommand = ""
  }) => {
    if (!hasConfiguredApiKeys()) {
      return fallbackIntentDecision({ selectionKind, text });
    }

    const startedAt = Date.now();
    const cacheKey =
      selectionKind === "text" && text.trim()
        ? `intent:${mode}:${actionHint}:${voiceCommand}:${text.trim().slice(0, 9000)}`
        : null;
    const cached = cacheKey ? readCache(cache, cacheKey) : null;
    if (cached) {
      return {
        ...cached,
        latencyMs: Math.max(1, Date.now() - startedAt),
        cached: true
      };
    }

    try {
      const candidateModels = buildModelCandidates(model);
      const retryContext = createRetryContext();
      const request = buildIntentRouterRequest({
        model,
        selectionKind,
        text,
        imageBase64,
        imageMimeType,
        mode,
        actionHint,
        voiceCommand
      });

      const { response, model: firstResolvedModel } = await withGoogleGenAiClient(
        (client, entry) => generateWithFallbackModels(
          client,
          request,
          candidateModels,
          false,
          { keyIndex: entry.index, retryContext }
        ),
        { canRetryError: isRetriableGeminiRequestError }
      );

      let parsed = parseJsonObject(response.text || "");
      let resolvedModel = firstResolvedModel;
      const rerouteConcern =
        selectionKind === "image"
          ? ""
          : describeIntentRoutingConcern(parsed, text);

      if (rerouteConcern) {
        try {
          const recheckRequest = buildIntentRouterRequest({
            model,
            selectionKind,
            text,
            imageBase64,
            imageMimeType,
            mode,
            actionHint,
            voiceCommand,
            rerouteConcern
          });
          const rechecked = await withGoogleGenAiClient(
            (client, entry) => generateWithFallbackModels(
              client,
              recheckRequest,
              candidateModels,
              false,
              { keyIndex: entry.index, retryContext }
            ),
            { canRetryError: isRetriableGeminiRequestError }
          );
          const reparsed = parseJsonObject(rechecked.response.text || "");
          if (reparsed) {
            parsed = reparsed;
            resolvedModel = rechecked.model;
          }
        } catch {
          // Keep the original Gemini routing result if the self-check pass fails.
        }
      }

      if (!parsed) {
        throw new Error("Gemini intent router returned invalid JSON.");
      }

      const normalizedParsed =
        selectionKind === "image"
          ? {
              ...(parsed || {}),
              contentType: "image"
            }
          : parsed;
      const normalizedDecision = normalizeIntentDecision(normalizedParsed, text);
      const decision = {
        ...normalizedDecision,
        latencyMs: Date.now() - startedAt,
        model: resolvedModel
      };
      if (cacheKey) {
        writeCache(cache, cacheKey, decision);
      }

      return decision;
    } catch (error) {
      throw error;
    }
  };
}

export function createGeminiOptionGenerator({
  model = process.env.GEMINI_OPTIONS_MODEL || process.env.GEMINI_MODEL || DEFAULT_GEMINI_MODEL
} = {}) {
  const cache = new Map();

  return async ({
    selectionKind = "text",
    text = "",
    imageBase64 = "",
    imageMimeType = "image/png",
    contentType = "general_text",
    currentOptions = []
  }) => {
    if (!hasConfiguredApiKeys()) {
      return [];
    }

    const normalizedCurrent = currentOptions.map((value) => normalizeActionHint(String(value))).filter(Boolean);
    const cacheKey =
      selectionKind === "text" && text.trim()
        ? `options:${contentType}:${normalizedCurrent.join(",")}:${text.trim().slice(0, 6000)}`
        : null;
    const cached = cacheKey ? readCache(cache, cacheKey) : null;
    if (cached) {
      return cached;
    }

    try {
      const request =
        selectionKind === "image" && imageBase64
          ? {
              model,
              contents: [
                {
                  role: "user",
                  parts: [
                    {
                      text: buildImageDynamicOptionsPrompt({
                        contentType,
                        currentOptions: normalizedCurrent
                      })
                    },
                    {
                      inlineData: {
                        mimeType: imageMimeType || "image/png",
                        data: imageBase64
                      }
                    }
                  ]
                }
              ],
              config: {
                systemInstruction: DYNAMIC_OPTIONS_SYSTEM_INSTRUCTION,
                responseMimeType: "application/json",
                responseJsonSchema: DYNAMIC_OPTIONS_SCHEMA,
                temperature: 0.35
              }
            }
          : selectionKind === "text_image" && imageBase64
            ? {
                model,
                contents: [
                  {
                    role: "user",
                    parts: [
                      {
                        text: buildTextImageDynamicOptionsPrompt({
                          text,
                          contentType,
                          currentOptions: normalizedCurrent
                        })
                      },
                      {
                        inlineData: {
                          mimeType: imageMimeType || "image/png",
                          data: imageBase64
                        }
                      }
                    ]
                  }
                ],
                config: {
                  systemInstruction: DYNAMIC_OPTIONS_SYSTEM_INSTRUCTION,
                  responseMimeType: "application/json",
                  responseJsonSchema: DYNAMIC_OPTIONS_SCHEMA,
                  temperature: 0.35
                }
              }
          : {
              model,
              contents: buildTextDynamicOptionsPrompt({
                text,
                contentType,
                currentOptions: normalizedCurrent
              }),
              config: {
                systemInstruction: DYNAMIC_OPTIONS_SYSTEM_INSTRUCTION,
                responseMimeType: "application/json",
                responseJsonSchema: DYNAMIC_OPTIONS_SCHEMA,
                temperature: 0.35
              }
            };

      const optionRetryContext = createRetryContext({ budgetMs: DEFAULT_OPTIONS_BUDGET_MS });
      const { response } = await withGoogleGenAiClient(
        (client, entry) => generateWithFallbackModels(
          client,
          request,
          buildModelCandidates(model),
          false,
          {
            keyIndex: entry.index,
            retryContext: optionRetryContext
          }
        ),
        { canRetryError: isRetriableGeminiRequestError }
      );
      const parsed = parseJsonObject(response.text || "");
      const generated = parseActionListFromJson(parsed).filter((action) => !normalizedCurrent.includes(action)).slice(0, 10);
      if (cacheKey) {
        writeCache(cache, cacheKey, generated);
      }

      return generated;
    } catch {
      return [];
    }
  };
}

function buildModelCandidates(primaryModel) {
  const envModels = (process.env.GEMINI_FALLBACK_MODELS || "")
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean);

  return [primaryModel, ...envModels, ...DEFAULT_FALLBACK_MODELS]
    .filter(Boolean)
    .filter((value) => !RETIRED_FALLBACK_MODELS.has(value))
    .filter((value, index, array) => array.indexOf(value) === index);
}

export async function generateWithFallbackModels(
  client,
  request,
  candidateModels,
  useGrounding,
  {
    keyIndex,
    retryContext = createRetryContext(),
    retryDelaysMs = TRANSIENT_RETRY_DELAYS_MS
  } = {}
) {
  let lastError = null;

  for (let modelIndex = 0; modelIndex < candidateModels.length; modelIndex += 1) {
    const candidateModel = candidateModels[modelIndex];
    if (modelIndex > 0) {
      traceGeminiEvent("model_fallback", {
        keyIndex: Number.isInteger(keyIndex) ? keyIndex + 1 : "unknown",
        fromModel: candidateModels[modelIndex - 1],
        toModel: candidateModel
      });
    }

    for (let attempt = 0; attempt <= retryDelaysMs.length; attempt += 1) {
      const remainingMs = retryContext.deadlineAt - Date.now();
      if (remainingMs <= 0) {
        const timeoutError = new Error("504 TIMEOUT: Gemini recovery budget was exhausted.");
        timeoutError.code = 504;
        throw timeoutError;
      }

      const startedAt = Date.now();
      try {
        const attemptTimeoutMs = Math.max(1000, Math.min(resolveAttemptTimeoutMs(), remainingMs));
        const budgetSignal = AbortSignal.timeout(attemptTimeoutMs);
        const abortSignal = request.config?.abortSignal
          ? AbortSignal.any([request.config.abortSignal, budgetSignal])
          : budgetSignal;
        const compatibleConfig = buildModelCompatibleConfig(request.config, candidateModel);
        const response = await client.models.generateContent({
          ...request,
          model: candidateModel,
          config: {
            ...compatibleConfig,
            abortSignal,
            httpOptions: {
              ...(compatibleConfig.httpOptions || {}),
              timeout: Math.max(10_000, attemptTimeoutMs),
              retryOptions: {
                ...(request.config?.httpOptions?.retryOptions || {}),
                attempts: 1
              }
            }
          }
        });

        traceGeminiAttempt({
          outcome: "success",
          keyIndex,
          model: candidateModel,
          attempt: attempt + 1,
          durationMs: Date.now() - startedAt
        });

        return {
          response,
          model: candidateModel
        };
      } catch (error) {
        lastError = error;
        const errorCategory = classifyGeminiError(error);
        const transient = errorCategory === "transient" || errorCategory === "transport_security";
        const fallbackEligible = transient || isQuotaOrRateLimitError(error) || isModelAvailabilityError(error) || (useGrounding && isGroundingToolError(error));

        traceGeminiAttempt({
          outcome: "failure",
          keyIndex,
          model: candidateModel,
          attempt: attempt + 1,
          durationMs: Date.now() - startedAt,
          error
        });

        if (!fallbackEligible) {
          throw error;
        }

        if (transient && attempt < retryDelaysMs.length) {
          const backoffMs = Math.min(retryDelaysMs[attempt], Math.max(0, retryContext.deadlineAt - Date.now()));
          if (backoffMs > 0) {
            await delay(backoffMs);
          }
          continue;
        }

        if (errorCategory === "transport_security") {
          throw error;
        }

        break;
      }
    }
  }

  throw lastError ?? new Error("Gemini request failed.");
}

function buildModelCompatibleConfig(config = {}, model = "") {
  const compatible = { ...config };
  delete compatible.numPredict;

  if (/^gemini-(?:3\.6-flash|3\.5-flash-lite)(?:$|-)/i.test(model)) {
    delete compatible.temperature;
    delete compatible.topP;
    delete compatible.topK;
  }

  return compatible;
}

function createRetryContext({ budgetMs = resolveOperationBudgetMs() } = {}) {
  return {
    deadlineAt: Date.now() + Math.max(1000, budgetMs)
  };
}

function resolveOperationBudgetMs() {
  return readPositiveInteger(process.env.GEMINI_OPERATION_BUDGET_MS, DEFAULT_OPERATION_BUDGET_MS);
}

function resolveAttemptTimeoutMs() {
  return readPositiveInteger(process.env.GEMINI_ATTEMPT_TIMEOUT_MS, DEFAULT_ATTEMPT_TIMEOUT_MS);
}

function readPositiveInteger(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : fallback;
}

function delay(durationMs) {
  return new Promise((resolve) => setTimeout(resolve, durationMs));
}

function traceGeminiAttempt({ outcome, keyIndex, model, attempt, durationMs, error }) {
  traceGeminiEvent(`model_${outcome}`, {
    keyIndex: Number.isInteger(keyIndex) ? keyIndex + 1 : "unknown",
    model,
    attempt,
    durationMs: Math.max(0, Math.round(durationMs || 0)),
    ...(error ? getGeminiErrorDiagnostics(error) : {})
  });
}

function isGroundingToolError(error) {
  const message = error instanceof Error ? error.message : String(error);
  return /googleSearch|tool/i.test(message);
}

function buildTextCacheKey({ model, prompt, useGrounding, config }) {
  if (typeof prompt !== "string" || !prompt.trim()) {
    return null;
  }

  if (prompt.length > 12000) {
    return null;
  }

  return JSON.stringify({
    model,
    prompt,
    useGrounding,
    responseMimeType: config?.responseMimeType || "text/plain"
  });
}

function buildExecutionSystemInstruction({ selectionType, action }) {
  const normalizedSelectionType = selectionType || "general_text";
  const normalizedAction = action || "unspecified_action";

  return [
    CONTEXTUAL_EXECUTION_SYSTEM_INSTRUCTION,
    `Detected content type: ${normalizedSelectionType}.`,
    `Chosen action: ${normalizedAction}.`,
    "Return only the final user-facing result."
  ].join(" ");
}

function readCache(cache, key) {
  const entry = cache.get(key);
  if (!entry) {
    return null;
  }

  if (Date.now() - entry.createdAt > DEFAULT_CACHE_TTL_MS) {
    cache.delete(key);
    return null;
  }

  return entry.value;
}

function writeCache(cache, key, value) {
  cache.set(key, {
    createdAt: Date.now(),
    value
  });

  if (cache.size <= DEFAULT_CACHE_LIMIT) {
    return;
  }

  const oldestKey = cache.keys().next().value;
  if (oldestKey) {
    cache.delete(oldestKey);
  }
}
