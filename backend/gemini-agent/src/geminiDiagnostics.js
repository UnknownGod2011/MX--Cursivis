import { appendFile, mkdir } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { redactGeminiCredentials } from "./geminiCredentials.js";

const LOG_DIRECTORY = process.env.LOCALAPPDATA
  ? path.join(process.env.LOCALAPPDATA, "Cursivis", "Logs")
  : path.join(os.homedir(), ".cursivis", "logs");
const LOG_PATH = path.join(LOG_DIRECTORY, "gemini-backend.log");
let logDirectoryPromise = null;

export function traceGeminiEvent(event, values = {}) {
  if (!isTracingEnabled()) {
    return;
  }

  const fields = Object.entries(values)
    .filter(([, value]) => value !== undefined && value !== null && value !== "")
    .map(([key, value]) => `${key}=${JSON.stringify(String(value).replace(/\s+/g, " "))}`);
  const line = `${new Date().toISOString()} event=${event} ${fields.join(" ")}`.trim();

  console.info(`[cursivis-gemini] ${line}`);
  void ensureLogDirectory()
    .then(() => appendFile(LOG_PATH, `${line}\n`, "utf8"))
    .catch(() => {
      // Diagnostics must never interrupt a user request.
    });
}

export function getGeminiErrorDiagnostics(error) {
  const errorText = redactSensitiveText(getErrorText(error));
  const directStatus = Number(error?.status ?? error?.code);
  const status = Number.isInteger(directStatus) && directStatus >= 100 && directStatus <= 599
    ? directStatus
    : Number(errorText.match(/\b([45]\d\d)\b/)?.[1]) || null;

  return {
    status,
    error: errorText.slice(0, 500)
  };
}

function isTracingEnabled() {
  return process.env.CURSIVIS_GEMINI_TRACE !== "0" && !process.env.NODE_TEST_CONTEXT;
}

function ensureLogDirectory() {
  logDirectoryPromise ??= mkdir(LOG_DIRECTORY, { recursive: true });
  return logDirectoryPromise;
}

function getErrorText(error) {
  if (error instanceof Error) {
    return [error.message, error.code, error.status, error.statusText]
      .filter(Boolean)
      .join(" ");
  }

  if (error && typeof error === "object") {
    return [error.message, error.code, error.status, error.statusText]
      .filter(Boolean)
      .join(" ");
  }

  return String(error || "");
}

function redactSensitiveText(value) {
  return redactGeminiCredentials(value)
    .replace(/\bsk-[A-Za-z0-9_-]{10,}/g, "[REDACTED_API_KEY]")
    .replace(/\s+/g, " ");
}
