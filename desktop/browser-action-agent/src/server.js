import express from "express";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const port = Number(process.env.CURSIVIS_BROWSER_AGENT_PORT || 48820);
const dataDir = path.resolve(__dirname, "..", "data", "browser-profile");

let browserContext = null;
let launchPromise = null;
let lastKnownPage = null;
let activeChannel = "unknown";

const app = express();
app.use(express.json({ limit: "10mb" }));

app.get("/health", (_req, res) => {
  res.json({
    ok: true,
    service: "cursivis-browser-action-agent",
    browserReady: Boolean(browserContext),
    browserChannel: activeChannel,
    timestampUtc: new Date().toISOString()
  });
});

app.post("/ensure-browser", async (req, res) => {
  try {
    const preferredChannel = typeof req.body?.preferredChannel === "string"
      ? req.body.preferredChannel
      : "";
    const openUrl = typeof req.body?.openUrl === "string"
      ? req.body.openUrl
      : "";
    const page = await getCurrentPage({
      preferredChannel,
      openUrl
    });
    const pageContext = await buildPageContext(page);
    res.json({
      ok: true,
      browserChannel: activeChannel,
      pageContext
    });
  } catch (error) {
    res.status(500).json({
      error: "Failed to initialize browser automation session.",
      details: getErrorMessage(error)
    });
  }
});

app.get("/page-context", async (_req, res) => {
  try {
    const page = await getCurrentPage();
    res.json({
      ok: true,
      pageContext: await buildPageContext(page)
    });
  } catch (error) {
    res.status(500).json({
      error: "Failed to inspect browser page.",
      details: getErrorMessage(error)
    });
  }
});

app.post("/execute-plan", async (req, res) => {
  const steps = Array.isArray(req.body?.steps) ? req.body.steps : null;
  if (!steps) {
    return res.status(400).json({
      error: "steps array is required."
    });
  }

  try {
    const page = await getCurrentPage();
    const logs = [];
    let executedSteps = 0;

    await page.bringToFront();
    for (const step of steps) {
      const normalized = normalizeStep(step);
      if (!normalized) {
        continue;
      }

      await executeStep(page, normalized, logs);
      executedSteps += 1;
    }

    return res.json({
      ok: true,
      success: true,
      executedSteps,
      message: executedSteps > 0 ? "Browser actions completed." : "No browser actions were executed.",
      logs,
      pageContext: await buildPageContext(page)
    });
  } catch (error) {
    return res.status(500).json({
      ok: false,
      success: false,
      message: "Browser action execution failed.",
      details: getErrorMessage(error)
    });
  }
});

app.listen(port, () => {
  // eslint-disable-next-line no-console
  console.log(`[browser-action-agent] Listening on http://127.0.0.1:${port}`);
});

function getErrorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

async function ensureBrowserContext(preferredChannel = "") {
  const normalizedPreferred = String(preferredChannel || "").trim().toLowerCase();

  if (browserContext) {
    if (
      normalizedPreferred &&
      activeChannel &&
      activeChannel !== "unknown" &&
      activeChannel !== normalizedPreferred
    ) {
      await browserContext.close().catch(() => {});
      browserContext = null;
      lastKnownPage = null;
      activeChannel = "unknown";
    } else {
      return browserContext;
    }
  }

  if (browserContext) {
    return browserContext;
  }

  if (launchPromise) {
    return launchPromise;
  }

  launchPromise = (async () => {
    fs.mkdirSync(dataDir, { recursive: true });

    const preferredChannel = normalizedPreferred || (process.env.CURSIVIS_BROWSER_CHANNEL || "chrome").trim().toLowerCase();
    const launchTargets = [
      { channel: preferredChannel || "chrome" },
      { channel: preferredChannel === "chrome" ? "msedge" : "chrome" },
      { channel: null }
    ].filter((target, index, array) =>
      array.findIndex((candidate) => candidate.channel === target.channel) === index);

    let lastError = null;
    for (const target of launchTargets) {
      try {
        const context = await chromium.launchPersistentContext(dataDir, {
          channel: target.channel || undefined,
          headless: false,
          viewport: null,
          args: ["--start-maximized"]
        });

        activeChannel = target.channel || "chromium";
        wireContext(context);
        browserContext = context;
        return context;
      } catch (error) {
        lastError = error;
      }
    }

    throw lastError ?? new Error("Unable to launch browser automation session.");
  })();

  try {
    return await launchPromise;
  } finally {
    launchPromise = null;
  }
}

function wireContext(context) {
  for (const page of context.pages()) {
    wirePage(page);
  }

  context.on("page", (page) => {
    wirePage(page);
  });
}

function wirePage(page) {
  lastKnownPage = page;
  page.on("popup", (popup) => {
    wirePage(popup);
  });
  page.on("framenavigated", () => {
    lastKnownPage = page;
  });
  page.on("close", () => {
    if (lastKnownPage === page) {
      lastKnownPage = null;
    }
  });
}

async function getCurrentPage({ preferredChannel = "", openUrl = "" } = {}) {
  const context = await ensureBrowserContext(preferredChannel);
  let pages = context.pages().filter((page) => !page.isClosed());

  if (pages.length === 0) {
    const page = await context.newPage();
    await page.goto("https://www.google.com", { waitUntil: "domcontentloaded" });
    pages = [page];
    wirePage(page);
  }

  let page = lastKnownPage && !lastKnownPage.isClosed() ? lastKnownPage : pages[pages.length - 1];
  if (page.isClosed()) {
    page = pages[pages.length - 1];
  }

  await page.bringToFront();
  if (openUrl && isSafeNavigationUrl(openUrl) && !sameUrl(page.url(), openUrl)) {
    await page.goto(openUrl, { waitUntil: "domcontentloaded" });
  }

  return page;
}

async function buildPageContext(page) {
  const raw = await page.evaluate(() => {
    const normalizeText = (value) => (value || "").replace(/\s+/g, " ").trim();
    const visibleText = normalizeText(document.body?.innerText || "").slice(0, 4000);

    const interactiveElements = [];
    const candidates = Array.from(document.querySelectorAll("button, a, input, textarea, select, [role], label, [contenteditable='true']"));
    for (const element of candidates) {
      const style = window.getComputedStyle(element);
      if (style.visibility === "hidden" || style.display === "none") {
        continue;
      }

      const rect = element.getBoundingClientRect();
      if (rect.width < 2 || rect.height < 2) {
        continue;
      }

      const tagName = element.tagName.toLowerCase();
      const role = element.getAttribute("role") || (element.hasAttribute("contenteditable") ? "textbox" : tagName);
      const label =
        normalizeText(element.getAttribute("aria-label")) ||
        normalizeText(element.getAttribute("title")) ||
        normalizeText(element.getAttribute("placeholder")) ||
        normalizeText(element.innerText) ||
        normalizeText(element.textContent);

      if (!label && !["input", "textarea", "select"].includes(tagName)) {
        continue;
      }

      const nameAttribute = normalizeText(element.getAttribute("name"));
      const type = normalizeText(element.getAttribute("type")) || tagName;
      const options =
        tagName === "select"
          ? Array.from(element.querySelectorAll("option")).map((option) => normalizeText(option.textContent)).filter(Boolean).slice(0, 10)
          : [];

      interactiveElements.push({
        role,
        label,
        nameAttribute,
        type,
        options
      });

      if (interactiveElements.length >= 120) {
        break;
      }
    }

    return {
      url: window.location.href,
      title: document.title || "",
      visibleText,
      interactiveElements
    };
  });

  return {
    url: raw.url,
    title: raw.title,
    visibleText: raw.visibleText,
    interactiveElements: raw.interactiveElements,
    browserChannel: activeChannel
  };
}

function normalizeStep(step) {
  if (!step || typeof step !== "object" || typeof step.tool !== "string") {
    return null;
  }

  const normalized = {
    tool: step.tool.trim().toLowerCase()
  };

  for (const key of ["role", "name", "text", "label", "nameAttribute", "placeholder", "question", "option", "url", "key"]) {
    if (typeof step[key] === "string" && step[key].trim()) {
      normalized[key] = step[key].trim();
    }
  }

  if (Array.isArray(step.answers)) {
    const answers = step.answers
      .map((answer) => ({
        question: typeof answer?.question === "string" && answer.question.trim() ? answer.question.trim() : undefined,
        option: typeof answer?.option === "string" ? answer.option.trim() : ""
      }))
      .filter((answer) => answer.option)
      .slice(0, 20);

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

  return normalized;
}

function regexFromText(text) {
  return new RegExp(escapeRegex(text), "i");
}

function escapeRegex(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function escapeCssAttribute(value) {
  return value.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
}

async function executeStep(page, step, logs) {
  logs.push(`${step.tool}`);

  switch (step.tool) {
    case "navigate":
      if (!step.url) {
        throw new Error("navigate step requires url.");
      }
      await page.goto(step.url, { waitUntil: "domcontentloaded" });
      return;
    case "open_new_tab": {
      const context = await ensureBrowserContext();
      const newPage = await context.newPage();
      wirePage(newPage);
      if (step.url) {
        await newPage.goto(step.url, { waitUntil: "domcontentloaded" });
      }
      lastKnownPage = newPage;
      return;
    }
    case "switch_tab": {
      const context = await ensureBrowserContext();
      const pages = context.pages().filter((candidate) => !candidate.isClosed());
      const query = (step.name || step.text || step.url || "").trim().toLowerCase();
      let match = null;
      if (query) {
        for (const candidate of pages) {
          const title = await candidate.title().catch(() => "");
          const haystack = `${candidate.url()} ${title}`.toLowerCase();
          if (haystack.includes(query)) {
            match = candidate;
            break;
          }
        }
      } else {
        match = pages.at(-1) ?? null;
      }
      if (!match) {
        throw new Error(`Unable to locate a matching managed browser tab for '${query}'.`);
      }
      await match.bringToFront();
      lastKnownPage = match;
      return;
    }
    case "click_role":
      await clickByRole(page, step);
      return;
    case "click_text":
      await page.getByText(regexFromText(step.text || step.name || ""), { exact: false }).first().click();
      return;
    case "fill_label":
      await fillByLabel(page, step);
      return;
    case "fill_name":
      await page.locator(`[name="${escapeCssAttribute(step.nameAttribute || step.name || "")}"]`).first().fill(step.text || "");
      return;
    case "fill_placeholder":
      await page.getByPlaceholder(regexFromText(step.placeholder || step.label || "")).first().fill(step.text || "");
      return;
    case "fill_editor":
      await fillEditor(page, step);
      return;
    case "type_active":
      await page.keyboard.type(step.text || "");
      return;
    case "select_option":
      await selectOption(page, step);
      return;
    case "check_radio":
      await checkChoice(page, "radio", step);
      return;
    case "check_checkbox":
      await checkChoice(page, "checkbox", step);
      return;
    case "apply_answer_key":
      await applyAnswerKey(page, step, logs);
      return;
    case "press_key":
      await page.keyboard.press(step.key || "Enter");
      return;
    case "scroll":
      await page.evaluate((mode) => {
        const normalized = String(mode || "down").toLowerCase();
        if (normalized.includes("top")) {
          window.scrollTo({ top: 0, behavior: "smooth" });
          return;
        }

        if (normalized.includes("bottom")) {
          window.scrollTo({ top: document.body.scrollHeight, behavior: "smooth" });
          return;
        }

        const delta = normalized.includes("up") ? -window.innerHeight * 0.8 : window.innerHeight * 0.8;
        window.scrollBy({ top: delta, behavior: "smooth" });
      }, step.text || step.name || "down");
      return;
    case "extract_dom":
      await buildPageContext(page);
      return;
    case "wait_for_text":
      await page.getByText(regexFromText(step.text || step.name || "")).first().waitFor({ timeout: 5000 });
      return;
    case "wait_ms":
      await new Promise((resolve) => setTimeout(resolve, step.waitMs || 300));
      return;
    default:
      throw new Error(`Unsupported tool: ${step.tool}`);
  }
}

async function fillByLabel(page, step) {
  const label = step.label || step.name || "";
  const text = step.text || "";
  if (!label) {
    throw new Error("fill_label requires a label.");
  }

  if (containsEditorSemanticLabel(label)) {
    await fillEditor(page, step);
    return;
  }

  for (const candidate of expandFieldLabels(label)) {
    try {
      await page.getByLabel(regexFromText(candidate)).first().fill(text);
      return;
    } catch {
      // Continue to more permissive fallbacks below.
    }

    try {
      await page.getByRole("textbox", { name: regexFromText(candidate) }).first().fill(text);
      return;
    } catch {
      // Continue.
    }

    const escapedLabel = escapeCssAttribute(candidate);
    for (const selector of [
      `[aria-label="${escapedLabel}"]`,
      `[title="${escapedLabel}"]`,
      `[name="${escapedLabel}"]`,
      `[placeholder="${escapedLabel}"]`,
      `[contenteditable="true"][aria-label="${escapedLabel}"]`
    ]) {
      const locator = page.locator(selector).first();
      if (await locator.count().catch(() => 0)) {
        await locator.fill(text);
        return;
      }
    }
  }

  if (containsMailBodyLabel(label)) {
    for (const selector of [
      `[aria-label="Message Body"]`,
      `[aria-label="Message body"]`,
      `[aria-label="Message"]`,
      `[role="textbox"][contenteditable="true"]`,
      `[contenteditable="true"]`
    ]) {
      const locator = page.locator(selector).first();
      if (await locator.count().catch(() => 0)) {
        try {
          await locator.fill(text);
        } catch {
          await locator.click();
          await page.keyboard.press("Control+A").catch(() => {});
          await page.keyboard.type(text);
        }
        return;
      }
    }
  }

  throw new Error(`Unable to locate a fillable field for label: ${label}`);
}

async function fillEditor(page, step) {
  const label = step.label || step.name || "Message";
  const text = step.text || "";

  for (const candidate of expandEditorLabels(label)) {
    try {
      await writeIntoLocator(page.getByLabel(regexFromText(candidate)).first(), text);
      return;
    } catch {
      // Continue.
    }

    try {
      await writeIntoLocator(page.getByRole("textbox", { name: regexFromText(candidate) }).first(), text);
      return;
    } catch {
      // Continue.
    }

    const escapedLabel = escapeCssAttribute(candidate);
    for (const selector of [
      `[aria-label="${escapedLabel}"]`,
      `[aria-placeholder="${escapedLabel}"]`,
      `[data-placeholder="${escapedLabel}"]`,
      `[title="${escapedLabel}"]`,
      `[name="${escapedLabel}"]`,
      `[placeholder="${escapedLabel}"]`,
      `[contenteditable="true"][aria-label="${escapedLabel}"]`,
      `[role="textbox"][aria-label="${escapedLabel}"]`
    ]) {
      const locator = page.locator(selector).first();
      if (await locator.count().catch(() => 0)) {
        await writeIntoLocator(locator, text);
        return;
      }
    }
  }

  for (const selector of [
    `[role="textbox"][contenteditable="true"]`,
    `[contenteditable="true"][aria-multiline="true"]`,
    `[contenteditable="true"]`,
    `textarea`,
    `[role="textbox"]`
  ]) {
    const locator = page.locator(selector).first();
    if (await locator.count().catch(() => 0)) {
      await writeIntoLocator(locator, text);
      return;
    }
  }

  throw new Error(`Unable to locate a rich editor for label: ${label}`);
}

async function selectOption(page, step) {
  const optionText = step.option || step.text || "";
  if (!optionText) {
    throw new Error("select_option requires option text.");
  }

  const label = step.label || step.name || "";
  if (label) {
    try {
      await page.getByLabel(regexFromText(label)).first().selectOption({ label: optionText });
      return;
    } catch {
      // Fall through to alternate locators.
    }

    try {
      await page.getByRole("combobox", { name: regexFromText(label) }).first().selectOption({ label: optionText });
      return;
    } catch {
      // Fall through to generic select locator.
    }
  }

  await page.locator("select").first().selectOption({ label: optionText });
}

async function checkChoice(page, role, step) {
  const option = step.option || step.label || step.name || "";
  if (!option) {
    throw new Error(`${role} step requires option or label.`);
  }

  const optionRegex = regexFromText(option);
  const question = step.question || "";

  if (question) {
    const questionRegex = regexFromText(question);
    const groups = page.locator("fieldset, form, section, div[role='radiogroup'], div[role='group']");
    const groupCount = await groups.count();
    for (let index = 0; index < groupCount; index += 1) {
      const group = groups.nth(index);
      const groupText = await group.innerText().catch(() => "");
      if (!questionRegex.test(groupText)) {
        continue;
      }

      try {
        await group.getByRole(role, { name: optionRegex }).first().check();
        return;
      } catch {
        try {
          await group.getByLabel(optionRegex).first().check();
          return;
        } catch {
          // Continue scanning.
        }
      }
    }
  }

  try {
    await page.getByRole(role, { name: optionRegex }).first().check();
    return;
  } catch {
    try {
      await page.getByLabel(optionRegex).first().check();
      return;
    } catch {
      const coords = await findChoiceCoordinates(page, {
        question,
        option,
        role
      });
      if (!coords) {
        throw new Error(`Unable to find a responsive ${role} option for '${option}'.`);
      }

      await clickAtCoordinates(page, coords);
    }
  }
}

async function applyAnswerKey(page, step, logs) {
  const answers = Array.isArray(step.answers) ? step.answers.filter((answer) => answer?.option).slice(0, 20) : [];
  if (answers.length === 0) {
    throw new Error("apply_answer_key requires at least one answer.");
  }

  const pending = [...answers];
  const maxPages = Math.min(Math.max(pending.length + 1, 2), 12);
  let appliedCount = 0;

  for (let pageIndex = 0; pageIndex < maxPages && pending.length > 0; pageIndex += 1) {
    const result = await page.evaluate(({ answersOnPage }) => {
      const normalize = (value) => String(value || "").replace(/\s+/g, " ").trim().toLowerCase();
      const textMatches = (left, right) => {
        const a = normalize(left);
        const b = normalize(right);
        if (!a || !b) {
          return false;
        }

        return a.includes(b) || b.includes(a);
      };

      const isVisible = (element) => {
        if (!(element instanceof Element)) {
          return false;
        }

        const style = window.getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        return style.visibility !== "hidden" &&
          style.display !== "none" &&
          style.opacity !== "0" &&
          rect.width >= 4 &&
          rect.height >= 4;
      };

      const isClickable = (element) => {
        if (!(element instanceof Element)) {
          return false;
        }

        if (element.matches("button, a, label, input, [role='button'], [role='radio'], [role='checkbox'], [onclick]")) {
          return true;
        }

        const tabindex = element.getAttribute("tabindex");
        if (tabindex && tabindex !== "-1") {
          return true;
        }

        return window.getComputedStyle(element).cursor === "pointer";
      };

      const clickableAncestor = (element) =>
        element.closest("label, button, a, [role='button'], [role='radio'], [role='checkbox'], [onclick]") || element;

      const labelOf = (element) =>
        normalize(
          element.getAttribute?.("aria-label") ||
          element.getAttribute?.("title") ||
          element.getAttribute?.("placeholder") ||
          element.textContent ||
          ""
        );

      const contextText = (element) => {
        const parts = [];
        let current = element;
        let depth = 0;
        while (current && depth < 5) {
          const text = normalize(current.textContent || "");
          if (text) {
            parts.push(text);
          }

          current = current.parentElement;
          depth += 1;
        }

        return parts.join(" ");
      };

      const findCandidate = (questionText, optionText) => {
        const option = normalize(optionText);
        const question = normalize(questionText);
        if (!option) {
          return null;
        }

        const candidates = Array.from(document.querySelectorAll("input, label, button, a, [role], [onclick], [tabindex], div, li, span"))
          .filter(isVisible)
          .map((element) => {
            const target = clickableAncestor(element);
            if (!isVisible(target)) {
              return null;
            }

            const label = labelOf(target);
            const context = contextText(target);
            if (!textMatches(`${label} ${context}`, option)) {
              return null;
            }

            if (question && !textMatches(context, question) && !textMatches(label, question)) {
              return null;
            }

            const rect = target.getBoundingClientRect();
            let score = 0;
            if (textMatches(label, option)) {
              score += 35;
            }

            if (textMatches(context, option)) {
              score += 18;
            }

            if (question && textMatches(context, question)) {
              score += 20;
            }

            if (target.matches("input[type='radio'], [role='radio'], input[type='checkbox'], [role='checkbox']")) {
              score += 28;
            }

            if (isClickable(target)) {
              score += 10;
            }

            score -= Math.min((rect.width * rect.height) / 1500, 16);
            return { target, score };
          })
          .filter(Boolean)
          .sort((left, right) => right.score - left.score);

        return candidates[0]?.target || null;
      };

      const wasSelected = (element) => {
        if (!(element instanceof Element)) {
          return false;
        }

        if ("checked" in element && element.checked) {
          return true;
        }

        return normalize(element.getAttribute("aria-checked")) === "true";
      };

      const clickTarget = (element) => {
        const target = clickableAncestor(element);
        if (target instanceof HTMLElement) {
          target.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
          target.focus?.();
          target.click();
        }

        return target;
      };

      const applied = [];
      for (const answer of answersOnPage) {
        const candidate = findCandidate(answer.question, answer.option);
        if (!candidate) {
          continue;
        }

        const clicked = clickTarget(candidate);
        const key = `${normalize(answer.question)}|${normalize(answer.option)}`;
        if (wasSelected(clicked) || wasSelected(candidate) || true) {
          applied.push(key);
        }
      }

      const navCandidates = Array.from(document.querySelectorAll("button, a, [role='button'], [onclick], [tabindex], div, span"))
        .filter((element) => isVisible(element) && isClickable(element))
        .map((element) => {
          const label = labelOf(element);
          const rect = element.getBoundingClientRect();
          let score = 0;
          if (/(next|continue|submit|finish|done)/i.test(label)) {
            score += 35;
          }

          if (rect.left > window.innerWidth * 0.55) {
            score += 10;
          }

          if (rect.top > window.innerHeight * 0.45) {
            score += 10;
          }

          return { element, score };
        })
        .filter((entry) => entry.score > 0)
        .sort((left, right) => right.score - left.score);

      return {
        applied,
        nextAvailable: navCandidates.length > 0
      };
    }, {
      answersOnPage: pending
    });

    const appliedSet = new Set(Array.isArray(result?.applied) ? result.applied : []);
    if (appliedSet.size > 0) {
      for (let index = pending.length - 1; index >= 0; index -= 1) {
        const key = `${normalizeText(pending[index].question || "")}|${normalizeText(pending[index].option || "")}`;
        if (appliedSet.has(key)) {
          pending.splice(index, 1);
          appliedCount += 1;
        }
      }
    }

    logs.push(`apply_answer_key:applied=${appliedSet.size}`);

    if (pending.length === 0) {
      return;
    }

    if (!step.advancePages || !result?.nextAvailable) {
      break;
    }

    const advanced = await findNavigationCoordinates(page, ["Next", "Continue", "Go to next", "Done", "Submit"]);
    if (!advanced) {
      break;
    }

    await clickAtCoordinates(page, advanced);
    await page.waitForTimeout(900);
  }

  if (appliedCount === 0) {
    throw new Error("Could not match the answer key to responsive quiz options on the page.");
  }

  if (pending.length > 0) {
    throw new Error(`Applied ${appliedCount} answer(s), but ${pending.length} question(s) could not be matched yet.`);
  }
}

function containsMailBodyLabel(label) {
  return /message|body|compose/i.test(label);
}

function containsChatBodyLabel(label) {
  return /chat|comment|message|reply|thread|type a message|send a message/i.test(label);
}

function containsEditorSemanticLabel(label) {
  return containsMailBodyLabel(label) || containsChatBodyLabel(label);
}

function containsComposeLabel(label) {
  return /compose|new message|new mail/i.test(label);
}

function containsSendLabel(label) {
  return /^send$|send now|send email/i.test(label);
}

function containsScheduleLabel(label) {
  return /schedule|more send options|send later/i.test(label);
}

function expandRoleNames(role, name) {
  const normalizedRole = String(role || "").trim().toLowerCase();
  const normalizedName = String(name || "").trim();
  const values = normalizedName ? [normalizedName] : [];

  if (normalizedRole === "button") {
    if (containsComposeLabel(normalizedName)) {
      pushUnique(values, "Compose");
      pushUnique(values, "New message");
      pushUnique(values, "New mail");
      pushUnique(values, "Compose mail");
    } else if (containsSendLabel(normalizedName)) {
      pushUnique(values, "Send");
      pushUnique(values, "Send now");
      pushUnique(values, "Send email");
    } else if (containsScheduleLabel(normalizedName)) {
      pushUnique(values, "More send options");
      pushUnique(values, "Schedule send");
      pushUnique(values, "Send later");
    }
  }

  return values;
}

function expandFieldLabels(label) {
  const normalized = String(label || "").trim().toLowerCase();
  const values = label ? [label] : [];

  if (/\bto\b|recipient/.test(normalized)) {
    pushUnique(values, "To");
    pushUnique(values, "To recipients");
    pushUnique(values, "Recipients");
  } else if (normalized.includes("subject")) {
    pushUnique(values, "Subject");
    pushUnique(values, "Add a subject");
  } else if (containsMailBodyLabel(normalized)) {
    pushUnique(values, "Message Body");
    pushUnique(values, "Message body");
    pushUnique(values, "Message");
    pushUnique(values, "Compose email");
  }

  return values;
}

function expandEditorLabels(label) {
  const values = expandFieldLabels(label);
  const normalized = String(label || "").trim().toLowerCase();

  if (containsEditorSemanticLabel(normalized)) {
    pushUnique(values, "Reply");
    pushUnique(values, "Write a reply");
    pushUnique(values, "Type a message");
    pushUnique(values, "Send a message");
    pushUnique(values, "Chat");
  }

  return values;
}

async function writeIntoLocator(locator, text, { append = false } = {}) {
  await locator.waitFor({ state: "visible", timeout: 2500 }).catch(() => {});
  const applied = await locator.evaluate((element, payload) => {
    const normalize = (value) => String(value || "").replace(/\s+/g, " ").trim();
    const isContentEditable = (candidate) => candidate instanceof HTMLElement && candidate.isContentEditable;
    const isRichText = (candidate) => {
      if (!(candidate instanceof Element)) {
        return false;
      }

      const tagName = candidate.tagName.toLowerCase();
      if (tagName === "textarea" || tagName === "input" || tagName === "select") {
        return false;
      }

      return isContentEditable(candidate) || candidate.getAttribute("role") === "textbox";
    };
    const readValue = (candidate) => {
      if (!(candidate instanceof Element)) {
        return "";
      }

      if (isContentEditable(candidate)) {
        return normalize(candidate.textContent || candidate.innerText);
      }

      if ("value" in candidate) {
        return normalize(candidate.value);
      }

      return normalize(candidate.textContent || candidate.innerText);
    };
    const dispatch = (candidate, nextText, inputType) => {
      try {
        candidate.dispatchEvent(new InputEvent("beforeinput", {
          bubbles: true,
          cancelable: true,
          data: nextText,
          inputType
        }));
      } catch {
        // Ignore.
      }

      try {
        candidate.dispatchEvent(new InputEvent("input", {
          bubbles: true,
          data: nextText,
          inputType
        }));
      } catch {
        candidate.dispatchEvent(new Event("input", { bubbles: true }));
      }

      candidate.dispatchEvent(new Event("change", { bubbles: true }));
    };
    const setNativeValue = (candidate, nextValue) => {
      const prototype =
        candidate instanceof HTMLTextAreaElement
          ? HTMLTextAreaElement.prototype
          : candidate instanceof HTMLInputElement
            ? HTMLInputElement.prototype
            : candidate instanceof HTMLSelectElement
              ? HTMLSelectElement.prototype
              : null;

      const setter = prototype
        ? Object.getOwnPropertyDescriptor(prototype, "value")?.set
        : null;

      if (setter) {
        setter.call(candidate, nextValue);
        return;
      }

      candidate.value = nextValue;
    };
    const writePlainText = (candidate, nextText) => {
      while (candidate.firstChild) {
        candidate.removeChild(candidate.firstChild);
      }

      const lines = String(nextText || "").split(/\r?\n/);
      lines.forEach((line, index) => {
        if (index > 0) {
          candidate.appendChild(document.createElement("br"));
        }

        candidate.appendChild(document.createTextNode(line));
      });
    };

    if (!(element instanceof Element)) {
      return false;
    }

    const currentValue = readValue(element);
    const nextValue = payload.append ? `${currentValue}${payload.text}` : payload.text;
    element.focus?.();

    if (isRichText(element)) {
      let inserted = false;
      try {
        if (typeof document.execCommand === "function") {
          const selection = window.getSelection?.();
          if (selection) {
            const range = document.createRange();
            range.selectNodeContents(element);
            selection.removeAllRanges();
            selection.addRange(range);
          }

          inserted = document.execCommand("insertText", false, nextValue);
        }
      } catch {
        inserted = false;
      }

      if (!inserted) {
        writePlainText(element, nextValue);
      }

      dispatch(element, nextValue, payload.append ? "insertText" : "insertReplacementText");
      return readValue(element).includes(normalize(nextValue));
    }

    if ("value" in element) {
      setNativeValue(element, nextValue);
      dispatch(element, nextValue, payload.append ? "insertText" : "insertReplacementText");
      return readValue(element).includes(normalize(nextValue));
    }

    return false;
  }, {
    text,
    append
  });

  if (!applied) {
    throw new Error("The target editor did not accept the generated text.");
  }
}

async function clickByRole(page, step) {
  const role = step.role || "button";
  const names = expandRoleNames(role, step.name || step.text || "");

  for (const candidate of names) {
    try {
      await page.getByRole(role, { name: regexFromText(candidate) }).first().click();
      return;
    } catch {
      try {
        await page.getByText(regexFromText(candidate), { exact: false }).first().click();
        return;
      } catch {
        // Try next alias.
      }
    }
  }

  if (role === "button") {
    const coords = await findNavigationCoordinates(page, names);
    if (coords) {
      await clickAtCoordinates(page, coords);
      return;
    }
  }

  throw new Error(`Unable to find role '${role}' with name '${step.name || step.text || ""}'.`);
}

async function clickAtCoordinates(page, coords) {
  await page.mouse.move(coords.x, coords.y);
  await page.mouse.down();
  await page.mouse.up();
  await page.waitForTimeout(180);
}

async function findChoiceCoordinates(page, { question = "", option = "", role = "" }) {
  return await page.evaluate(({ questionText, optionText, roleName }) => {
    const normalize = (value) => String(value || "").replace(/\s+/g, " ").trim().toLowerCase();
    const option = normalize(optionText);
    const question = normalize(questionText);
    const type = normalize(roleName);

    if (!option) {
      return null;
    }

    const textMatches = (left, right) => {
      const a = normalize(left);
      const b = normalize(right);
      if (!a || !b) {
        return false;
      }

      return a.includes(b) || b.includes(a);
    };

    const isVisible = (element) => {
      if (!(element instanceof Element)) {
        return false;
      }

      const style = window.getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.visibility !== "hidden" &&
        style.display !== "none" &&
        style.opacity !== "0" &&
        rect.width >= 4 &&
        rect.height >= 4;
    };

    const isClickable = (element) => {
      if (!(element instanceof Element)) {
        return false;
      }

      if (element.matches("button, a, label, input, [role='button'], [role='radio'], [role='checkbox'], [onclick]")) {
        return true;
      }

      const tabindex = element.getAttribute("tabindex");
      if (tabindex && tabindex !== "-1") {
        return true;
      }

      return window.getComputedStyle(element).cursor === "pointer";
    };

    const clickableAncestor = (element) =>
      element.closest("label, button, a, [role='button'], [role='radio'], [role='checkbox'], [onclick]") || element;

    const labelOf = (element) =>
      normalize(
        element.getAttribute?.("aria-label") ||
        element.getAttribute?.("title") ||
        element.getAttribute?.("placeholder") ||
        element.textContent ||
        ""
      );

    const contextText = (element) => {
      const parts = [];
      let current = element;
      let depth = 0;
      while (current && depth < 4) {
        const text = normalize(current.textContent || "");
        if (text) {
          parts.push(text);
        }

        current = current.parentElement;
        depth += 1;
      }

      return parts.join(" ");
    };

    const scoreCandidate = (element) => {
      const target = clickableAncestor(element);
      if (!isVisible(target)) {
        return null;
      }

      const label = labelOf(target);
      const context = contextText(target);
      if (!textMatches(`${label} ${context}`, option)) {
        return null;
      }

      if (question && !textMatches(context, question) && !textMatches(label, question)) {
        return null;
      }

      const rect = target.getBoundingClientRect();
      let score = 0;

      if (textMatches(label, option)) {
        score += 35;
      }

      if (textMatches(context, option)) {
        score += 18;
      }

      if (question && textMatches(context, question)) {
        score += 20;
      }

      if (target.matches(`input[type='${type}'], [role='${type}']`)) {
        score += 30;
      }

      if (isClickable(target)) {
        score += 12;
      }

      score -= Math.min((rect.width * rect.height) / 1400, 16);

      return {
        x: rect.left + rect.width / 2,
        y: rect.top + rect.height / 2,
        score
      };
    };

    const selectors = [
      "input",
      "label",
      "button",
      "a",
      "[role]",
      "[onclick]",
      "[tabindex]",
      "div",
      "li",
      "span"
    ];

    const candidates = Array.from(document.querySelectorAll(selectors.join(",")))
      .map(scoreCandidate)
      .filter(Boolean)
      .sort((left, right) => right.score - left.score);

    return candidates[0] || null;
  }, {
    questionText: question,
    optionText: option,
    roleName: role
  });
}

async function findNavigationCoordinates(page, aliases) {
  return await page.evaluate((aliasList) => {
    const normalize = (value) => String(value || "").replace(/\s+/g, " ").trim().toLowerCase();
    const aliases = Array.isArray(aliasList) ? aliasList.map(normalize).filter(Boolean) : [];

    const textMatches = (left, right) => {
      const a = normalize(left);
      const b = normalize(right);
      if (!a || !b) {
        return false;
      }

      return a.includes(b) || b.includes(a);
    };

    const isVisible = (element) => {
      if (!(element instanceof Element)) {
        return false;
      }

      const style = window.getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.visibility !== "hidden" &&
        style.display !== "none" &&
        style.opacity !== "0" &&
        rect.width >= 4 &&
        rect.height >= 4;
    };

    const isClickable = (element) => {
      if (!(element instanceof Element)) {
        return false;
      }

      if (element.matches("button, a, [role='button'], [onclick]")) {
        return true;
      }

      const tabindex = element.getAttribute("tabindex");
      if (tabindex && tabindex !== "-1") {
        return true;
      }

      return window.getComputedStyle(element).cursor === "pointer";
    };

    const labelOf = (element) =>
      normalize(
        element.getAttribute?.("aria-label") ||
        element.getAttribute?.("title") ||
        element.textContent ||
        ""
      );

    const candidates = Array.from(document.querySelectorAll("button, a, [role='button'], [onclick], [tabindex], div, span"))
      .filter((element) => isVisible(element) && isClickable(element))
      .map((element) => {
        const rect = element.getBoundingClientRect();
        const label = labelOf(element);
        let score = 0;

        if (aliases.some((alias) => textMatches(label, alias))) {
          score += 45;
        }

        if (/(next|continue|submit|finish|done)/i.test(label)) {
          score += 30;
        }

        if (element.querySelector("svg, path")) {
          score += 10;
        }

        if (rect.left > window.innerWidth * 0.55) {
          score += 10;
        }

        if (rect.top > window.innerHeight * 0.45) {
          score += 10;
        }

        return {
          x: rect.left + rect.width / 2,
          y: rect.top + rect.height / 2,
          score
        };
      })
      .filter((candidate) => candidate.score > 0)
      .sort((left, right) => right.score - left.score);

    return candidates[0] || null;
  }, aliases);
}

function pushUnique(values, nextValue) {
  if (!values.some((value) => value.toLowerCase() === String(nextValue).toLowerCase())) {
    values.push(nextValue);
  }
}

function isSafeNavigationUrl(url) {
  return /^(https?:\/\/|mailto:)/i.test(String(url || "").trim());
}

function sameUrl(currentUrl, nextUrl) {
  const current = String(currentUrl || "").trim();
  const next = String(nextUrl || "").trim();
  if (!current || !next) {
    return false;
  }

  return current === next;
}
