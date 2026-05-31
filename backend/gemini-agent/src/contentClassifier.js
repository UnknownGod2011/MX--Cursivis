const CODE_KEYWORD_PATTERN =
  /\b(function|class|const|let|var|public|private|protected|import|export|return|if\s*\(|for\s*\(|while\s*\(|try|catch|throw|await|async|def|interface|enum|namespace|using|console\.log|print\s*\(|SELECT\s+.+\s+FROM|INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM)\b/i;
const CODE_INLINE_FEATURE_PATTERN =
  /(=>|==={0,1}|!==|::|<\/?[a-z][^>]*>|#include\b|using\s+[A-Z][A-Za-z0-9_.]+;)/i;
const CODE_PUNCTUATION_FEATURE_PATTERN =
  /[{};]/g;
const CODE_SHAPED_LINE_PATTERN =
  /(^|\n)\s*(function|class|const|let|var|public|private|protected|import|export|return|if\s*\(|for\s*\(|while\s*\(|try\b|catch\b|throw\b|await\b|async\b|def\b|interface\b|enum\b|namespace\b|using\s+[A-Z][A-Za-z0-9_.]+;|console\.log|print\s*\(|SELECT\s+.+\s+FROM|INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM)\b/i;
const CODE_FENCE_PATTERN =
  /```|~~~|^\s*(\/\/|\/\*|\*)/m;
const CODE_ERROR_PATTERN =
  /\b(syntaxerror|typeerror|referenceerror|exception|stack trace|nullreferenceexception|null pointer|undefined(?:\s+is|\s+variable)?|unexpected token|unexpected end|unexpected identifier|unterminated|compilation failed|runtime error|traceback|missing\s+[)\]};]|missing\b)\b/i;
const INCOMPLETE_CODE_LINE_PATTERN =
  /(^|\n)\s*(if|else if|for|while|switch|try|catch|finally|function|class)\b[^\n{};]*$|(^|\n)\s*(return|throw|await)\s*$|[=+\-*/%&|?:.,]\s*$/i;
const QUESTION_SET_PATTERN =
  /(?:^|\n)\s*(?:question\s*\d+|\d+[\).:-])\s+[^\n]+(?:\?|$)/i;
const QUESTION_SET_LINE_PATTERN =
  /^(?:question\s*\d+|\d+[\).:-])\s+/i;

const QUESTION_PREFIX_PATTERN =
  /^(who|what|when|where|why|how|which|whom|whose|is|are|can|could|should|would|will|do|does|did|name|define|explain|tell me|give me|find|list)\b/i;

const PHRASE_QUERY_PATTERN =
  /\b(richest|poorest|largest|smallest|highest|lowest|best|top|cheapest|costliest|fastest|latest|current|newest|capital|population|price|weather|time|meaning|definition|difference|vs|versus|smartest|oldest|youngest|strongest|biggest|tallest|longest|deepest|closest|farthest|hottest|coldest)\b/i;

const FACT_QUERY_TOPIC_PATTERN =
  /\b(person|people|country|city|place|animal|company|ceo|founder|president|prime minister|county|state|mountain|ocean|river|planet|movie|book|player|team|brand|phone|laptop|currency|religion|language|meaning|definition|population|price|time|weather|capital|net worth|iq)\b/i;

const REGION_OR_SCOPE_PATTERN =
  /\b(in the world|of the world|on earth|right now|today|currently|as of|in india|in usa|in europe|near me|of all time)\b/i;

const TIME_SENSITIVE_PATTERN =
  /\b(current|currently|latest|today|right now|as of|this year|richest|most valuable|president|prime minister|ceo|market cap|stock price|exchange rate|winner|champion|rankings?)\b/i;

const PRODUCT_PATTERN =
  /\b(price|buy|discount|deal|review|specs?|model|amazon|flipkart|walmart|compare|msrp|shipping|warranty)\b|\$\d+|\u20B9\d+/i;

const EMAIL_PATTERN = /\b(subject:|dear\s+\w+|hi\s+\w+|thanks[,!]|\bregards[,!]|\bbest regards\b|sincerely[,!])\b/i;
const EMAIL_HEADER_PATTERN = /(^|\n)\s*(from|to|cc|bcc|sent|date|subject):/i;
const EMAIL_ADDRESS_PATTERN = /\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/i;
const EMAIL_THREAD_PATTERN = /\b(on .+ wrote:|forwarded message|original message|re:|fwd:)\b/i;
const EMAIL_CLOSING_PATTERN = /\b(best regards|regards|sincerely|thanks|thank you|warm regards|kind regards)\b/i;
const EMAIL_GREETING_PATTERN = /\b(dear|hi|hello|hey)\b/i;
const EMAIL_INLINE_ADDRESS_PATTERN = /<[^>\r\n]*@[^\r\n>]+>/i;

const CAPTION_PATTERN = /\b(caption|hashtags?|yt|youtube|thumbnail|hook|title ideas?)\b/i;
const MCQ_PATTERN =
  /\b(mcq|multiple choice|choose (the )?correct|select (the )?correct|tick the correct|mark the correct)\b|(?:^|\n)\s*(?:\d+[\).:-]\s*)?.{0,140}\n?(?:\s*[A-Da-d][\).:-]\s+.+){2,}/i;
const MCQ_OPTION_PATTERN = /\b(a\)|b\)|c\)|d\)|option\s+[abcd]\b)/i;

const ACTION_MAP = new Map([
  ["summarize", "summarize"],
  ["summarise", "summarize"],
  ["expand", "expand_text"],
  ["expand text", "expand_text"],
  ["elaborate", "expand_text"],
  ["rewrite", "rewrite"],
  ["rewrite_structured", "rewrite_structured"],
  ["rewrite structured", "rewrite_structured"],
  ["structure rewrite", "rewrite_structured"],
  ["optimize_prompt", "optimize_prompt"],
  ["optimize prompt", "optimize_prompt"],
  ["prompt_optimizer", "optimize_prompt"],
  ["prompt optimizer", "optimize_prompt"],
  ["prompt optimization", "optimize_prompt"],
  ["prompt optimisation", "optimize_prompt"],
  ["improve prompt", "optimize_prompt"],
  ["structure prompt", "optimize_prompt"],
  ["extract_insights", "extract_insights"],
  ["extract insights", "extract_insights"],
  ["key insights", "extract_insights"],
  ["extract key insights", "extract_insights"],
  ["translate", "translate"],
  ["explain", "explain"],
  ["answer_question", "answer_question"],
  ["answer question", "answer_question"],
  ["answer", "answer_question"],
  ["bullet_points", "bullet_points"],
  ["bullet points", "bullet_points"],
  ["extract bullet points", "bullet_points"],
  ["convert to bullet points", "bullet_points"],
  ["polish_email", "polish_email"],
  ["polish email", "polish_email"],
  ["fix punctuation", "polish_email"],
  ["draft_reply", "draft_reply"],
  ["draft reply", "draft_reply"],
  ["reply_email", "draft_reply"],
  ["reply email", "draft_reply"],
  ["reply to email", "draft_reply"],
  ["improve_code", "improve_code"],
  ["improve code", "improve_code"],
  ["enhance code", "improve_code"],
  ["explain_code", "explain_code"],
  ["explain code", "explain_code"],
  ["debug_code", "debug_code"],
  ["debug code", "debug_code"],
  ["optimize_code", "optimize_code"],
  ["optimize code", "optimize_code"],
  ["extract_product_info", "extract_product_info"],
  ["extract product info", "extract_product_info"],
  ["product details", "extract_product_info"],
  ["compare_prices", "compare_prices"],
  ["compare prices", "compare_prices"],
  ["show_product_details", "show_product_details"],
  ["show product details", "show_product_details"],
  ["find_reviews", "find_reviews"],
  ["find reviews", "find_reviews"],
  ["suggest_captions", "suggest_captions"],
  ["suggest captions", "suggest_captions"],
  ["generate_captions", "generate_captions"],
  ["generate captions", "generate_captions"],
  ["extract_dominant_colors", "extract_dominant_colors"],
  ["extract dominant colors", "extract_dominant_colors"],
  ["identify_image_colors", "extract_dominant_colors"],
  ["identify image colors", "extract_dominant_colors"],
  ["dominant colors", "extract_dominant_colors"],
  ["image colors", "extract_dominant_colors"],
  ["color palette", "extract_dominant_colors"],
  ["ocr_extract_text", "ocr_extract_text"],
  ["ocr extract text", "ocr_extract_text"],
  ["extract text", "ocr_extract_text"],
  ["extract_table_data", "extract_table_data"],
  ["extract table data", "extract_table_data"],
  ["describe_image", "describe_image"],
  ["describe image", "describe_image"],
  ["extract_key_details", "extract_key_details"],
  ["extract key details", "extract_key_details"],
  ["identify_objects", "identify_objects"],
  ["identify objects", "identify_objects"]
]);

const KNOWN_ACTIONS = new Set([...ACTION_MAP.values()]);

const KNOWN_CONTENT_TYPES = new Set([
  "question",
  "mcq",
  "code",
  "email",
  "report",
  "product",
  "social_caption",
  "prompt",
  "general_text",
  "image"
]);

const ALTERNATIVES_BY_TYPE = {
  question: ["answer_question", "explain", "extract_insights", "rewrite"],
  mcq: ["answer_question", "explain", "bullet_points", "extract_insights"],
  code: ["explain_code", "debug_code", "improve_code", "optimize_code"],
  email: ["polish_email", "draft_reply", "rewrite", "bullet_points"],
  report: ["extract_insights", "bullet_points", "summarize", "rewrite_structured"],
  product: ["extract_product_info", "compare_prices", "show_product_details", "find_reviews", "extract_insights"],
  social_caption: ["suggest_captions", "rewrite", "bullet_points"],
  prompt: ["optimize_prompt", "rewrite_structured", "rewrite", "bullet_points"],
  general_text: ["extract_insights", "rewrite_structured", "summarize", "bullet_points", "rewrite", "translate"],
  image: ["describe_image", "extract_key_details", "identify_objects", "extract_dominant_colors", "generate_captions"]
};

const EXTENDED_ALTERNATIVES_BY_TYPE = {
  question: ["fact_check", "compare_answers", "turn_into_flashcards", "bullet_points", "translate", "extract_insights"],
  mcq: ["answer_question", "explain", "eliminate_wrong_options", "create_answer_key", "bullet_points", "extract_insights"],
  code: ["explain_code", "debug_code", "improve_code", "optimize_code", "write_tests", "refactor_code"],
  email: ["polish_email", "draft_reply", "rewrite", "change_tone", "shorten_email", "expand_text", "translate", "extract_action_items"],
  report: ["extract_insights", "bullet_points", "summarize", "rewrite_structured", "executive_summary", "extract_action_items", "extract_metrics"],
  product: ["extract_product_info", "compare_prices", "show_product_details", "find_reviews", "pros_cons", "buyer_checklist", "extract_insights"],
  social_caption: ["suggest_captions", "generate_hashtags", "rewrite", "create_hook_variants", "short_caption", "long_caption"],
  prompt: ["optimize_prompt", "rewrite_structured", "make_concise", "add_output_format", "add_constraints"],
  general_text: ["extract_insights", "rewrite_structured", "summarize", "bullet_points", "rewrite", "translate", "expand_text", "grammar_fix"],
  image: ["describe_image", "extract_key_details", "identify_objects", "extract_dominant_colors", "generate_captions", "generate_alt_text"]
};

const DEFAULT_ACTION_BY_TYPE = {
  question: "answer_question",
  mcq: "answer_question",
  code: "explain_code",
  email: "polish_email",
  report: "extract_insights",
  product: "extract_product_info",
  social_caption: "suggest_captions",
  prompt: "optimize_prompt",
  general_text: "extract_insights",
  image: "describe_image"
};

const META_TOOL_ACTION_PATTERN =
  /^(search|search_web|web_search|google_search|lookup|look_up|browse|browse_web|open_browser|open_page|navigate|go_to|visit|click(?:_|$)|press(?:_|$)|fill(?:_|$)|select(?:_|$)|check(?:_|$)|submit(?:_|$)|send(?:_|$)|schedule(?:_|$)|take_action|execute_browser_action)/i;
const GENERIC_TEXT_ACTIONS = new Set(["extract_insights", "rewrite_structured", "summarize", "rewrite", "bullet_points"]);
const CODE_ACTIONS = new Set(["debug_code", "explain_code", "improve_code", "optimize_code"]);

const COMPATIBLE_ACTIONS_BY_TYPE = {
  question: new Set(["answer_question", "rewrite", "translate", "bullet_points", "summarize", "explain", "extract_insights"]),
  mcq: new Set(["answer_question", "explain", "bullet_points", "summarize", "extract_insights"]),
  code: new Set(["improve_code", "debug_code", "optimize_code", "explain_code", "summarize"]),
  email: new Set(["polish_email", "draft_reply", "rewrite", "translate", "summarize", "bullet_points", "explain"]),
  report: new Set(["extract_insights", "bullet_points", "summarize", "rewrite_structured", "rewrite", "translate", "explain"]),
  product: new Set(["extract_product_info", "compare_prices", "show_product_details", "find_reviews", "bullet_points", "summarize", "extract_insights"]),
  social_caption: new Set(["suggest_captions", "rewrite", "bullet_points", "summarize", "translate"]),
  prompt: new Set(["optimize_prompt", "rewrite_structured", "rewrite", "bullet_points", "summarize", "expand_text"]),
  general_text: new Set(["extract_insights", "rewrite_structured", "summarize", "bullet_points", "rewrite", "translate", "explain", "answer_question"]),
  image: new Set(["describe_image", "extract_key_details", "identify_objects", "summarize", "generate_captions", "extract_dominant_colors", "ocr_extract_text", "extract_table_data"])
};

function normalizeWhitespace(value) {
  return value.trim().toLowerCase().replaceAll("-", " ").replace(/\s+/g, " ");
}

function toSnakeCaseAction(action) {
  const candidate = action
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9\s_]/g, " ")
    .replace(/\s+/g, "_")
    .replace(/_+/g, "_")
    .replace(/^_+|_+$/g, "");

  return candidate || "summarize";
}

function isLikelyForeignLanguageText(text) {
  if (!text || !text.trim()) {
    return false;
  }

  if (/[\u0400-\u04FF\u0590-\u05FF\u0600-\u06FF\u0900-\u097F\u4E00-\u9FFF\u3040-\u30FF\uAC00-\uD7AF]/.test(text)) {
    return true;
  }

  const normalized = text.trim().toLowerCase();
  if (!normalized) {
    return false;
  }

  const accentedWordMatches =
    normalized.match(/\b[^\s]*[àáâãäåæçèéêëìíîïñòóôõöøœùúûüýÿ][^\s]*\b/gi)?.length ?? 0;

  const foreignCueMatches =
    normalized.match(/\b(?:bonjour|merci|pour|avec|dans|mais|vous|nous|leur|leurs|sont|comme|sans|depuis|toujours|notre|votre|réunion|demain|matin|veuillez|consulter|préparer|nécessaires|déplacement|gracias|hola|porque|cuando|donde|usted|ustedes|para|pero|aunque|mientras|hallo|danke|nicht|eine|einen|dieser|diese|dass|ciao|grazie|quando|dove)\b/gi)?.length ?? 0;
  const englishCueMatches =
    normalized.match(/\b(?:the|and|that|this|with|from|your|have|will|would|there|their|about|which|these|those|into|while|please|thanks|thank|because|where|when)\b/gi)?.length ?? 0;

  if (foreignCueMatches >= 3 && foreignCueMatches > englishCueMatches) {
    return true;
  }

  return accentedWordMatches >= 3 &&
    foreignCueMatches >= 1 &&
    englishCueMatches <= foreignCueMatches + 1;
}

function formatDateForPrompt(date = new Date()) {
  return date.toLocaleDateString("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric",
    timeZone: "UTC"
  });
}

export function normalizeActionHint(actionHint) {
  if (!actionHint) {
    return "summarize";
  }

  const normalized = normalizeWhitespace(actionHint);
  return ACTION_MAP.get(normalized) ?? toSnakeCaseAction(normalized);
}

export function normalizeContentType(contentType) {
  if (!contentType) {
    return "general_text";
  }

  const normalized = normalizeWhitespace(contentType).replaceAll(" ", "_");
  return KNOWN_CONTENT_TYPES.has(normalized) ? normalized : "general_text";
}

export function alternativesForType(type) {
  const normalizedType = normalizeContentType(type);
  return [...(ALTERNATIVES_BY_TYPE[normalizedType] ?? ALTERNATIVES_BY_TYPE.general_text)];
}

export function fallbackActionForType(type) {
  const normalizedType = normalizeContentType(type);
  return DEFAULT_ACTION_BY_TYPE[normalizedType] ?? "summarize";
}

export function isSevereActionConflict(action, contentType) {
  const normalizedType = normalizeContentType(contentType);
  const normalizedAction = normalizeActionHint(action);
  if (!KNOWN_ACTIONS.has(normalizedAction)) {
    // Allow novel Gemini-proposed actions to flow through unless explicitly unsafe elsewhere.
    return false;
  }

  const compatible = COMPATIBLE_ACTIONS_BY_TYPE[normalizedType] ?? COMPATIBLE_ACTIONS_BY_TYPE.general_text;
  return !compatible.has(normalizedAction);
}

export function looksLikeCode(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const trimmed = text.trim();
  if (looksLikeMailboxEmail(trimmed) || looksLikePolishedEmail(trimmed) || looksLikeRoughEmailDraft(trimmed)) {
    return false;
  }

  const punctuationMatches = trimmed.match(CODE_PUNCTUATION_FEATURE_PATTERN)?.length ?? 0;
  const lines = trimmed.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const structuredLines = lines.filter((line) =>
    /^(if|else|for|while|switch|try|catch|finally|function|class|public|private|protected|const|let|var|return|import|export|using|namespace)\b/i.test(line)
  ).length;

  if (CODE_FENCE_PATTERN.test(trimmed)) {
    return true;
  }

  if (CODE_KEYWORD_PATTERN.test(trimmed) || CODE_INLINE_FEATURE_PATTERN.test(trimmed)) {
    return CODE_SHAPED_LINE_PATTERN.test(trimmed) || punctuationMatches >= 2 || structuredLines >= 1;
  }

  return punctuationMatches >= 3 && structuredLines >= 1;
}

function hasUnbalancedDelimiters(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const openingToClosing = new Map([
    ["(", ")"],
    ["[", "]"],
    ["{", "}"]
  ]);
  const closingToOpening = new Map(
    [...openingToClosing.entries()].map(([opening, closing]) => [closing, opening])
  );

  const stack = [];
  let inSingleQuote = false;
  let inDoubleQuote = false;
  let inTemplateString = false;
  let escaped = false;

  for (const char of text) {
    if (escaped) {
      escaped = false;
      continue;
    }

    if (char === "\\") {
      escaped = true;
      continue;
    }

    if (!inDoubleQuote && !inTemplateString && char === "'") {
      inSingleQuote = !inSingleQuote;
      continue;
    }

    if (!inSingleQuote && !inTemplateString && char === "\"") {
      inDoubleQuote = !inDoubleQuote;
      continue;
    }

    if (!inSingleQuote && !inDoubleQuote && char === "`") {
      inTemplateString = !inTemplateString;
      continue;
    }

    if (inSingleQuote || inDoubleQuote || inTemplateString) {
      continue;
    }

    if (openingToClosing.has(char)) {
      stack.push(char);
      continue;
    }

    const expectedOpening = closingToOpening.get(char);
    if (!expectedOpening) {
      continue;
    }

    const lastOpening = stack.pop();
    if (lastOpening !== expectedOpening) {
      return true;
    }
  }

  return stack.length > 0 || inSingleQuote || inDoubleQuote || inTemplateString;
}

export function looksLikeBrokenCode(text) {
  if (!looksLikeCode(text)) {
    return false;
  }

  const trimmed = text.trim();
  if (!trimmed) {
    return false;
  }

  if (CODE_ERROR_PATTERN.test(trimmed)) {
    return true;
  }

  if (hasUnbalancedDelimiters(trimmed)) {
    return true;
  }

  if (INCOMPLETE_CODE_LINE_PATTERN.test(trimmed)) {
    return true;
  }

  const lines = trimmed.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  if (lines.length === 1 && /^(function|class|if|for|while|switch)\b/i.test(lines[0]) && !/[{};]$/.test(lines[0])) {
    return true;
  }

  return false;
}

export function inferUsefulCodeAction(text) {
  if (looksLikeBrokenCode(text)) {
    return "debug_code";
  }

  return "explain_code";
}

export function looksLikeProductText(text) {
  return Boolean(text && PRODUCT_PATTERN.test(text));
}

export function looksLikeMailboxEmail(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const firstLines = text.trim().split(/\r?\n/).slice(0, 4).join("\n");
  const emailMatchCount = firstLines.match(new RegExp(EMAIL_ADDRESS_PATTERN, "gi"))?.length ?? 0;

  return EMAIL_HEADER_PATTERN.test(text) ||
    EMAIL_THREAD_PATTERN.test(text) ||
    (emailMatchCount >= 1 && /\bto\b/i.test(firstLines)) ||
    (emailMatchCount >= 1 && /\b(on\s+.+\s+wrote:|forwarded message|original message)\b/i.test(text));
}

export function looksLikeBriefEmailText(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const normalized = text.trim();
  const hasEmailAddress = EMAIL_ADDRESS_PATTERN.test(normalized) || EMAIL_INLINE_ADDRESS_PATTERN.test(normalized);
  if (!hasEmailAddress) {
    return false;
  }

  const lineCount = normalized.split(/\r?\n/).filter((line) => line.trim()).length;
  const hasEmailCue =
    EMAIL_HEADER_PATTERN.test(normalized) ||
    EMAIL_THREAD_PATTERN.test(normalized) ||
    EMAIL_GREETING_PATTERN.test(normalized) ||
    EMAIL_CLOSING_PATTERN.test(normalized) ||
    /\b(thanks|thank you|please|regards|re:|fwd:|to)\b/i.test(normalized);

  return hasEmailCue || lineCount >= 2;
}

export function looksLikePolishedEmail(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const normalized = text.trim();
  const hasGreeting = /\b(dear|hi|hello)\b/i.test(normalized);
  const hasClosing = EMAIL_CLOSING_PATTERN.test(normalized);
  const punctuationHits = normalized.match(/[.!?](?:\s|$)/g)?.length ?? 0;
  const lineCount = normalized.split(/\r?\n/).filter((line) => line.trim()).length;

  return hasGreeting && hasClosing && punctuationHits >= 3 && lineCount >= 4;
}

export function looksLikeRoughEmailDraft(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const normalized = text.trim();
  const lines = normalized.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const hasEmailSignal =
    EMAIL_HEADER_PATTERN.test(normalized) ||
    EMAIL_ADDRESS_PATTERN.test(normalized) ||
    EMAIL_THREAD_PATTERN.test(normalized) ||
    EMAIL_GREETING_PATTERN.test(normalized) ||
    EMAIL_CLOSING_PATTERN.test(normalized);

  if (!hasEmailSignal) {
    return false;
  }

  const bodyLines = lines.filter((line) => !/^(from|to|cc|bcc|sent|date|subject):/i.test(line));
  const punctuationEndedLines = bodyLines.filter((line) => /[.!?]$/.test(line)).length;
  const lowercaseLines = bodyLines.filter((line) => /^[a-z]/.test(line)).length;

  let score = 0;
  if (!EMAIL_GREETING_PATTERN.test(normalized)) {
    score += 1;
  }

  if (!EMAIL_CLOSING_PATTERN.test(normalized)) {
    score += 1;
  }

  if (bodyLines.length >= 2 && punctuationEndedLines < Math.max(1, Math.floor(bodyLines.length / 2))) {
    score += 1;
  }

  if (bodyLines.length >= 2 && lowercaseLines >= Math.max(2, Math.ceil(bodyLines.length / 2))) {
    score += 1;
  }

  if (!looksLikePolishedEmail(normalized) && /[a-z0-9][\r\n]+[a-z]/.test(normalized)) {
    score += 1;
  }

  return score >= 2;
}

export function inferUsefulEmailAction(text) {
  if (!text || !text.trim()) {
    return "polish_email";
  }

  if (isLikelyForeignLanguageText(text)) {
    return "translate";
  }

  if (looksLikeRoughEmailDraft(text)) {
    return "polish_email";
  }

  if (looksLikeMailboxEmail(text) || looksLikePolishedEmail(text) || looksLikeBriefEmailText(text)) {
    return "draft_reply";
  }

  return "polish_email";
}

export function looksLikeMcq(text) {
  return Boolean(text && (MCQ_PATTERN.test(text) || MCQ_OPTION_PATTERN.test(text) || looksLikeQuestionSet(text)));
}

export function looksLikeQuestionSet(text) {
  if (!text || !text.trim()) {
    return false;
  }

  if (QUESTION_SET_PATTERN.test(text)) {
    return true;
  }

  const lines = text.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const numberedQuestions = lines.filter((line) => QUESTION_SET_LINE_PATTERN.test(line)).length;
  const questionLines = lines.filter((line) => line.includes("?")).length;

  return numberedQuestions >= 2 || (numberedQuestions >= 1 && questionLines >= 2);
}

function looksLikeLongInformationalText(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const trimmed = text.trim();
  if (looksLikeQuestionSet(trimmed)) {
    return false;
  }

  const lines = trimmed.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const wordCount = trimmed.split(/\s+/).filter(Boolean).length;
  const sentenceCount = trimmed.match(/[.!?](?:\s|$)/g)?.length ?? 0;
  const questionLines = lines.filter((line) => line.includes("?")).length;

  return (wordCount >= 60 || trimmed.length >= 420) &&
    sentenceCount >= 3 &&
    questionLines <= Math.max(1, Math.floor(lines.length / 4));
}

export function isQuestionText(text) {
  if (!text || !text.trim()) {
    return false;
  }

  const trimmed = text.trim();
  if (looksLikeQuestionSet(trimmed)) {
    return true;
  }

  const normalized = trimmed.toLowerCase();
  const lines = trimmed.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const questionLines = lines.filter((line) => line.includes("?")).length;
  const wordCount = normalized.split(/\s+/).filter(Boolean).length;

  if (trimmed.endsWith("?")) {
    return true;
  }

  if (lines.length <= 5 && questionLines >= 1 && questionLines >= Math.ceil(lines.length / 2)) {
    return true;
  }

  if (looksLikeLongInformationalText(trimmed)) {
    return false;
  }

  if (wordCount <= 18 && QUESTION_PREFIX_PATTERN.test(normalized)) {
    return true;
  }

  if (wordCount <= 12 && PHRASE_QUERY_PATTERN.test(normalized)) {
    return true;
  }

  if (wordCount <= 14 && PHRASE_QUERY_PATTERN.test(normalized) && FACT_QUERY_TOPIC_PATTERN.test(normalized)) {
    return true;
  }

  if (wordCount <= 14 && REGION_OR_SCOPE_PATTERN.test(normalized) && FACT_QUERY_TOPIC_PATTERN.test(normalized)) {
    return true;
  }

  if (/^(mcq|multiple choice|choose (the )?correct)/i.test(normalized)) {
    return true;
  }

  if (MCQ_OPTION_PATTERN.test(normalized)) {
    return true;
  }

  return false;
}

export function isMetaToolAction(action) {
  if (!action || !String(action).trim()) {
    return false;
  }

  return META_TOOL_ACTION_PATTERN.test(normalizeActionHint(String(action)));
}

export function inferFallbackType(text) {
  if (!text || !text.trim()) {
    return "general_text";
  }

  if (EMAIL_PATTERN.test(text) || looksLikeMailboxEmail(text) || looksLikePolishedEmail(text) || looksLikeRoughEmailDraft(text) || looksLikeBriefEmailText(text)) {
    return "email";
  }

  if (looksLikeCode(text)) {
    return "code";
  }

  if (looksLikeProductText(text)) {
    return "product";
  }

  if (CAPTION_PATTERN.test(text)) {
    return "social_caption";
  }

  if (looksLikeMcq(text)) {
    return "mcq";
  }

  if (text.length > 500 || /\b(summary|findings|analysis|report)\b/i.test(text) || looksLikeLongInformationalText(text)) {
    return "report";
  }

  if (isQuestionText(text)) {
    return "question";
  }

  return "general_text";
}

export function isLikelyTimeSensitiveQuestion(text) {
  if (!isQuestionText(text)) {
    return false;
  }

  return Boolean(text && TIME_SENSITIVE_PATTERN.test(text));
}

export function extendedAlternativesForType(type, selectionText = "") {
  const normalizedType = normalizeContentType(type);
  const base = [...(EXTENDED_ALTERNATIVES_BY_TYPE[normalizedType] ?? EXTENDED_ALTERNATIVES_BY_TYPE.general_text)];

  if (isLikelyForeignLanguageText(selectionText) && !base.includes("translate")) {
    base.unshift("translate");
  }

  return base
    .map((action) => normalizeActionHint(action))
    .filter(Boolean)
    .filter((value, index, array) => array.indexOf(value) === index);
}

export function buildIntentRouterPrompt({
  text,
  mode,
  actionHint,
  voiceCommand
}) {
  const clippedText = text.trim().slice(0, 9000);

  return [
    "You are the Cursivis intent router.",
    "Return strict JSON only. No markdown, no commentary.",
    "JSON schema:",
    JSON.stringify(
      {
        contentType: "question|mcq|code|email|report|product|social_caption|prompt|general_text",
        bestAction:
          "short snake_case action name. Prefer the most useful action, not the most generic one. Create a new action when clearly better (e.g. punctuate_email, convert_to_table, extract_deadlines).",
        confidence: 0.0,
        alternatives: ["3-8 action names, snake_case, distinct follow-up actions"]
      },
      null,
      2
    ),
    "Routing rules:",
    "- You are not choosing from a fixed menu. bestAction may be any concise snake_case action that would genuinely help the user.",
    "- Questions and MCQs should map to answer_question.",
    "- If the selection looks like a quiz, form, question set, or multiple numbered questions, treat it as mcq/question answering rather than a single free-form question.",
    "- For question sets, answer each objective item and mark user-specific personal fields as needing user input instead of answering as the AI.",
    "- Short factual noun phrases like 'smartest person in the world' should also map to answer_question.",
    "- Use answer_question only when the selection is actually asking something or is a short factual query. Long informational excerpts, articles, Wikipedia-like text, biographies, and reports are not questions just because they contain facts.",
    "- Do not classify long encyclopedia-style text as a question unless the text itself clearly asks one.",
    "- Code should map to the most useful code help: debug_code for broken, incomplete, or failing code; explain_code for correct code that mainly needs explanation; improve_code for valid code that clearly needs refactoring or cleanup.",
    "- Detect general code breakage patterns such as mismatched delimiters, incomplete statements, syntax/runtime errors, missing structure, or compiler/exception messages.",
    "- For email content, choose the most useful email help: polish_email for rough drafts, draft_reply for already-written messages or email threads, summarize for long threads, translate for foreign-language email.",
    "- Do not default to polish_email if the selected email already reads polished or looks like a sent/received message with mailbox metadata.",
    "- Examples: polished email -> draft_reply; rough or poorly punctuated email -> polish_email; valid code -> explain_code; broken code -> debug_code.",
    "- Also use the same principle more broadly: long reports -> summarize or extract_insights; foreign-language text -> translate; raw unstructured notes -> rewrite or rewrite_structured; YouTube description or caption seeds -> expand_text or suggest_captions; direct questions or factual phrases like 'fastest man on earth' -> answer_question.",
    "- Reports or long factual text should map to extract_insights, bullet_points, or summarize depending on what is most useful.",
    "- Product-related text should map to extract_product_info, compare_prices, show_product_details, or another clearly useful product action.",
    "- Social caption requests should map to suggest_captions.",
    "- If text appears non-English, include translate and choose it when translation is most useful.",
    "- Selection is the context and trigger is the intent. Infer the single most useful thing the user likely wants right now.",
    "- alternatives must be realistic follow-up actions the user might want after the main result.",
    "- Keep alternatives to 3-8 distinct actions.",
    `Mode: ${mode || "smart"}`,
    `Action hint: ${actionHint || "none"}`,
    `Voice command: ${voiceCommand || "none"}`,
    "Selected text:",
    clippedText
  ].join("\n\n");
}

export function describeIntentRoutingConcern(routerOutput, selectionText = "") {
  if (!selectionText || !selectionText.trim()) {
    return "";
  }

  const normalizedType = normalizeContentType(routerOutput?.contentType ?? "");
  const bestAction = routerOutput?.bestAction
    ? normalizeActionHint(String(routerOutput.bestAction))
    : "";
  const fallbackType = inferFallbackType(selectionText);

  if (!bestAction) {
    return "";
  }

  if (isMetaToolAction(bestAction)) {
    return "Choose the single most useful content action, not a browser/search tool action.";
  }

  if (
    (normalizedType === "code" || CODE_ACTIONS.has(bestAction)) &&
    fallbackType !== "code" &&
    !looksLikeCode(selectionText)
  ) {
    return fallbackType === "email"
      ? "The selection looks like email text, not code. Re-evaluate and choose the most useful email action."
      : "The selection does not appear to be code. Re-evaluate and choose the most useful non-code action.";
  }

  if (
    (normalizedType === "question" || bestAction === "answer_question") &&
    looksLikeLongInformationalText(selectionText) &&
    !isQuestionText(selectionText)
  ) {
    return "The selection is long informational prose, not a direct question. Re-evaluate and prefer a summary, insights, rewrite, or another content-focused action.";
  }

  if (
    (fallbackType === "question" || fallbackType === "mcq") &&
    normalizedType === "general_text" &&
    GENERIC_TEXT_ACTIONS.has(bestAction)
  ) {
    return "The selection looks like a question or MCQ. Re-evaluate and choose the most useful answer-oriented action.";
  }

  return "";
}

export function normalizeIntentDecision(routerOutput, selectionText = "") {
  const fallbackType = inferFallbackType(selectionText);
  let normalizedType = normalizeContentType(routerOutput?.contentType ?? fallbackType);
  const hasRouterAction = Boolean(routerOutput?.bestAction && String(routerOutput.bestAction).trim());
  let bestAction = hasRouterAction
    ? normalizeActionHint(routerOutput.bestAction)
    : normalizedType === "email"
      ? inferUsefulEmailAction(selectionText)
      : normalizedType === "code"
      ? inferUsefulCodeAction(selectionText)
      : fallbackActionForType(normalizedType);
  const rawConfidence = Number(routerOutput?.confidence);
  const confidence =
    Number.isFinite(rawConfidence) && rawConfidence >= 0 && rawConfidence <= 1
      ? rawConfidence
      : 0.72;

  const rawAlternatives = Array.isArray(routerOutput?.alternatives) ? routerOutput.alternatives : [];
  const normalizedAlternatives = rawAlternatives
    .map((action) => normalizeActionHint(String(action)))
    .filter(Boolean);

  if (
    !hasRouterAction &&
    isLikelyForeignLanguageText(selectionText) &&
    (bestAction === "summarize" || bestAction === "rewrite" || bestAction === "rewrite_structured")
  ) {
    bestAction = "translate";
  }

  if (
    isLikelyForeignLanguageText(selectionText) &&
    (normalizedType === "general_text" || normalizedType === "report") &&
    (bestAction === "summarize" ||
      bestAction === "rewrite" ||
      bestAction === "rewrite_structured" ||
      bestAction === "extract_insights" ||
      bestAction === "bullet_points")
  ) {
    bestAction = "translate";
  }

  const alternatives = [bestAction, ...normalizedAlternatives, ...alternativesForType(normalizedType)]
    .filter(Boolean)
    .filter((value, index, array) => array.indexOf(value) === index)
    .slice(0, 5);

  return {
    contentType: normalizedType,
    bestAction,
    confidence,
    alternatives
  };
}

export function buildPrompt({
  text,
  action,
  contentType,
  voiceCommand,
  useGrounding = false
}) {
  const normalizedAction = normalizeActionHint(action);
  const normalizedType = normalizeContentType(contentType);
  const explicitDate = formatDateForPrompt();
  const isQuestionSet = normalizedType === "mcq" || looksLikeQuestionSet(text);

  if (voiceCommand && voiceCommand.trim()) {
    return [
      "Apply the spoken command to the selected text and return only the final output.",
      "Keep the output concise and useful.",
      `Spoken command: ${voiceCommand.trim()}`,
      "Selected text:",
      text
    ].join("\n\n");
  }

  switch (normalizedAction) {
    case "answer_question":
      return isQuestionSet
        ? [
            "Solve the selected question set / MCQ form and return a clean answer key.",
            "For each answerable question, output: Q<number> [short question label]: <best answer option/text> - <very short reason>.",
            "For factual, scientific, explanatory, definitional, or otherwise objective short-answer questions, provide the best direct answer text instead of leaving it blank.",
            "Only if a question asks for truly personal or user-specific information that cannot be inferred from the selection, output: Q<number> [short question label]: Needs user input.",
            "Do not answer personal prompts as the AI itself.",
            "Keep each explanation very short and practical.",
            "Do not tell the user to search the web or do more research unless explicitly asked.",
            useGrounding
              ? `Use live web grounding when the answer is time-sensitive and mention 'As of ${explicitDate}' once if needed.`
              : "If a fact may change over time, include a date qualifier and avoid fake certainty.",
            "MCQ content:",
            text
          ].join("\n\n")
        : [
            "Answer the question directly and concisely.",
            "Return 2-4 sentences max unless a short list is clearly better.",
            "Do not tell the user to search the web, look it up, or do further research unless they explicitly asked for search steps.",
            useGrounding
              ? `Use live web grounding and start with 'As of ${explicitDate}, ...'.`
              : "If the fact may change over time, include a date qualifier and avoid fake certainty.",
            "Question:",
            text
          ].join("\n\n");
    case "polish_email":
      return [
        "Rewrite this into a stronger, cleaner, and more concise professional email.",
        "Make a materially useful improvement even if the original is already decent.",
        "Tighten wording, improve clarity, reduce repetition, and sharpen professionalism.",
        "Do not include raw mailbox metadata like To:, From:, Cc:, or Bcc: in the output.",
        "Return only the improved subject line if one is clear, followed by the final email body.",
        "Email draft:",
        text
      ].join("\n\n");
    case "draft_reply":
      return [
        "Draft the most useful reply or follow-up email for this selected email.",
        "If the selected message looks like a sent or received email rather than an unfinished draft, prefer writing a concise, practical reply.",
        "Keep it professional, specific, and ready to send.",
        "Do not include raw mailbox metadata like To:, From:, Cc:, or Bcc: in the output.",
        "Return only an optional Subject line plus the final reply body.",
        "Selected email:",
        text
      ].join("\n\n");
    case "extract_insights":
      return [
        "Extract the most useful insights from this selection.",
        "Focus on the key conclusions, patterns, signals, or implications a user would actually care about.",
        "Return a compact bullet list or short structured summary, whichever is clearer.",
        "Text:",
        text
      ].join("\n\n");
    case "bullet_points":
      return [
        "Summarize into concise bullet points.",
        "Use 5-8 bullets max and keep each bullet short.",
        normalizedType === "report" ? "Prioritize key findings and decisions." : "Prioritize core takeaways.",
        "Text:",
        text
      ].join("\n\n");
    case "expand_text":
      return [
        "Expand this content with more detail while keeping it clear and useful.",
        "Preserve original intent and avoid fluff.",
        "Text:",
        text
      ].join("\n\n");
    case "rewrite_structured":
      return [
        "Rewrite the content into a clear structured format.",
        "Use a short heading and concise bullet points.",
        "Remove repetition and keep intent intact.",
        "Text:",
        text
      ].join("\n\n");
    case "optimize_prompt":
      return [
        "Rewrite the selected rough prompt, note, or idea dump into a clearer high-quality prompt for an LLM.",
        "Preserve the user's original intent, requirements, constraints, examples, tone, and scope.",
        "Do not invent facts, tools, names, deadlines, requirements, or details that are not present.",
        "Make the instructions specific, actionable, and easy for another model to follow.",
        "Use compact structure only when it helps, such as Goal, Context, Requirements, Constraints, and Output Format.",
        "If the selected text is not a prompt, clean it into fluent, well-structured writing while preserving meaning.",
        "Avoid overcomplicating the result. Omit unsupported sections instead of filling them with guesses.",
        "Return only the optimized prompt or cleaned text.",
        "Messy input:",
        text
      ].join("\n\n");
    case "rewrite":
      return [
        "Rewrite this to be clearer and more polished.",
        "Keep the meaning intact and concise.",
        "Text:",
        text
      ].join("\n\n");
    case "translate":
      return [
        "Translate this to English while preserving intent and tone.",
        "If already English, return a cleaner English rewrite.",
        "Text:",
        text
      ].join("\n\n");
    case "explain":
      return [
        "Explain this in plain language.",
        "Keep it concise and practical.",
        "Text:",
        text
      ].join("\n\n");
    case "improve_code":
      if (looksLikeBrokenCode(text)) {
        return [
          "This code appears broken, incomplete, or likely to fail.",
          "Fix the code first, then briefly explain the main issue and what changed.",
          "Return corrected code first, then a short 'Issue fixed' note.",
          "Code:",
          text
        ].join("\n\n");
      }

      return [
        "Improve the code quality and correctness.",
        "Return improved code first, then a short list of key fixes.",
        "Code:",
        text
      ].join("\n\n");
    case "debug_code":
      return [
        "Treat this as debugging/fix-first work.",
        "Identify likely syntax, runtime, or logic issues and provide corrected code.",
        "Return corrected code first, then concise notes on the issue and fix.",
        "Code:",
        text
      ].join("\n\n");
    case "optimize_code":
      return [
        "Optimize this code for readability and performance while preserving behavior.",
        "Return optimized code and short notes.",
        "Code:",
        text
      ].join("\n\n");
    case "explain_code":
      if (looksLikeBrokenCode(text)) {
        return [
          "The selected code appears broken or incomplete.",
          "Debug and repair it first, then explain the key issue in concise practical terms.",
          "Return corrected code first, then a short explanation.",
          "Code:",
          text
        ].join("\n\n");
      }

      return [
        "Explain what this code does and call out caveats.",
        "Keep it concise and practical.",
        "Code:",
        text
      ].join("\n\n");
    case "extract_product_info":
    case "show_product_details":
      return [
        "Extract product details in a compact structured list.",
        "Include: name/model, price mentions, specs/features, warranty/shipping if present.",
        "If a field is missing, write 'Not specified'.",
        "Text:",
        text
      ].join("\n\n");
    case "compare_prices":
      return [
        "Provide a concise buying comparison checklist.",
        "Include price drivers, what to verify, and red flags.",
        "Text:",
        text
      ].join("\n\n");
    case "find_reviews":
      return [
        "Summarize what review signals should be checked before buying.",
        "Include likely red flags and decision criteria.",
        "Text:",
        text
      ].join("\n\n");
    case "suggest_captions":
    case "generate_captions":
      return [
        "Generate 5 concise caption options.",
        "Vary tone and keep each caption ready to post.",
        "Include hashtag suggestions only if useful.",
        "Source text:",
        text
      ].join("\n\n");
    case "ocr_extract_text":
      return [
        "Extract all readable text from this content.",
        "Preserve important structure like headings and bullets.",
        "Text:",
        text
      ].join("\n\n");
    case "extract_table_data":
      return [
        "Extract any table-like structured data and format clearly.",
        "Prefer markdown table when suitable.",
        "Text:",
        text
      ].join("\n\n");
    default:
      if (normalizedType === "report" && normalizedAction.startsWith("extract_")) {
        return [
          `Perform this focused extraction on the text: ${normalizedAction.replaceAll("_", " ")}.`,
          "Prioritize the requested focus first.",
          "Also include any other materially important requirements, constraints, obligations, risks, or decisions needed to understand the selection properly.",
          "Do not omit critical context just because it falls outside the main focus.",
          "Return a compact structured list, and add 'Other important points' only when needed.",
          "Text:",
          text
        ].join("\n\n");
      }

      return [
        `Perform this operation on the text: ${normalizedAction.replaceAll("_", " ")}.`,
        "Return concise, practical output only.",
        "Text:",
        text
      ].join("\n\n");
  }
}
