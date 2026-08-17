const MINIMUM_KEY_LENGTH = 30;
const MAXIMUM_KEY_LENGTH = 256;
const PLACEHOLDER_PATTERN = /PASTE_YOUR|DEMO_KEY|YOUR_API_KEY/i;
const SAFE_TOKEN_PATTERN = /^[A-Za-z0-9._-]+$/;
const ALPHA_NUMERIC_PATTERN = /[A-Za-z0-9]/g;

export function parseGeminiApiKeyPool(value) {
  return String(value || "")
    .split(/[,;\n\r]+/)
    .map((entry) => entry.trim())
    .filter(Boolean);
}

export function isPlausibleGeminiCredential(value) {
  const key = String(value || "").trim();
  if (key.length < MINIMUM_KEY_LENGTH || key.length > MAXIMUM_KEY_LENGTH || PLACEHOLDER_PATTERN.test(key)) {
    return false;
  }

  if (!SAFE_TOKEN_PATTERN.test(key)) {
    return false;
  }

  const alphaNumericCount = (key.match(ALPHA_NUMERIC_PATTERN) || []).length;
  return alphaNumericCount >= 24 && !hasRepeatedPayload(key);
}

function hasRepeatedPayload(key) {
  const payload = key.startsWith("AQ.")
    ? key.slice(3)
    : key.startsWith("AIza")
      ? key.slice(4)
      : key;
  return /^([A-Za-z0-9._-])\1*$/.test(payload);
}

export function validateGeminiApiKeyPool(value) {
  const keys = parseGeminiApiKeyPool(value);
  const invalidIndex = keys.findIndex((key) => !isPlausibleGeminiCredential(key));
  if (invalidIndex >= 0) {
    return {
      ok: false,
      keys,
      error: `Key ${invalidIndex + 1} does not look like a valid Gemini API key. Check it and paste again.`
    };
  }

  return {
    ok: keys.length > 0,
    keys,
    error: keys.length === 0 ? "A valid Gemini API key is required." : ""
  };
}

export function redactGeminiCredentials(value) {
  return String(value || "")
    .replace(/(?:AIza[A-Za-z0-9_-]{20,}|AQ\.[A-Za-z0-9._-]{16,})/g, "[REDACTED_API_KEY]")
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{10,}/gi, "Bearer [REDACTED]")
    .replace(/([?&](?:key|api[_-]?key)=)[^&\s]+/gi, "$1[REDACTED]");
}
