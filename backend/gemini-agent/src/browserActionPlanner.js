import { detectBrowserTaskPack } from "./browserTaskPacks.js";

const ALLOWED_TOOLS = new Set([
  "navigate",
  "click_role",
  "click_text",
  "fill_label",
  "fill_name",
  "fill_placeholder",
  "fill_editor",
  "type_active",
  "select_option",
  "check_radio",
  "check_checkbox",
  "open_new_tab",
  "switch_tab",
  "press_key",
  "scroll",
  "extract_dom",
  "wait_for_text",
  "wait_ms",
  "apply_answer_key"
]);

const MAX_ANSWER_KEY_ENTRIES = 128;

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

    try {
      return JSON.parse(candidate.slice(startIndex, endIndex + 1));
    } catch {
      return null;
    }
  }
}

function normalizeStep(step) {
  if (!step || typeof step !== "object") {
    return null;
  }

  const tool = typeof step.tool === "string" ? step.tool.trim().toLowerCase() : "";
  if (!ALLOWED_TOOLS.has(tool)) {
    return null;
  }

  const normalized = { tool };
  for (const key of [
    "role",
    "name",
    "text",
    "label",
    "nameAttribute",
    "placeholder",
    "question",
    "option",
    "url",
    "key"
  ]) {
    if (typeof step[key] === "string" && step[key].trim()) {
      normalized[key] = step[key].trim();
    }
  }

  if (Array.isArray(step.answers)) {
    const answers = step.answers
      .map((answer) => ({
        question: typeof answer?.question === "string" && answer.question.trim() ? answer.question.trim() : undefined,
        option: typeof answer?.option === "string" ? sanitizeAnswerOption(answer.option) : "",
        questionIndex: Number.isInteger(answer?.questionIndex) && answer.questionIndex > 0
          ? answer.questionIndex
          : undefined,
        choiceIndex: Number.isInteger(answer?.choiceIndex) && answer.choiceIndex >= 0
          ? answer.choiceIndex
          : undefined
      }))
      .filter((answer) => answer.option)
      .slice(0, MAX_ANSWER_KEY_ENTRIES);

    if (answers.length > 0) {
      normalized.answers = answers;
    }
  }

  if (typeof step.advancePages === "boolean") {
    normalized.advancePages = step.advancePages;
  }

  if (Number.isFinite(step.waitMs) && step.waitMs > 0) {
    normalized.waitMs = Math.min(5000, Math.round(step.waitMs));
  }

  return isStepExecutable(normalized) ? normalized : null;
}

function isStepExecutable(step) {
  switch (step.tool) {
    case "navigate":
    case "open_new_tab":
      return Boolean(step.url);
    case "switch_tab":
      return Boolean(step.name || step.text || step.url);
    case "click_role":
      return Boolean(step.role && (step.name || step.text));
    case "click_text":
      return Boolean(step.text || step.name);
    case "fill_label":
      return Boolean((step.label || step.name) && typeof step.text === "string");
    case "fill_name":
      return Boolean((step.nameAttribute || step.name) && typeof step.text === "string");
    case "fill_placeholder":
      return Boolean((step.placeholder || step.label || step.name) && typeof step.text === "string");
    case "fill_editor":
      return Boolean((step.label || step.name || "Message") && typeof step.text === "string");
    case "type_active":
      return typeof step.text === "string" && step.text.length > 0;
    case "select_option":
      return Boolean((step.option || step.text) && (step.label || step.name || step.option || step.text));
    case "check_radio":
    case "check_checkbox":
      return Boolean(step.option || step.label || step.name);
    case "press_key":
      return Boolean(step.key);
    case "scroll":
      return true;
    case "extract_dom":
      return true;
    case "wait_for_text":
      return Boolean(step.text || step.name);
    case "wait_ms":
      return Number.isFinite(step.waitMs) && step.waitMs > 0;
    case "apply_answer_key":
      return Array.isArray(step.answers) && step.answers.length > 0;
    default:
      return false;
  }
}

function sanitizePlan(plan) {
  const parsedSteps = Array.isArray(plan?.steps)
    ? plan.steps.map(normalizeStep).filter(Boolean).slice(0, 16)
    : [];

  return {
    goal: typeof plan?.goal === "string" && plan.goal.trim()
      ? plan.goal.trim()
      : "browser_action",
    summary: typeof plan?.summary === "string" && plan.summary.trim()
      ? plan.summary.trim()
      : "Apply the generated result to the current browser page.",
    requiresConfirmation: Boolean(plan?.requiresConfirmation),
    steps: parsedSteps
  };
}

function containsAny(text = "", values = []) {
  const normalized = String(text).toLowerCase();
  return values.some((value) => normalized.includes(String(value).toLowerCase()));
}

function normalizeText(value = "") {
  return String(value)
    .toLowerCase()
    .replace(/\s+/g, " ")
    .trim();
}

function textMatches(left = "", right = "") {
  const normalizedLeft = normalizeText(left);
  const normalizedRight = normalizeText(right);
  if (!normalizedLeft || !normalizedRight) {
    return false;
  }

  return normalizedLeft.includes(normalizedRight) || normalizedRight.includes(normalizedLeft);
}

function isMailLike({ taskPack, contentType, action, voiceCommand }) {
  const routingText = `${action} ${voiceCommand}`;
  return (
    taskPack?.id === "mail_compose" ||
    String(contentType).toLowerCase() === "email" ||
    containsAny(routingText, ["email", "mail", "compose", "schedule send"])
  );
}

function isFormsLike({ taskPack, contentType, voiceCommand }) {
  const normalizedType = String(contentType).toLowerCase();
  return (
    taskPack?.id === "google_forms" ||
    taskPack?.id === "qa_form" ||
    normalizedType === "mcq" ||
    (normalizedType === "question" && containsAny(voiceCommand, ["fill", "autofill", "check", "tick", "mark", "select"]))
  );
}

function isDiscordLike({ taskPack, voiceCommand, browserContext }) {
  return (
    taskPack?.id === "discord" ||
    containsAny(`${voiceCommand} ${browserContext?.url} ${browserContext?.title}`, ["discord", "dm", "direct message", "channel"])
  );
}

function looksLikeProductText(...values) {
  return containsAny(values.join(" "), [
    "iphone",
    "phone case",
    "phone cover",
    "cover",
    "case",
    "back cover",
    "flipkart",
    "amazon",
    "price",
    "buy",
    "purchase",
    "compare",
    "review"
  ]);
}

function isShoppingLike({ taskPack, contentType, action, voiceCommand, browserContext, originalText = "", resultText = "" }) {
  return (
    taskPack?.id === "shopping" ||
    String(contentType).toLowerCase() === "product" ||
    containsAny(String(action || "").toLowerCase(), ["extract_product_info", "compare_prices", "find_reviews"]) ||
    looksLikeProductText(
      voiceCommand,
      browserContext?.url,
      browserContext?.title,
      originalText,
      resultText
    )
  );
}

function isRiskyBrowserAction(voiceCommand = "") {
  return containsAny(voiceCommand, ["send", "schedule", "submit", "delete", "purchase", "buy", "checkout"]);
}

function combinePlannerInstruction(voiceCommand = "", executionInstruction = "") {
  const normalizedVoice = String(voiceCommand || "").trim();
  const normalizedExecution = String(executionInstruction || "").trim();

  if (!normalizedVoice) {
    return normalizedExecution;
  }

  if (!normalizedExecution) {
    return normalizedVoice;
  }

  if (normalizeText(normalizedVoice) === normalizeText(normalizedExecution)) {
    return normalizedExecution;
  }

  return `${normalizedVoice}. ${normalizedExecution}`;
}

function parseShoppingPreference(instruction = "") {
  const normalized = normalizeText(instruction);
  const wantsAmazon = containsAny(normalized, ["amazon"]);
  const wantsFlipkart = containsAny(normalized, ["flipkart", "flip cart"]);
  const excludesAmazon = containsAny(normalized, ["not amazon", "do not open amazon", "don't open amazon", "without amazon"]);
  const excludesFlipkart = containsAny(normalized, ["not flipkart", "not flip cart", "do not open flipkart", "don't open flipkart", "without flipkart"]);
  const amazonOnly = /\b(?:only|just)\s+amazon\b/i.test(instruction) || (wantsAmazon && excludesFlipkart);
  const flipkartOnly = /\b(?:only|just)\s+flipkart\b/i.test(instruction) || /\b(?:only|just)\s+flip cart\b/i.test(instruction) || (wantsFlipkart && excludesAmazon);

  return {
    wantsAmazon,
    wantsFlipkart,
    excludesAmazon,
    excludesFlipkart,
    amazonOnly,
    flipkartOnly,
    compareRequested: containsAny(normalized, ["compare", "compare prices", "price compare"])
  };
}

function containsWholePhrase(haystack = "", phrase = "") {
  const escaped = String(phrase).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`(^|\\b)${escaped}(\\b|$)`, "i").test(String(haystack || ""));
}

function isMailDestination(url = "") {
  return containsAny(String(url || ""), [
    "mail.google.com",
    "outlook.office.com",
    "outlook.live.com"
  ]);
}

function isMailStep(step) {
  const primaryText = `${step?.name || ""} ${step?.text || ""} ${step?.label || ""}`.toLowerCase();
  return (
    ((step?.tool === "open_new_tab" || step?.tool === "navigate") && isMailDestination(step?.url)) ||
    containsAny(primaryText, [
      "compose",
      "reply all",
      "reply",
      "to recipients",
      "subject",
      "more send options"
    ])
  );
}

function looksLikeMailPage(browserContext) {
  if (isMailDestination(browserContext?.url)) {
    return true;
  }

  const surfaceText = `${browserContext?.title || ""} ${browserContext?.visibleText || ""}`;
  return [
    "compose",
    "new message",
    "inbox",
    "reply",
    "reply all",
    "schedule send"
  ].some((phrase) => containsWholePhrase(surfaceText, phrase));
}

function isDiscordStep(step) {
  const primaryText = `${step?.name || ""} ${step?.text || ""} ${step?.label || ""} ${step?.url || ""}`.toLowerCase();
  return containsAny(primaryText, ["discord", "direct messages", "channel", "message @"]);
}

function isFormsPlanCompatible({ plan, originalText, resultText }) {
  if (!plan?.steps?.length) {
    return false;
  }

  if (plan.steps.some((step) => isMailStep(step) || isDiscordStep(step))) {
    return false;
  }

  return isQuizPlanPlausible({ plan, originalText, resultText });
}

function isTaskPackCompatiblePlan({ plan, taskPack, originalText, resultText }) {
  const taskPackId = taskPack?.id;
  if (!taskPackId) {
    return true;
  }

  switch (taskPackId) {
    case "google_forms":
    case "qa_form":
      return isFormsPlanCompatible({ plan, originalText, resultText });
    case "mail_compose":
      return !plan.steps.some((step) => step.tool === "apply_answer_key" || isDiscordStep(step));
    case "discord":
      return !plan.steps.some((step) => step.tool === "apply_answer_key" || isMailStep(step));
    case "shopping":
      return !plan.steps.some((step) => step.tool === "apply_answer_key" || isMailStep(step));
    default:
      return true;
  }
}

function extractEmailAddress(...values) {
  for (const value of values) {
    const match = String(value || "").match(/\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/i);
    if (match) {
      return match[0];
    }
  }

  return "";
}

function extractSubject(originalText = "", resultText = "") {
  for (const source of [originalText, resultText]) {
    const match = String(source).match(/^\s*subject:\s*(.+)$/im);
    if (match?.[1]) {
      return match[1].trim().slice(0, 140);
    }
  }

  const firstLine = String(originalText).split(/\r?\n/).map((line) => line.trim()).find(Boolean);
  if (!firstLine) {
    return "";
  }

  return firstLine.length <= 120 ? firstLine : firstLine.slice(0, 117).trimEnd() + "...";
}

function stripEmailBody(resultText = "") {
  return String(resultText)
    .replace(/^\s*subject:\s*.+$/im, "")
    .trim();
}

function extractSearchQuery(...candidates) {
  for (const candidate of candidates) {
    const trimmed = String(candidate || "").trim();
    if (!trimmed) {
      continue;
    }

    const cleaned = trimmed
      .replace(/\b(open|only|just|please|new tab|tab|search|search for|find|compare|compare prices|price compare|add to cart|buy|purchase|amazon|flipkart|flip cart|walmart|for me|on|in|from|not|don't|do not|without|and then|then|and)\b/gi, " ")
      .replace(/\s+/g, " ")
      .trim();
    if (cleaned) {
      return cleaned.slice(0, 120);
    }
  }

  return "";
}

function parseAnswerKey(resultText = "") {
  const answers = [];
  const lines = String(resultText)
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);

  for (const line of lines) {
    const parsedEntries = parseAnswerKeyLine(line);
    if (parsedEntries.length > 0) {
      answers.push(...parsedEntries);
      continue;
    }

    const answerMatch = line.match(/^(?:answer|best answer|correct answer)\s*[:.-]\s*(.+?)(?:\s+-\s+.+)?$/i);
    if (answerMatch) {
      const option = sanitizeAnswerOption(answerMatch[1]);
      if (option) {
        answers.push({
          question: "",
          option
        });
      }
    }
  }

  return answers.slice(0, MAX_ANSWER_KEY_ENTRIES);
}

function splitCompoundAnswerOption(option = "") {
  const cleaned = sanitizeAnswerOption(option);
  if (!cleaned || !cleaned.includes(",") || /[.!?;:]/.test(cleaned)) {
    return [cleaned].filter(Boolean);
  }

  const parts = cleaned
    .split(/\s*,\s*/)
    .map((part) => sanitizeAnswerOption(part))
    .filter(Boolean);

  if (parts.length < 2 || parts.length > 10) {
    return [cleaned];
  }

  const looksLikeChecklist = parts.every((part) => {
    const words = part.split(/\s+/).filter(Boolean);
    return words.length > 0 &&
      words.length <= 5 &&
      !part.includes("=") &&
      !/\b(is|are|was|were|be|been|being|have|has|had|do|does|did|can|could|should|would|may|might|must|will|shall|perform|performs|performing|enable|enables|enabling|contain|contains|containing|turn|turns|turning|necessary|because|therefore)\b/i.test(part);
  });

  return looksLikeChecklist ? parts : [cleaned];
}

function buildAnswerEntry({ question = "", option = "", questionIndex = undefined, choiceIndex = undefined }) {
  const sanitizedOption = sanitizeAnswerOption(option);
  if (!sanitizedOption) {
    return null;
  }

  const normalizedQuestionIndex = Number.isInteger(questionIndex) && questionIndex > 0
    ? questionIndex
    : undefined;
  const normalizedChoiceIndex = Number.isInteger(choiceIndex) && choiceIndex >= 0
    ? choiceIndex
    : undefined;

  return {
    question: question || "",
    option: sanitizedOption,
    questionIndex: normalizedQuestionIndex,
    choiceIndex: normalizedChoiceIndex
  };
}

function parseChoiceDescriptor(value = "") {
  const cleaned = sanitizeAnswerOption(value);
  if (!cleaned) {
    return null;
  }

  const choiceIndex = extractChoiceIndex(cleaned);
  return {
    option: cleaned,
    choiceIndex: choiceIndex >= 0 ? choiceIndex : undefined
  };
}

function expandAnswerEntry(question, descriptor, questionIndex = undefined) {
  const parts = splitCompoundAnswerOption(descriptor.option);
  const expanded = parts
    .map((part) => buildAnswerEntry({
      question,
      questionIndex,
      option: part,
      choiceIndex: parts.length === 1 ? descriptor.choiceIndex : undefined
    }))
    .filter(Boolean);

  return expanded;
}

function extractChoiceIndex(value = "") {
  const normalizedValue = normalizeText(value);
  if (!normalizedValue) {
    return -1;
  }

  const letterMatch = normalizedValue.match(/^(?:option\s*)?([a-z])(?:[\).:-]|\s|$)/i);
  if (letterMatch) {
    const index = letterMatch[1].toLowerCase().charCodeAt(0) - 97;
    return index >= 0 && index < 26 ? index : -1;
  }

  const numericMatch = normalizedValue.match(/^(?:option\s*)?(\d+)(?:[\).:-]|\s|$)/);
  if (numericMatch) {
    const index = Number.parseInt(numericMatch[1], 10) - 1;
    return Number.isFinite(index) && index >= 0 ? index : -1;
  }

  return -1;
}

function extractQuestionIndex(value = "") {
  const match = String(value || "").match(/\bq(?:uestion)?\s*(\d+)\b/i);
  if (!match?.[1]) {
    return undefined;
  }

  const questionIndex = Number.parseInt(match[1], 10);
  return Number.isInteger(questionIndex) && questionIndex > 0
    ? questionIndex
    : undefined;
}

function parseAnswerKeyLine(line = "") {
  const bracketedMatch = line.match(/^(?:q(?:uestion)?\s*)?(\d+)?\s*(?:\[(.+?)\])\s*:\s*(.+?)(?:\s+-\s+.+)?$/i);
  if (bracketedMatch) {
    const descriptor = parseChoiceDescriptor(bracketedMatch[3]);
    if (!descriptor) {
      return [];
    }

    return expandAnswerEntry(
      (bracketedMatch[2] || "").trim() || (bracketedMatch[1] ? `Question ${bracketedMatch[1]}` : ""),
      descriptor,
      bracketedMatch[1] ? Number.parseInt(bracketedMatch[1], 10) : undefined
    );
  }

  const numberedQuestionMatch = line.match(/^(?:q(?:uestion)?\s*)?(\d+)\s+(.+?)\s*:\s*(.+?)(?:\s+-\s+.+)?$/i);
  if (numberedQuestionMatch) {
    const descriptor = parseChoiceDescriptor(numberedQuestionMatch[3]);
    if (!descriptor) {
      return [];
    }

    return expandAnswerEntry(
      (numberedQuestionMatch[2] || "").trim() || `Question ${numberedQuestionMatch[1]}`,
      descriptor,
      Number.parseInt(numberedQuestionMatch[1], 10)
    );
  }

  const simpleQuestionMatch = line.match(/^(.+?)\s*:\s*(.+?)(?:\s+-\s+.+)?$/);
  if (simpleQuestionMatch && /\b(q(?:uestion)?\s*\d+|find|term|difference|sequence|value|next term|common difference|negative term|sum)\b/i.test(simpleQuestionMatch[1])) {
    const descriptor = parseChoiceDescriptor(simpleQuestionMatch[2]);
    if (!descriptor) {
      return [];
    }

    return expandAnswerEntry(
      (simpleQuestionMatch[1] || "").trim(),
      descriptor,
      extractQuestionIndex(simpleQuestionMatch[1])
    );
  }

  const numberedAnswerMatch = line.match(/^(?:q(?:uestion)?\s*)?(\d+)\s*[\).:-]\s*(.+?)$/i);
  if (numberedAnswerMatch) {
    const descriptor = parseChoiceDescriptor(numberedAnswerMatch[2]);
    if (!descriptor) {
      return [];
    }

    return expandAnswerEntry(
      `Question ${numberedAnswerMatch[1]}`,
      descriptor,
      Number.parseInt(numberedAnswerMatch[1], 10)
    );
  }

  const bareChoiceMatch = line.match(/^(?:[-*]\s*)?([a-z]|\d+)(?:[\).:-]|\s+)(.+)?$/i);
  if (bareChoiceMatch) {
    const descriptor = parseChoiceDescriptor(
      bareChoiceMatch[2]?.trim()
        ? `${bareChoiceMatch[1]} ${bareChoiceMatch[2].trim()}`
        : bareChoiceMatch[1]
    );
    if (descriptor) {
      return expandAnswerEntry("", descriptor);
    }
  }

  return [];
}

function sanitizeAnswerOption(value = "") {
  const cleaned = String(value)
    .replace(/^[\s"'`]+|[\s"'`]+$/g, "")
    .replace(/^(option|answer)\s+/i, "")
    .trim();

  if (
    !cleaned ||
    /^(needs user input|user input required|not applicable|n\/a|skip|cannot infer)(?:[.!?])?$/i.test(cleaned)
  ) {
    return "";
  }

  return cleaned;
}

function extractQuestionLabel(sourceText = "") {
  const lines = String(sourceText)
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);

  for (const line of lines) {
    if (/^(?:[a-d]|\d+)[\).:-]\s+/i.test(line)) {
      break;
    }

    if (line.length > 6) {
      return line;
    }
  }

  return "";
}

function extractOptionTexts(sourceText = "") {
  const normalizeVisibleOptionText = (line = "") => {
    const trimmed = String(line || "").trim();
    const match = trimmed.match(/^(?:[a-d]|\d+)[\).:-]\s+(.+)$/i);
    return match?.[1]?.trim() || "";
  };

  return String(sourceText)
    .split(/\r?\n/)
    .map((line) => line.trim())
    .map((line) => normalizeVisibleOptionText(line))
    .filter(Boolean)
    .slice(0, 12);
}

function isSelectionMetaLine(line = "") {
  return /^(?:\d+\s*points?|\d+\s*point|clear selection|clear form|submit|back|next|previous|required|optional|page\s+\d+(?:\s+of\s+\d+)?)$/i
    .test(String(line || "").trim());
}

function isSelectionControlHint(line = "") {
  return /^(?:choose|check)\s+all\s+that\s+apply$|^(?:multiple choice|short answer|short answer text|paragraph|long answer text|linear scale|dropdown|date|time|file upload|your answer)$/i
    .test(String(line || "").trim());
}

function isSelectionBooleanOption(line = "") {
  return /^(?:true|false|yes|no)$/i.test(String(line || "").trim());
}

function isLikelySelectionOptionLine(line = "") {
  const trimmed = String(line || "").trim();
  if (!trimmed || isSelectionMetaLine(trimmed) || isSelectionControlHint(trimmed)) {
    return false;
  }

  if (/^(?:[a-z]|\d+)[\).:-]\s+/.test(trimmed) || isSelectionBooleanOption(trimmed)) {
    return true;
  }

  if (/[?]$/.test(trimmed) || /[:;]$/.test(trimmed)) {
    return false;
  }

  const words = trimmed.split(/\s+/).filter(Boolean);
  return words.length > 0 &&
    words.length <= 8 &&
    trimmed.length <= 90 &&
    !/[.]$/.test(trimmed);
}

function isLikelySelectionQuestionLine(line = "", nextLine = "", hasCurrentBlock = false) {
  const trimmed = String(line || "").trim();
  const nextTrimmed = String(nextLine || "").trim();
  if (!trimmed || isSelectionMetaLine(trimmed) || isSelectionControlHint(trimmed)) {
    return false;
  }

  if (/[?]$/.test(trimmed)) {
    return true;
  }

  if (nextTrimmed && (isSelectionControlHint(nextTrimmed) || isSelectionBooleanOption(nextTrimmed))) {
    return trimmed.length > 8;
  }

  if (isLikelySelectionOptionLine(trimmed)) {
    return false;
  }

  if (nextTrimmed && isLikelySelectionOptionLine(nextTrimmed)) {
    return trimmed.length > 8;
  }

  return !hasCurrentBlock && /[.]$/.test(trimmed) && trimmed.length > 12;
}

function extractLeadingQuestionMarker(line = "") {
  const match = String(line || "").trim().match(/^(?:q(?:uestion)?\s*)?(\d+)\s*[\).:-]?\s+(.+)$/i);
  if (!match?.[1] || !match?.[2]) {
    return null;
  }

  const questionIndex = Number.parseInt(match[1], 10);
  if (!Number.isInteger(questionIndex) || questionIndex <= 0) {
    return null;
  }

  return {
    questionIndex,
    questionText: match[2].trim()
  };
}

function parseSelectionQuestionBlocks(sourceText = "") {
  const normalizeVisibleOptionText = (line = "") => {
    const trimmed = String(line || "").trim();
    const match = trimmed.match(/^(?:[a-d]|\d+)[\).:-]\s+(.+)$/i);
    return match?.[1]?.trim() || trimmed;
  };

  const lines = String(sourceText)
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);

  const blocks = [];
  let currentBlock = null;

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    if (isSelectionMetaLine(line)) {
      continue;
    }

    const nextLine = lines.slice(index + 1).find((candidate) => !isSelectionMetaLine(candidate)) || "";
    const explicitMarker = extractLeadingQuestionMarker(line);
    if (explicitMarker) {
      currentBlock = {
        questionIndex: explicitMarker.questionIndex,
        questionText: explicitMarker.questionText,
        optionTexts: []
      };
      blocks.push(currentBlock);
      continue;
    }

    if (isLikelySelectionQuestionLine(line, nextLine, currentBlock !== null)) {
      currentBlock = {
        questionIndex: blocks.length + 1,
        questionText: line,
        optionTexts: []
      };
      blocks.push(currentBlock);
      continue;
    }

    if (!currentBlock || isSelectionControlHint(line)) {
      continue;
    }

    currentBlock.optionTexts.push(normalizeVisibleOptionText(line));
  }

  return blocks;
}

function resolveSelectionQuestionBlock(answer, selectionBlocks = []) {
  const explicitQuestionIndex = Number.isInteger(answer?.questionIndex) && answer.questionIndex > 0
    ? answer.questionIndex
    : undefined;
  if (explicitQuestionIndex !== undefined) {
    const exactBlock = selectionBlocks.find((block) => block.questionIndex === explicitQuestionIndex);
    if (exactBlock) {
      return exactBlock;
    }
  }

  const desiredQuestion = normalizeText(answer?.question || "");
  if (!desiredQuestion) {
    return null;
  }

  const tokenizeQuestionMatch = (value = "") => normalizeText(value)
    .split(/[^a-z0-9]+/)
    .filter((token) => token.length >= 3);

  let bestBlock = null;
  let bestScore = 0;
  for (const block of selectionBlocks) {
    const normalizedQuestion = normalizeText(block.questionText);
    if (!normalizedQuestion) {
      continue;
    }

    let score = 0;
    if (textMatches(normalizedQuestion, desiredQuestion)) {
      score += 100;
    } else {
      const desiredTokens = tokenizeQuestionMatch(desiredQuestion);
      const questionTokens = tokenizeQuestionMatch(normalizedQuestion);
      score += desiredTokens.filter((token) => questionTokens.some((candidate) => candidate.includes(token) || token.includes(candidate))).length * 18;
    }

    if (score > bestScore) {
      bestScore = score;
      bestBlock = block;
    }
  }

  return bestScore >= 18 ? bestBlock : null;
}

function pickBestMatchingOption(answerText = "", candidateOptions = []) {
  const cleanedAnswer = sanitizeAnswerOption(answerText);
  if (!cleanedAnswer || candidateOptions.length === 0) {
    return "";
  }

  const normalizedAnswer = normalizeText(cleanedAnswer);
  const letterMatch = normalizedAnswer.match(/^[a-d]$/i);
  if (letterMatch) {
    const index = letterMatch[0].toLowerCase().charCodeAt(0) - 97;
    return candidateOptions[index] || "";
  }

  const directMatch = candidateOptions.find((option) => textMatches(cleanedAnswer, option));
  if (directMatch) {
    return directMatch;
  }

  const answerTokens = normalizedAnswer.split(" ").filter(Boolean);
  let bestMatch = "";
  let bestScore = 0;

  for (const option of candidateOptions) {
    const normalizedOption = normalizeText(option);
    const score = answerTokens.filter((token) => normalizedOption.includes(token)).length;
    if (score > bestScore) {
      bestMatch = option;
      bestScore = score;
    }
  }

  return bestScore > 0 ? bestMatch : "";
}

function hasEditorSurface(browserContext) {
  const interactiveElements = Array.isArray(browserContext?.interactiveElements)
    ? browserContext.interactiveElements
    : [];

  return interactiveElements.some((element) => {
    const role = normalizeText(element?.role);
    const type = normalizeText(element?.type);
    const label = normalizeText(`${element?.label || ""} ${element?.nameAttribute || ""}`);
    const looksEditable = role === "textbox" ||
      type === "textarea" ||
      (type === "input" && (
        label.includes("message") ||
        label.includes("reply") ||
        label.includes("compose") ||
        label.includes("chat") ||
        label.includes("comment") ||
        label.includes("document")
      ));

    if (!looksEditable) {
      return false;
    }

    return !/search|filter|find in page|look up/.test(label);
  });
}

function hasInteractiveLabel(browserContext, ...phrases) {
  const interactiveElements = Array.isArray(browserContext?.interactiveElements)
    ? browserContext.interactiveElements
    : [];

  return interactiveElements.some((element) => {
    const label = normalizeText(`${element?.label || ""} ${element?.nameAttribute || ""}`);
    return phrases.some((phrase) => textMatches(label, phrase));
  });
}

function inferAnswersFromSelection({ originalText = "", resultText = "" }) {
  const parsedAnswers = parseAnswerKey(resultText);
  const selectionQuestionBlocks = parseSelectionQuestionBlocks(originalText);
  const candidateOptions = extractOptionTexts(originalText);
  const defaultQuestion = extractQuestionLabel(originalText);

  if (parsedAnswers.length > 0) {
    return parsedAnswers
      .map((answer) => {
        const matchedQuestionBlock = resolveSelectionQuestionBlock(answer, selectionQuestionBlocks);
        const optionPool = matchedQuestionBlock?.optionTexts?.length
          ? matchedQuestionBlock.optionTexts
          : candidateOptions;

        return {
          question: matchedQuestionBlock?.questionText || answer.question || defaultQuestion,
          option: shouldResolveAgainstCandidateOptions(answer.option, answer.choiceIndex)
            ? (pickBestMatchingOption(answer.option, optionPool) || answer.option)
            : answer.option,
          questionIndex: Number.isInteger(answer.questionIndex) && answer.questionIndex > 0
            ? answer.questionIndex
            : matchedQuestionBlock?.questionIndex,
          choiceIndex: answer.choiceIndex
        };
      })
      .filter((answer) => sanitizeAnswerOption(answer.option))
      .slice(0, MAX_ANSWER_KEY_ENTRIES);
  }

  if (candidateOptions.length > 0) {
    const matchedOption = pickBestMatchingOption(resultText, candidateOptions);
    if (matchedOption) {
      return [
        {
          question: defaultQuestion,
          option: matchedOption
        }
      ];
    }
  }

  return [];
}

function shouldResolveAgainstCandidateOptions(option = "", choiceIndex = undefined) {
  if (choiceIndex !== undefined) {
    return false;
  }

  const cleaned = sanitizeAnswerOption(option);
  if (!cleaned) {
    return false;
  }

  const words = cleaned.split(/\s+/).filter(Boolean);
  return cleaned.length <= 80 &&
    words.length <= 8 &&
    !/[.!?]/.test(cleaned);
}

function shouldAdvanceQuiz({ voiceCommand = "", browserContext }) {
  const browserText = `${browserContext?.title || ""} ${browserContext?.visibleText || ""}`;
  if (containsAny(voiceCommand, ["next", "continue", "attempt all", "all questions", "autofill all", "entire quiz"])) {
    return true;
  }

  return /\b\d+\s*\/\s*\d+\b/.test(browserText);
}

function buildNextQuizSteps({ browserContext }) {
  const browserText = `${browserContext?.title || ""} ${browserContext?.visibleText || ""}`;
  if (!containsAny(browserText, ["next", "continue"]) && !/\b\d+\s*\/\s*\d+\b/.test(browserText)) {
    return [];
  }

  return [
    {
      tool: "click_role",
      role: "button",
      name: "Next"
    },
    {
      tool: "wait_ms",
      waitMs: 900
    }
  ];
}

function buildExpectedQuizOptions({ originalText = "", resultText = "" }) {
  return inferAnswersFromSelection({ originalText, resultText })
    .map((answer) => normalizeText(answer.option))
    .filter(Boolean);
}

function isQuizPlanPlausible({ plan, originalText, resultText }) {
  const expectedOptions = buildExpectedQuizOptions({ originalText, resultText });
  if (expectedOptions.length === 0) {
    return plan.steps.length > 0;
  }

  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key" && Array.isArray(step.answers));
  if (answerKeyStep) {
    const options = answerKeyStep.answers
      .map((answer) => normalizeText(answer.option))
      .filter(Boolean);

    if (options.length === 0) {
      return false;
    }

    return options.every((candidate) => expectedOptions.some((expected) => textMatches(expected, candidate)));
  }

  const choiceSteps = plan.steps.filter((step) => ["check_radio", "check_checkbox", "select_option", "click_text"].includes(step.tool));
  if (choiceSteps.length === 0) {
    return false;
  }

  return choiceSteps.every((step) => {
    const candidate = normalizeText(step.option || step.text || step.name || "");
    return candidate && expectedOptions.some((expected) => textMatches(expected, candidate));
  });
}

function buildMailFallbackPlan({ browserContext, originalText, resultText, voiceCommand }) {
  const recipient = extractEmailAddress(voiceCommand, originalText, resultText);
  const subject = extractSubject(originalText, resultText);
  const body = stripEmailBody(resultText);
  const browserText = `${browserContext?.url} ${browserContext?.title} ${browserContext?.visibleText}`;
  const onMailPage = looksLikeMailPage(browserContext);
  const shouldReply = containsAny(voiceCommand, ["reply", "respond"]) || containsAny(`${originalText} ${resultText}`, ["re:", "reply"]);
  const replyVisible = containsAny(browserText, ["reply", "reply all", "send"]);
  const editorVisible = hasEditorSurface(browserContext);
  const toFieldVisible = hasInteractiveLabel(browserContext, "to", "to recipients", "recipients");
  const subjectFieldVisible = hasInteractiveLabel(browserContext, "subject", "add a subject");

  const steps = [];
  if (shouldReply && onMailPage && replyVisible && !editorVisible) {
    steps.push({
      tool: "click_role",
      role: "button",
      name: "Reply"
    });
    steps.push({
      tool: "wait_ms",
      waitMs: 1200
    });
  } else if (!onMailPage) {
    steps.push({
      tool: "open_new_tab",
      url: "https://mail.google.com/mail/u/0/#inbox?compose=new"
    });
    steps.push({
      tool: "wait_ms",
      waitMs: 1800
    });
  } else if (!editorVisible && containsAny(browserContext?.visibleText, ["compose", "new message"]) && !containsAny(browserContext?.visibleText, ["message body", "subject", "write a reply"])) {
    steps.push({
      tool: "click_role",
      role: "button",
      name: "Compose"
    });
    steps.push({
      tool: "wait_ms",
      waitMs: 1200
    });
  }

  if (recipient && !shouldReply && (toFieldVisible || !onMailPage)) {
    steps.push({
      tool: "fill_label",
      label: "To recipients",
      text: recipient
    });
  }

  if (subject && !shouldReply && (subjectFieldVisible || !onMailPage)) {
    steps.push({
      tool: "fill_label",
      label: "Subject",
      text: subject
    });
  }

  if (body) {
    steps.push(
      {
        tool: "fill_editor",
        label: shouldReply ? "Write a reply" : "Message Body",
        text: body
      }
    );
  }

  if (containsAny(voiceCommand, ["schedule"])) {
    steps.push({
      tool: "click_role",
      role: "button",
      name: "More send options"
    });
  } else if (containsAny(voiceCommand, ["send"])) {
    steps.push({
      tool: "click_role",
      role: "button",
      name: "Send"
    });
  }

  return {
    goal: shouldReply ? "reply_to_email" : "apply_email_result",
    summary: recipient
      ? `Open compose and draft the email for ${recipient}.`
      : shouldReply
        ? "Open the reply composer and insert the generated response."
        : "Open compose and draft the generated email body.",
    requiresConfirmation: isRiskyBrowserAction(voiceCommand),
    steps: steps.slice(0, 16)
  };
}

function buildFormsFallbackPlan({ originalText, resultText, voiceCommand, browserContext }) {
  const answers = inferAnswersFromSelection({ originalText, resultText });
  if (answers.length === 0) {
    return null;
  }

  const advancePages = shouldAdvanceQuiz({ voiceCommand, browserContext });

  return {
    goal: "fill_form_answers",
    summary: advancePages
      ? "Apply the answer key across the visible quiz and continue page-by-page when needed."
      : "Apply the answer key to the visible form questions.",
    requiresConfirmation: false,
    steps: [
      {
        tool: "apply_answer_key",
        answers: answers.slice(0, MAX_ANSWER_KEY_ENTRIES),
        advancePages
      }
    ]
  };
}

function buildDiscordFallbackPlan({ resultText, voiceCommand, browserContext }) {
  const body = String(resultText || "").trim();
  if (!body) {
    return null;
  }

  const onDiscordPage = containsAny(`${browserContext?.url} ${browserContext?.title}`, ["discord.com", "discord"]);
  return {
    goal: "draft_or_send_discord_message",
    summary: containsAny(voiceCommand, ["send"])
      ? "Fill the Discord composer with the generated message and send it after confirmation."
      : "Fill the Discord composer with the generated message.",
    requiresConfirmation: isRiskyBrowserAction(voiceCommand),
    steps: [
      !onDiscordPage
        ? {
            tool: "open_new_tab",
            url: "https://discord.com/channels/@me"
          }
        : null,
      !onDiscordPage
        ? {
            tool: "wait_ms",
            waitMs: 1800
          }
        : null,
      {
        tool: "fill_editor",
        label: "Type a message",
        text: body
      },
      containsAny(voiceCommand, ["send"])
        ? {
            tool: "press_key",
            key: "Enter"
          }
        : null
    ].filter(Boolean)
  };
}

function buildEditorFallbackPlan({ resultText, voiceCommand, browserContext, taskPack }) {
  const body = String(resultText || "").trim();
  if (!body || !hasEditorSurface(browserContext)) {
    return null;
  }

  const surfaceLabel = (() => {
    switch (taskPack?.id) {
      case "google_docs":
        return "Document";
      case "notion":
        return "Page";
      case "mail_compose":
        return "Message Body";
      case "discord":
        return "Message";
      default:
        return "Message";
    }
  })();

  const shouldSend = containsAny(voiceCommand, ["send", "post"]) &&
    taskPack?.id !== "google_docs" &&
    taskPack?.id !== "notion";

  return {
    goal: "insert_generated_result",
    summary: shouldSend
      ? "Insert the generated result into the active editor and send it after confirmation."
      : "Insert the generated result into the current active editor.",
    requiresConfirmation: isRiskyBrowserAction(voiceCommand),
    steps: [
      {
        tool: "fill_editor",
        label: surfaceLabel,
        text: body
      },
      shouldSend
        ? {
            tool: "press_key",
            key: "Enter"
          }
        : null
    ].filter(Boolean)
  };
}

function buildShoppingFallbackPlan({ originalText, resultText, voiceCommand, executionInstruction = "" }) {
  const planningInstruction = combinePlannerInstruction(voiceCommand, executionInstruction);
  const query = extractSearchQuery(executionInstruction, voiceCommand, originalText, resultText);
  if (!query) {
    return null;
  }

  const shouldAddToCart = containsAny(planningInstruction, ["add to cart", "cart"]);
  const shoppingPreference = parseShoppingPreference(planningInstruction);
  const shouldOpenAmazon = shoppingPreference.amazonOnly ||
    (!shoppingPreference.flipkartOnly && !shoppingPreference.excludesAmazon && (shoppingPreference.wantsAmazon || shouldAddToCart));
  const shouldOpenFlipkart = shoppingPreference.flipkartOnly ||
    (!shoppingPreference.amazonOnly && !shoppingPreference.excludesFlipkart && shoppingPreference.wantsFlipkart);
  const amazonUrl = `https://www.amazon.in/s?k=${encodeURIComponent(query)}`;
  const flipkartUrl = `https://www.flipkart.com/search?q=${encodeURIComponent(query)}`;
  const searchUrl = shouldOpenAmazon ? amazonUrl : flipkartUrl;

  if (!shouldAddToCart) {
    const openBothSites =
      !shoppingPreference.amazonOnly &&
      !shoppingPreference.flipkartOnly &&
      !shoppingPreference.excludesAmazon &&
      !shoppingPreference.excludesFlipkart &&
      (!shoppingPreference.wantsAmazon && !shoppingPreference.wantsFlipkart || shoppingPreference.compareRequested);

    if (openBothSites) {
      return {
        goal: "compare_product_prices",
        summary: `Open Amazon and Flipkart search tabs to compare prices for ${query}.`,
        requiresConfirmation: false,
        steps: [
          {
            tool: "open_new_tab",
            url: amazonUrl
          },
          {
            tool: "wait_ms",
            waitMs: 900
          },
          {
            tool: "open_new_tab",
            url: flipkartUrl
          }
        ]
      };
    }

    return {
      goal: "search_product",
      summary: `Open ${shouldOpenAmazon ? "Amazon" : "Flipkart"} and search for ${query}.`,
      requiresConfirmation: false,
      steps: [
        {
          tool: "open_new_tab",
          url: searchUrl
        }
      ]
    };
  }

  return {
    goal: "search_product_and_add_to_cart",
    summary: `Open ${shouldOpenAmazon ? "Amazon" : "Flipkart"}, search for ${query}, and continue toward add-to-cart flow with confirmation.`,
    requiresConfirmation: shouldAddToCart,
    steps: [
      {
        tool: "open_new_tab",
        url: searchUrl
      },
      {
        tool: "wait_ms",
        waitMs: 1800
      },
      shouldAddToCart
        ? {
            tool: "click_role",
            role: "button",
            name: "Add to Cart"
          }
        : null
    ].filter(Boolean)
  };
}

function buildFallbackPlan({ taskPack, originalText, resultText, action, voiceCommand, contentType, browserContext }) {
  switch (taskPack?.id) {
    case "google_forms":
    case "qa_form":
      return buildFormsFallbackPlan({ originalText, resultText, voiceCommand, browserContext });
    case "mail_compose":
      return buildMailFallbackPlan({ browserContext, originalText, resultText, voiceCommand });
    case "discord":
      return buildDiscordFallbackPlan({ resultText, voiceCommand, browserContext });
    case "shopping":
      return buildShoppingFallbackPlan({ originalText, resultText, voiceCommand });
    default:
      break;
  }

  if (isFormsLike({ taskPack, contentType, voiceCommand })) {
    return buildFormsFallbackPlan({ originalText, resultText, voiceCommand, browserContext });
  }

  if (isMailLike({ taskPack, contentType, action, voiceCommand })) {
    return buildMailFallbackPlan({ browserContext, originalText, resultText, voiceCommand });
  }

  if (isDiscordLike({ taskPack, voiceCommand, browserContext })) {
    return buildDiscordFallbackPlan({ resultText, voiceCommand, browserContext });
  }

  if (isShoppingLike({ taskPack, contentType, action, voiceCommand, browserContext, originalText, resultText })) {
    return buildShoppingFallbackPlan({ originalText, resultText, voiceCommand });
  }

  const editorPlan = buildEditorFallbackPlan({ resultText, voiceCommand, browserContext, taskPack });
  if (editorPlan) {
    return editorPlan;
  }

  return null;
}

function buildSafeNoopPlan(taskPack) {
  const taskPackId = taskPack?.id;
  switch (taskPackId) {
    case "google_forms":
    case "qa_form":
      return {
        goal: "fill_form_answers",
        summary: "The current page looks like a form, but Cursivis could not derive a safe answer-key mapping from the current result. Re-run the result on the latest selection, then try Take Action again.",
        requiresConfirmation: false,
        steps: []
      };
    case "mail_compose":
      return {
        goal: "apply_email_result",
        summary: "The current page looks like a mail workflow, but Cursivis could not derive a safe compose or reply action from the current result.",
        requiresConfirmation: false,
        steps: []
      };
    case "discord":
      return {
        goal: "draft_or_send_discord_message",
        summary: "The current page looks like a message composer, but Cursivis could not derive a safe message insertion plan from the current result.",
        requiresConfirmation: false,
        steps: []
      };
    default:
      return {
        goal: "browser_action",
        summary: "Cursivis could not build a safe current-tab action plan from the current result.",
        requiresConfirmation: false,
        steps: []
      };
  }
}

function buildBrowserActionPrompt({
  originalText,
  resultText,
  action,
  voiceCommand,
  executionInstruction,
  contentType,
  browserContext,
  taskPack
}) {
  return [
    "You are the Cursivis browser action planner.",
    "Convert the user's intent plus the current browser page context into a safe executable action plan.",
    "Return strict JSON only. No markdown.",
    JSON.stringify(
      {
        goal: "short_snake_case_goal",
        summary: "one concise sentence describing what will happen",
        requiresConfirmation: true,
        steps: [
          {
            tool:
              "navigate|open_new_tab|switch_tab|click_role|click_text|fill_label|fill_name|fill_placeholder|fill_editor|type_active|select_option|check_radio|check_checkbox|press_key|scroll|extract_dom|wait_for_text|wait_ms|apply_answer_key",
            role: "optional aria role like button/link/textbox",
            name: "optional accessible name",
            label: "optional field label",
            text: "optional text to type or click",
            question: "optional question/group name for radio buttons",
            option: "optional option text",
            answers: [{ question: "optional visible question", option: "required visible answer label" }],
            advancePages: true,
            url: "optional url",
            key: "optional keyboard key",
            waitMs: 250
          }
        ]
      },
      null,
      2
    ),
    "Rules:",
    "- Use only the listed tools.",
    "- If executionInstruction is present, treat it as the highest-priority instruction for what to do now.",
    "- If executionInstruction conflicts with an inferred comparison or shopping flow, follow executionInstruction.",
    "- If executionInstruction says to use only one site, do not open comparison tabs for other sites.",
    "- Prefer fill_label, fill_editor, click_role, select_option, and check_radio over brittle generic clicks.",
    "- Use open_new_tab only when the command explicitly implies a new tab or when the destination is clearly different from the current page.",
    "- Use switch_tab when the instruction refers to a tab by site, title, or purpose.",
    "- Use scroll when content must be brought into view before acting.",
    "- Use extract_dom when you need one explicit re-check of the current page before deciding later steps.",
    "- Use the browser page context exactly as provided. Do not invent elements that are not present.",
    "- For MCQ or form filling, map answer choices to visible radio/select controls.",
    "- For MCQs, treat the original selected text plus generated result as the answer source, then match those answers to visible question groups and option labels on the page.",
    "- For MCQs, never treat quiz counters or page numbers like '2/11' or '12' as answer options.",
    "- For MCQs, prefer the exact visible answer label text such as 'February 29' instead of a numeric index.",
    "- Use fill_editor for rich text, contenteditable, or browser-based message composers when a standard text input is unlikely to be reliable.",
    "- For email workflows, fill recipient, subject, and body fields only when those fields are visible or clearly labeled in the page context.",
    "- For email workflows, use the generated result as the body content when appropriate.",
    "- If navigation is needed, only navigate when the destination is explicit from the command or current page flow.",
    "- If the request is risky (send, submit, schedule, delete, purchase), set requiresConfirmation to true.",
    "- For risky flows, do not skip the final button press, but make sure confirmation is required first.",
    "- Prefer deterministic current-tab editor insertion or answer-key application when the page clearly exposes an editor or question controls.",
    "- If there is not enough page context to act safely, return an empty steps array and explain why in summary.",
    "- Keep plans short and practical.",
    taskPack ? `Detected task pack: ${taskPack.label}. ${taskPack.guidance}` : "Detected task pack: none",
    `Executed content action: ${action || "unknown"}`,
    `Selection content type: ${contentType || "general_text"}`,
    `Voice command: ${voiceCommand || "none"}`,
    `Execution instruction: ${executionInstruction || "none"}`,
    "Original selected text:",
    originalText || "(none)",
    "Generated result to apply:",
    resultText || "(none)",
    "Browser page context:",
    JSON.stringify(browserContext, null, 2)
  ].join("\n\n");
}

function buildRefinedBrowserActionPrompt({
  originalText,
  resultText,
  action,
  voiceCommand,
  executionInstruction,
  contentType,
  browserContext,
  taskPack,
  currentPlan
}) {
  return [
    "You are the Cursivis browser action plan refiner.",
    "Revise the existing browser action plan so it follows the execution instruction exactly while staying safe and executable.",
    "Return strict JSON only. No markdown.",
    JSON.stringify(
      {
        goal: "short_snake_case_goal",
        summary: "one concise sentence describing what will happen",
        requiresConfirmation: true,
        steps: [
          {
            tool:
              "navigate|open_new_tab|switch_tab|click_role|click_text|fill_label|fill_name|fill_placeholder|fill_editor|type_active|select_option|check_radio|check_checkbox|press_key|scroll|extract_dom|wait_for_text|wait_ms|apply_answer_key",
            role: "optional aria role like button/link/textbox",
            name: "optional accessible name",
            label: "optional field label",
            text: "optional text to type or click",
            question: "optional question/group name for radio buttons",
            option: "optional option text",
            answers: [{ question: "optional visible question", option: "required visible answer label" }],
            advancePages: true,
            url: "optional url",
            key: "optional keyboard key",
            waitMs: 250
          }
        ]
      },
      null,
      2
    ),
    "Rules:",
    "- executionInstruction is the highest-priority instruction.",
    "- Start from the current plan, but change any step that conflicts with executionInstruction.",
    "- Keep useful existing steps when they still help satisfy executionInstruction.",
    "- If the user adds another destination or website, include it explicitly in the revised steps.",
    "- If the user says only one site should be used, remove other site openings from the current plan.",
    "- If the instruction asks for an official website, prefer the brand's official domain when you know it confidently; otherwise open the official homepage or a safe search step that clearly targets the official site.",
    "- Use only the listed tools.",
    "- Use the browser page context exactly as provided. Do not invent elements that are not present.",
    "- Keep plans short, practical, and directly executable.",
    "- If there is not enough context to safely improve the current plan, return the current plan with an updated summary instead of inventing risky steps.",
    taskPack ? `Detected task pack: ${taskPack.label}. ${taskPack.guidance}` : "Detected task pack: none",
    `Executed content action: ${action || "unknown"}`,
    `Selection content type: ${contentType || "general_text"}`,
    `Voice command: ${voiceCommand || "none"}`,
    `Execution instruction: ${executionInstruction || "none"}`,
    "Current plan to refine:",
    JSON.stringify(currentPlan, null, 2),
    "Original selected text:",
    originalText || "(none)",
    "Generated result to apply:",
    resultText || "(none)",
    "Browser page context:",
    JSON.stringify(browserContext, null, 2)
  ].join("\n\n");
}

export function createBrowserActionPlanner({ generateText }) {
  return async ({
    originalText,
    resultText,
    action,
    voiceCommand,
    executionInstruction = "",
    contentType,
    browserContext
  }) => {
    const planningInstruction = combinePlannerInstruction(voiceCommand, executionInstruction);
    const taskPack = detectBrowserTaskPack({
      browserContext,
      contentType,
      action,
      voiceCommand: planningInstruction
    });

    const directFormsPlan = isFormsLike({ taskPack, contentType, voiceCommand: planningInstruction })
      ? buildFormsFallbackPlan({ originalText, resultText, voiceCommand: planningInstruction, browserContext })
      : null;
    if (directFormsPlan) {
      return directFormsPlan;
    }

    const directShoppingPlan = isShoppingLike({
      taskPack,
      contentType,
      action,
      voiceCommand: planningInstruction,
      browserContext,
      originalText,
      resultText
    })
      ? buildShoppingFallbackPlan({ originalText, resultText, voiceCommand, executionInstruction })
      : null;
    if (directShoppingPlan) {
      return directShoppingPlan;
    }

    const directTaskPackPlan =
      (taskPack?.id === "mail_compose"
        ? buildMailFallbackPlan({ browserContext, originalText, resultText, voiceCommand: planningInstruction })
        : null) ||
      (taskPack?.id === "discord"
        ? buildDiscordFallbackPlan({ resultText, voiceCommand: planningInstruction, browserContext })
        : null) ||
      (taskPack?.id === "shopping"
        ? buildShoppingFallbackPlan({ originalText, resultText, voiceCommand, executionInstruction })
        : null) ||
      ((taskPack?.id === "google_docs" || taskPack?.id === "notion")
        ? buildEditorFallbackPlan({ resultText, voiceCommand: planningInstruction, browserContext, taskPack })
        : null);
    if (directTaskPackPlan) {
      return directTaskPackPlan;
    }

    const prompt = buildBrowserActionPrompt({
      originalText,
      resultText,
      action,
      voiceCommand: planningInstruction,
      executionInstruction,
      contentType,
      browserContext,
      taskPack
    });

    const generated = await generateText({
      prompt,
      config: {
        responseMimeType: "application/json",
        temperature: 0.15
      }
    });

    const parsed = parseJsonObject(generated.text);
    const sanitized = sanitizePlan(parsed);
    if (
      sanitized.steps.length > 0 &&
      isTaskPackCompatiblePlan({
        plan: sanitized,
        taskPack,
        originalText,
        resultText
      })
    ) {
      if (isFormsLike({ taskPack, contentType, voiceCommand }) && shouldAdvanceQuiz({ voiceCommand, browserContext })) {
        const hasNextStep = sanitized.steps.some((step) => step.tool === "click_role" && normalizeText(step.name) === "next");
        if (!hasNextStep) {
          sanitized.steps.push(...buildNextQuizSteps({ browserContext }));
          sanitized.steps = sanitized.steps.slice(0, 16);
        }
      }

      return sanitized;
    }

    return buildFallbackPlan({
      taskPack,
      originalText,
      resultText,
      action,
      voiceCommand: planningInstruction,
      contentType,
      browserContext
    }) ?? (taskPack ? buildSafeNoopPlan(taskPack) : sanitized);
  };
}

export function createBrowserActionPlanRefiner({ generateText, planBrowserAction }) {
  return async ({
    originalText,
    resultText,
    action,
    voiceCommand,
    executionInstruction = "",
    contentType,
    browserContext,
    currentPlan
  }) => {
    const normalizedInstruction = String(executionInstruction || "").trim();
    const sanitizedCurrentPlan = sanitizePlan(currentPlan);
    if (!normalizedInstruction) {
      return sanitizedCurrentPlan;
    }

    const planningInstruction = combinePlannerInstruction(voiceCommand, normalizedInstruction);
    const taskPack = detectBrowserTaskPack({
      browserContext,
      contentType,
      action,
      voiceCommand: planningInstruction
    });

    const prompt = buildRefinedBrowserActionPrompt({
      originalText,
      resultText,
      action,
      voiceCommand: planningInstruction,
      executionInstruction: normalizedInstruction,
      contentType,
      browserContext,
      taskPack,
      currentPlan: sanitizedCurrentPlan
    });

    const generated = await generateText({
      prompt,
      config: {
        responseMimeType: "application/json",
        temperature: 0.1
      }
    });

    const parsed = parseJsonObject(generated.text);
    const sanitized = sanitizePlan(parsed);
    if (
      sanitized.steps.length > 0 &&
      isTaskPackCompatiblePlan({
        plan: sanitized,
        taskPack,
        originalText,
        resultText
      })
    ) {
      if (isFormsLike({ taskPack, contentType, voiceCommand: planningInstruction }) && shouldAdvanceQuiz({ voiceCommand: planningInstruction, browserContext })) {
        const hasNextStep = sanitized.steps.some((step) => step.tool === "click_role" && normalizeText(step.name) === "next");
        if (!hasNextStep) {
          sanitized.steps.push(...buildNextQuizSteps({ browserContext }));
          sanitized.steps = sanitized.steps.slice(0, 16);
        }
      }

      return sanitized;
    }

    if (sanitizedCurrentPlan.steps.length > 0) {
      return {
        ...sanitizedCurrentPlan,
        summary: sanitizedCurrentPlan.summary || "Cursivis kept the current Take Action plan because the refinement could not be made safely."
      };
    }

    return await planBrowserAction({
      originalText,
      resultText,
      action,
      voiceCommand,
      executionInstruction: normalizedInstruction,
      contentType,
      browserContext
    });
  };
}
