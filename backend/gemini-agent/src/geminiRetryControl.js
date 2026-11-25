export class GeminiRequestStopped extends Error {
  constructor(reason) {
    const cancelled = reason === "cancelled";
    super(cancelled ? "The Gemini request was cancelled." : "The Gemini request ran out of time.");
    this.code = cancelled ? "CURSIVIS_GEMINI_CANCELLED" : "CURSIVIS_GEMINI_DEADLINE";
  }
}

export function checkGeminiRequest({ deadlineAt = Infinity, signal } = {}) {
  if (signal?.aborted) {
    throw new GeminiRequestStopped("cancelled");
  }
  const remainingMs = deadlineAt - Date.now();
  if (remainingMs <= 0) {
    throw new GeminiRequestStopped("deadline");
  }
  return remainingMs;
}
