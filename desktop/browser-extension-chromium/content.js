(function () {
  if (window.__cursivisBridgeLoaded) {
    return;
  }

  window.__cursivisBridgeLoaded = true;
  let lastFocusedEditable = null;
  const MAX_DOM_ANSWER_KEY_ENTRIES = 128;
  const CONTEXT_VISIBLE_TEXT_LIMIT = 10000;

  document.addEventListener("focusin", (event) => {
    const target = getEditableHost(event.target);
    if (target && isVisible(target)) {
      lastFocusedEditable = target;
    }
  }, true);

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    handleMessage(message)
      .then((result) => sendResponse(result))
      .catch((error) => {
        sendResponse({
          ok: false,
          error: error instanceof Error ? error.message : String(error)
        });
      });

    return true;
  });

  async function handleMessage(message) {
    switch (message?.type) {
      case "ping":
        return { ok: true };
      case "collect_context":
        return {
          ok: true,
          payload: collectContext()
        };
      case "execute_step":
        return await executeStep(message.step || {});
      default:
        return {
          ok: false,
          error: `Unsupported content-script message: ${message?.type || "unknown"}`
        };
    }
  }

  function collectContext() {
    return {
      url: window.location.href,
      title: document.title || "",
      visibleText: buildContextVisibleText(),
      interactiveElements: collectInteractiveElements()
    };
  }

  function buildContextVisibleText() {
    const parts = [];
    const seen = new Set();

    const pushPart = (value) => {
      const normalized = normalize(value);
      if (!normalized || seen.has(normalized)) {
        return;
      }

      seen.add(normalized);
      parts.push(normalized);
    };

    for (const value of collectPriorityEditorTexts()) {
      pushPart(value);
    }

    pushPart(document.body?.innerText || "");
    return parts.join(" ").slice(0, CONTEXT_VISIBLE_TEXT_LIMIT);
  }

  function collectPriorityEditorTexts() {
    const prioritized = [];
    const seenElements = new Set();

    const pushEditor = (element, priority = 0) => {
      const host = getEditableHost(element);
      if (!host || !isVisible(host) || !isEditable(host) || seenElements.has(host)) {
        return;
      }

      seenElements.add(host);
      const value = readFieldValue(host);
      if (!value || value.length < 24) {
        return;
      }

      const label = elementLabel(host);
      let score = priority;
      if (containsMailBodyLabel(label)) {
        score += 60;
      }
      if (isRichTextEditor(host)) {
        score += 20;
      }

      prioritized.push({
        score,
        value
      });
    };

    pushEditor(resolveFocusedEditableTarget(), 100);
    pushEditor(lastFocusedEditable, 90);

    for (const element of Array.from(document.querySelectorAll("textarea, input, [role='textbox'], [contenteditable='true']"))) {
      pushEditor(element);
    }

    return prioritized
      .sort((left, right) => right.score - left.score)
      .map((entry) => entry.value)
      .slice(0, 6);
  }

  function collectInteractiveElements() {
    const candidates = Array.from(document.querySelectorAll("button, a, input, textarea, select, [role], label, [contenteditable='true']"));
    const elements = [];

    for (const element of candidates) {
      if (!isVisible(element)) {
        continue;
      }

      const tagName = element.tagName.toLowerCase();
      const role = element.getAttribute("role") || (element.hasAttribute("contenteditable") ? "textbox" : tagName);
      const label = elementLabel(element);

      if (!label && !["input", "textarea", "select"].includes(tagName)) {
        continue;
      }

      const options = tagName === "select"
        ? Array.from(element.querySelectorAll("option")).map((option) => normalize(option.textContent)).filter(Boolean).slice(0, 10)
        : [];

      elements.push({
        role,
        label,
        nameAttribute: normalize(element.getAttribute("name")),
        type: normalize(element.getAttribute("type")) || tagName,
        options
      });

      if (elements.length >= 120) {
        break;
      }
    }

    return elements;
  }

  async function executeStep(step) {
    const normalized = normalizeStep(step);
    if (!normalized) {
      throw new Error("Invalid DOM action step.");
    }

    switch (normalized.tool) {
      case "click_role":
        clickElement(findByRole(normalized.role, normalized.name || normalized.text));
        return { ok: true };
      case "click_text":
        clickElement(findByText(normalized.text || normalized.name));
        return { ok: true };
      case "fill_label":
        await fillField(findFieldByLabel(normalized.label || normalized.name), normalized.text || "");
        return { ok: true };
      case "fill_name":
        await fillField(findFieldByName(normalized.nameAttribute || normalized.name), normalized.text || "");
        return { ok: true };
      case "fill_placeholder":
        await fillField(findFieldByPlaceholder(normalized.placeholder || normalized.label || normalized.name), normalized.text || "");
        return { ok: true };
      case "fill_editor":
        await fillEditor(normalized.label || normalized.name || "Message", normalized.text || "");
        return { ok: true };
      case "type_active":
        await typeIntoActiveElement(normalized.text || "");
        return { ok: true };
      case "select_option":
        await selectOption(normalized);
        return { ok: true };
      case "check_radio":
        await setChoice("radio", normalized);
        return { ok: true };
      case "check_checkbox":
        await setChoice("checkbox", normalized);
        return { ok: true };
      case "apply_answer_key":
        return {
          ok: true,
          payload: await applyAnswerKey(normalized)
        };
      case "press_key":
        pressKey(normalized.key || "Enter");
        return { ok: true };
      case "wait_for_text":
        await waitForText(normalized.text || normalized.name || "", 5000);
        return { ok: true };
      case "wait_ms":
        await delay(normalized.waitMs || 250);
        return { ok: true };
      case "scroll":
        scrollPage(normalized);
        return { ok: true };
      case "extract_dom":
        return {
          ok: true,
          payload: collectContext()
        };
      default:
        throw new Error(`Unsupported DOM tool: ${normalized.tool}`);
    }
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

    if (Number.isFinite(step.waitMs) && step.waitMs > 0) {
      normalized.waitMs = Math.min(10000, Math.round(step.waitMs));
    }

    if (Array.isArray(step.answers)) {
      const answers = step.answers
        .map((answer) => ({
          question: typeof answer?.question === "string" && answer.question.trim() ? answer.question.trim() : undefined,
          option: typeof answer?.option === "string" ? answer.option.trim() : "",
          questionIndex: Number.isInteger(answer?.questionIndex) && answer.questionIndex > 0
            ? answer.questionIndex
            : undefined,
          choiceIndex: Number.isInteger(answer?.choiceIndex) && answer.choiceIndex >= 0
            ? answer.choiceIndex
            : undefined
        }))
        .filter((answer) => answer.option)
        .slice(0, MAX_DOM_ANSWER_KEY_ENTRIES);

      if (answers.length > 0) {
        normalized.answers = answers;
      }
    }

    if (typeof step.advancePages === "boolean") {
      normalized.advancePages = step.advancePages;
    }

    return normalized;
  }

  function findByRole(role, name) {
    const tagMatches = roleToTagList(role);
    const candidates = Array.from(document.querySelectorAll(tagMatches.join(","))).filter(isVisible);
    const aliases = expandRoleNames(role, name);
    const match = candidates.find((element) => aliases.some((value) => textMatches(elementLabel(element), value)));
    if (!match && normalize(role) === "button" && aliases.some((value) => /next|continue|submit|finish|done/i.test(value))) {
      const fallback = findLikelyNavigationElement(aliases);
      if (fallback) {
        return fallback;
      }
    }

    if (!match) {
      throw new Error(`Could not find ${role || "element"} '${name || ""}'.`);
    }

    return match;
  }

  function findByText(text) {
    const query = String(text || "").trim();
    if (!query) {
      throw new Error("click_text requires text.");
    }

    const candidates = Array.from(document.querySelectorAll("button, a, span, div, label, [role], [contenteditable='true']")).filter(isVisible);
    const match = candidates.find((element) => textMatches(elementLabel(element), query));
    if (!match) {
      throw new Error(`Could not find visible text '${query}'.`);
    }

    return match;
  }

  function findFieldByLabel(label) {
    const queries = expandFieldQueries(label);
    for (const query of queries) {
      if (containsEditorSemanticLabel(query)) {
        const editorMatch = findEditorTarget(query);
        if (editorMatch) {
          return editorMatch;
        }
      }

      const byDirectLabel = findFieldUsingLabelTag(query);
      if (byDirectLabel) {
        return byDirectLabel;
      }

      const candidates = getFieldCandidates();
      const match = candidates.find((element) => {
        const combined = `${elementLabel(element)} ${closestContainerText(element)}`;
        return textMatches(combined, query);
      });

      if (match) {
        return match;
      }
    }

    if (containsEditorSemanticLabel(label)) {
      const editorFallback = findEditorTarget(label);
      if (editorFallback) {
        return editorFallback;
      }
    }

    throw new Error(`Could not find field '${label || ""}'.`);
  }

  function findFieldUsingLabelTag(label) {
    const labelElements = Array.from(document.querySelectorAll("label")).filter(isVisible);
    for (const labelElement of labelElements) {
      if (!textMatches(normalize(labelElement.innerText || labelElement.textContent), label)) {
        continue;
      }

      const forId = labelElement.getAttribute("for");
      if (forId) {
        const field = document.getElementById(forId);
        if (field && isEditable(field)) {
          return field;
        }
      }

      const nestedField = labelElement.querySelector("input, textarea, select, [contenteditable='true']");
      if (nestedField && isEditable(nestedField)) {
        return nestedField;
      }
    }

    return null;
  }

  function findFieldByName(name) {
    const query = String(name || "").trim();
    if (!query) {
      throw new Error("Field name is required.");
    }

    const field = document.querySelector(`[name="${cssEscape(query)}"]`);
    if (!field || !isEditable(field)) {
      throw new Error(`Could not find field named '${query}'.`);
    }

    return field;
  }

  function findFieldByPlaceholder(placeholder) {
    const query = String(placeholder || "").trim();
    if (!query) {
      throw new Error("Field placeholder is required.");
    }

    const candidates = getFieldCandidates();
    const match = candidates.find((element) => textMatches(element.getAttribute("placeholder"), query) || textMatches(elementLabel(element), query));
    if (!match) {
      throw new Error(`Could not find field placeholder '${query}'.`);
    }

    return match;
  }

  function getFieldCandidates() {
    const seen = new Set();
    const candidates = [];
    for (const element of document.querySelectorAll("input, textarea, select, [contenteditable='true'], [role='textbox'], [aria-multiline='true']")) {
      const target = getEditableHost(element);
      if (!target || !isVisible(target) || !isEditable(target) || seen.has(target)) {
        continue;
      }

      seen.add(target);
      candidates.push(target);
    }

    return candidates;
  }

  async function fillField(element, text, options = {}) {
    const target = getEditableHost(element);
    if (!target) {
      throw new Error("The matched element is not fillable.");
    }

    const nextText = String(text || "");
    if (!nextText && !options.allowEmpty) {
      return;
    }

    if (target.tagName.toLowerCase() === "select") {
      throw new Error("Use select_option for dropdown controls.");
    }

    await focusEditableTarget(target);

    if (!options.append && isFieldValueApplied(target, nextText)) {
      return;
    }

    if (isRichTextEditor(target) || options.forceEditor) {
      await setRichTextValue(target, nextText, options);
      return;
    }

    if ("value" in target) {
      await setFormControlValue(target, nextText, options);
      return;
    }

    throw new Error("The matched element is not fillable.");
  }

  async function fillEditor(label, text) {
    if (editorContentAlreadyVisible(text)) {
      return;
    }

    let target = await waitForEditorTarget(label, 1600);
    if (!target) {
      const opened = await openLikelyEditorSurface(label);
      if (opened) {
        target = await waitForEditorTarget(label, 1800);
      }
    }

    if (!target) {
      if (editorContentAlreadyVisible(text)) {
        return;
      }

      throw new Error(`Could not find an editor for '${label || "message"}'.`);
    }

    await fillField(target, text, { forceEditor: true });
  }

  async function waitForEditorTarget(label, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const match = findEditorTarget(label);
      if (match) {
        return match;
      }

      await delay(120);
    }

    return null;
  }

  async function typeIntoActiveElement(text) {
    const activeElement = resolveFocusedEditableTarget();
    if (!activeElement) {
      throw new Error("No editable active element is focused.");
    }

    await fillField(activeElement, text, { append: true, forceEditor: isRichTextEditor(activeElement) });
  }

  async function selectOption(step) {
    const optionText = String(step.option || step.text || "").trim();
    if (!optionText) {
      throw new Error("select_option requires option text.");
    }

    const field = step.label || step.name
      ? findFieldByLabel(step.label || step.name)
      : getFieldCandidates().find((element) => element.tagName.toLowerCase() === "select");

    if (!field) {
      throw new Error("Could not find a select field.");
    }

    if (field.tagName.toLowerCase() === "select") {
      const option = Array.from(field.options).find((item) => textMatches(item.textContent, optionText));
      if (!option) {
        throw new Error(`Could not find option '${optionText}'.`);
      }

      setNativeValue(field, option.value);
      dispatchInputEvents(field, optionText, "insertReplacementText");
      await delay(45);
      if (normalize(field.value) !== normalize(option.value)) {
        throw new Error(`The select option '${optionText}' did not stay selected.`);
      }
      return;
    }

    clickElement(field);
    await delay(60);
    const optionElement = findByText(optionText);
    clickElement(optionElement);
  }

  async function setChoice(type, step) {
    const optionText = String(step.option || step.label || step.name || "").trim();
    if (!optionText) {
      throw new Error(`${type} option is required.`);
    }

    const selector = type === "radio"
      ? "input[type='radio'], [role='radio']"
      : "input[type='checkbox'], [role='checkbox']";

    const candidates = Array.from(document.querySelectorAll(selector)).filter(isVisible);
    const questionText = normalize(step.question);
    let match = candidates.find((element) => {
      const combined = `${elementLabel(element)} ${closestContainerText(element)}`;
      return textMatches(combined, optionText) &&
        (!questionText || textMatches(combined, questionText) || textMatches(closestContainerText(element), questionText));
    });

    if (!match) {
      const labels = Array.from(document.querySelectorAll("label")).filter(isVisible);
      const labelMatch = labels.find((label) => {
        const combined = `${normalize(label.innerText || label.textContent)} ${closestContainerText(label)}`;
        return textMatches(combined, optionText) &&
          (!questionText || textMatches(combined, questionText) || textMatches(closestContainerText(label), questionText));
      });

      if (labelMatch) {
        const forId = labelMatch.getAttribute("for");
        if (forId) {
          const input = document.getElementById(forId);
          if (input) {
            match = input;
          }
        } else {
          match = labelMatch.querySelector(selector);
        }
      }
    }

    if (!match) {
      match = findChoiceLikeElement(optionText, questionText, type);
    }

    if (!match) {
      throw new Error(`Could not find ${type} option '${optionText}'.`);
    }

    await ensureChoiceApplied(match, type, optionText);
    if (!isChoiceSelected(match)) {
      throw new Error(`The ${type} option '${optionText}' did not stay selected.`);
    }
  }

  function getEditableHost(node) {
    if (node instanceof Element && isEditable(node)) {
      return node;
    }

    const element = node instanceof Element ? node : node?.parentElement;
    if (!(element instanceof Element)) {
      return null;
    }

    return element.closest("input, textarea, select, [contenteditable='true'], [role='textbox'], [aria-multiline='true']");
  }

  function resolveFocusedEditableTarget() {
    const selection = typeof window.getSelection === "function" ? window.getSelection() : null;
    const fromSelection = getEditableHost(selection?.anchorNode);
    if (fromSelection && isVisible(fromSelection)) {
      return fromSelection;
    }

    const active = getEditableHost(document.activeElement);
    if (active && isVisible(active)) {
      return active;
    }

    if (lastFocusedEditable && isVisible(lastFocusedEditable)) {
      return lastFocusedEditable;
    }

    return null;
  }

  function getEditorCandidates() {
    return getFieldCandidates().filter((element) => {
      const tagName = element.tagName.toLowerCase();
      if (tagName === "select") {
        return false;
      }

      const type = normalize(element.getAttribute?.("type"));
      return tagName !== "input" || !["radio", "checkbox", "button", "submit", "reset", "file", "hidden"].includes(type);
    });
  }

  function findEditorTarget(label) {
    const queries = expandFieldQueries(label || "Message");
    const siteSpecific = findSiteSpecificEditorTarget(label);
    if (siteSpecific) {
      return siteSpecific;
    }

    const focused = resolveFocusedEditableTarget();
    const candidates = getEditorCandidates();
    const ranked = candidates
      .map((element) => ({
        element,
        score: scoreEditorCandidate(element, queries, focused)
      }))
      .filter((entry) => entry.score > 0)
      .sort((left, right) => right.score - left.score);

    if (ranked.length > 0) {
      return ranked[0].element;
    }

    return focused;
  }

  function scoreEditorCandidate(element, queries, focusedElement) {
    const tagName = element.tagName.toLowerCase();
    const label = elementLabel(element);
    const context = closestContainerText(element);
    const placeholder = normalize(
      element.getAttribute("placeholder") ||
      element.getAttribute("aria-placeholder") ||
      element.getAttribute("data-placeholder")
    );
    const searchable = `${label} ${placeholder} ${context}`;
    const expectsSemanticEditor = queries.some((query) => containsEditorSemanticLabel(query));
    let score = 0;

    for (const query of queries) {
      if (!query) {
        continue;
      }

      if (textMatches(label, query)) {
        score += 55;
      }

      if (textMatches(placeholder, query)) {
        score += 36;
      }

      if (textMatches(context, query)) {
        score += 24;
      }
    }

    if (focusedElement && element === focusedElement) {
      score += 44;
    }

    if (isRichTextEditor(element)) {
      score += 16;
    }

    if (tagName === "textarea") {
      score += 12;
    }

    if (containsEditorSemanticLabel(searchable)) {
      score += 18;
    } else if (expectsSemanticEditor) {
      score -= 28;
    }

    if (containsSearchLikeLabel(searchable)) {
      score -= 22;
    }

    if (normalize(element.getAttribute("aria-multiline")) === "true") {
      score += 8;
    }

    return score;
  }

  async function focusEditableTarget(element) {
    focusElement(element);
    element.scrollIntoView({ block: "center", inline: "center", behavior: "auto" });
    await delay(35);
  }

  function isRichTextEditor(element) {
    if (!(element instanceof Element)) {
      return false;
    }

    const tagName = element.tagName.toLowerCase();
    if (tagName === "textarea" || tagName === "input" || tagName === "select") {
      return false;
    }

    return isContentEditable(element) || element.getAttribute("role") === "textbox";
  }

  async function setFormControlValue(element, text, options = {}) {
    const currentValue = "value" in element ? String(element.value || "") : "";
    const nextValue = options.append ? `${currentValue}${text}` : text;

    if (typeof element.select === "function" && !options.append) {
      try {
        element.select();
      } catch {
        // Ignore controls that do not support selection.
      }
    }

    setNativeValue(element, nextValue);
    dispatchInputEvents(element, nextValue, options.append ? "insertText" : "insertReplacementText");
    await delay(45);

    if (!isFieldValueApplied(element, nextValue)) {
      setNativeValue(element, nextValue);
      dispatchInputEvents(element, nextValue, options.append ? "insertText" : "insertReplacementText");
      await delay(90);
    }

    verifyTextValue(element, nextValue);
  }

  async function setRichTextValue(element, text, options = {}) {
    const currentText = options.append ? readFieldValue(element) : "";
    const nextValue = options.append ? `${currentText}${text}` : text;
    let applied = false;

    if (isDiscordPage() && !options.append) {
      writePlainTextContent(element, nextValue);
      applied = true;
    } else {
      try {
        if (typeof document.execCommand === "function") {
          selectEditableContents(element);
          applied = document.execCommand("insertText", false, nextValue);
        }
      } catch {
        applied = false;
      }
    }

    if (!applied) {
      writePlainTextContent(element, nextValue);
    }

    dispatchInputEvents(element, nextValue, options.append ? "insertText" : "insertReplacementText");
    await delay(65);

    if (!options.append && isDuplicatedText(readFieldValue(element), nextValue)) {
      writePlainTextContent(element, nextValue);
      dispatchInputEvents(element, nextValue, "insertReplacementText");
      await delay(70);
    }

    if (!isFieldValueApplied(element, nextValue)) {
      writePlainTextContent(element, nextValue);
      dispatchInputEvents(element, nextValue, options.append ? "insertText" : "insertReplacementText");
      await delay(110);
    }

    verifyTextValue(element, nextValue);
  }

  function selectEditableContents(element) {
    if (!(element instanceof Element)) {
      return;
    }

    focusElement(element);
    const selection = window.getSelection?.();
    if (!selection) {
      return;
    }

    const range = document.createRange();
    range.selectNodeContents(element);
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function writePlainTextContent(element, text) {
    while (element.firstChild) {
      element.removeChild(element.firstChild);
    }

    const lines = String(text || "").split(/\r?\n/);
    lines.forEach((line, index) => {
      if (index > 0) {
        element.appendChild(document.createElement("br"));
      }

      element.appendChild(document.createTextNode(line));
    });
  }

  async function ensureChoiceApplied(element, type, optionText) {
    const verificationTarget = resolveChoiceVerificationTarget(element);
    if (!verificationTarget) {
      throw new Error(`Could not verify ${type} option '${optionText}'.`);
    }

    if (isChoiceSelected(verificationTarget) || isChoiceSelected(element)) {
      return;
    }

    for (let attempt = 0; attempt < 3; attempt += 1) {
      focusElement(verificationTarget);
      clickElement(element);
      await delay(40 + (attempt * 30));
      if (isChoiceSelected(verificationTarget) || isChoiceSelected(element)) {
        return;
      }

      if ("checked" in verificationTarget) {
        setNativeCheckedValue(verificationTarget, true);
        dispatchInputEvents(verificationTarget, optionText, "insertReplacementText");
      } else {
        verificationTarget.setAttribute("aria-checked", "true");
        verificationTarget.dispatchEvent(new Event("input", { bubbles: true }));
        verificationTarget.dispatchEvent(new Event("change", { bubbles: true }));
      }

      await delay(45 + (attempt * 35));
      if (isChoiceSelected(verificationTarget) || isChoiceSelected(element)) {
        return;
      }
    }
  }

  function clickElement(element) {
    focusElement(element);
    element.scrollIntoView({ block: "center", inline: "center", behavior: "auto" });
    const target = element.closest("label") && element.tagName.toLowerCase() === "input"
      ? element.closest("label")
      : element.closest("button, a, label, [role='button'], [role='radio'], [role='checkbox']") || element;

    if (typeof target.click === "function") {
      target.click();
      return;
    }

    target.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
  }

  function resolveChoiceVerificationTarget(element) {
    if (!(element instanceof Element)) {
      return null;
    }

    if (element.matches("input[type='radio'], input[type='checkbox'], [role='radio'], [role='checkbox']")) {
      return element;
    }

    const descendant = element.querySelector("input[type='radio'], input[type='checkbox'], [role='radio'], [role='checkbox']");
    if (descendant) {
      return descendant;
    }

    const enclosingLabel = element.closest("label");
    if (enclosingLabel) {
      const labelledInput = enclosingLabel.querySelector("input[type='radio'], input[type='checkbox']");
      if (labelledInput) {
        return labelledInput;
      }
    }

    return element.closest("[role='radio'], [role='checkbox']") || element;
  }

  function isChoiceSelected(element) {
    const target = resolveChoiceVerificationTarget(element);
    if (!(target instanceof Element)) {
      return false;
    }

    if ("checked" in target && typeof target.checked === "boolean") {
      return target.checked;
    }

    const ariaChecked = normalize(target.getAttribute("aria-checked"));
    if (ariaChecked === "true") {
      return true;
    }

    if (normalize(target.getAttribute("aria-selected")) === "true") {
      return true;
    }

    return Boolean(
      target.querySelector("input[type='radio']:checked, input[type='checkbox']:checked, [role='radio'][aria-checked='true'], [role='checkbox'][aria-checked='true']")
    );
  }

  function readFieldValue(element) {
    if (!(element instanceof Element)) {
      return "";
    }

    if (isContentEditable(element)) {
      return normalize(element.textContent || element.innerText);
    }

    if ("value" in element) {
      return normalize(element.value);
    }

    return normalize(element.textContent || element.innerText);
  }

  function isFieldValueApplied(element, expectedText) {
    const actualValue = readFieldValue(element);
    const expectedValue = normalize(expectedText);
    if (!actualValue || !expectedValue) {
      return false;
    }

    return actualValue === expectedValue || actualValue.includes(expectedValue) || expectedValue.includes(actualValue);
  }

  async function tryApplyAnswerEntry(answer) {
    const questionText = normalize(answer.question);

    for (let attempt = 0; attempt < 3; attempt += 1) {
      const match = findChoiceLikeElement(answer.option, questionText, "radio") ||
        findChoiceLikeElement(answer.option, questionText, "checkbox");
      if (match) {
        await ensureChoiceApplied(match, "choice", answer.option);
        if (isChoiceSelected(match)) {
          return true;
        }
      }

      const textField = findTextResponseField(questionText, answer.option);
      if (textField) {
        await fillField(textField, answer.option, { forceEditor: isRichTextEditor(textField) });
        await delay(75 + (attempt * 35));
        if (isFieldValueApplied(textField, answer.option)) {
          return true;
        }
      }

      await delay(35);
    }

    return false;
  }

  function pressKey(key) {
    const activeElement = document.activeElement || document.body;
    const normalized = String(key || "Enter").trim();
    const event = new KeyboardEvent("keydown", {
      key: normalized.includes("+") ? normalized.split("+").at(-1) : normalized,
      ctrlKey: normalized.toLowerCase().includes("control+") || normalized.toLowerCase().includes("ctrl+"),
      bubbles: true,
      cancelable: true
    });

    activeElement.dispatchEvent(event);
    if (normalized.toLowerCase() === "enter" && typeof activeElement.click === "function" && activeElement.matches("button, [role='button']")) {
      activeElement.click();
    }
  }

  async function waitForText(text, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    const query = normalize(text);
    while (Date.now() < deadline) {
      if (normalize(document.body?.innerText || "").includes(query)) {
        return;
      }

      await delay(140);
    }

    throw new Error(`Timed out waiting for text '${text}'.`);
  }

  function scrollPage(step) {
    const mode = normalize(step.text || step.name || "down");
    if (mode.includes("top")) {
      window.scrollTo({ top: 0, behavior: "smooth" });
      return;
    }

    if (mode.includes("bottom")) {
      window.scrollTo({ top: document.body.scrollHeight, behavior: "smooth" });
      return;
    }

    const delta = mode.includes("up") ? -window.innerHeight * 0.8 : window.innerHeight * 0.8;
    window.scrollBy({ top: delta, behavior: "smooth" });
  }

  function roleToTagList(role) {
    switch (normalize(role)) {
      case "button":
        return ["button", "input[type='button']", "input[type='submit']", "[role='button']", "[aria-label]"];
      case "link":
        return ["a", "[role='link']"];
      case "textbox":
        return ["input", "textarea", "[role='textbox']", "[contenteditable='true']"];
      case "radio":
        return ["input[type='radio']", "[role='radio']"];
      case "checkbox":
        return ["input[type='checkbox']", "[role='checkbox']"];
      case "combobox":
        return ["select", "[role='combobox']"];
      default:
        return ["button", "a", "input", "textarea", "select", "[role]", "[contenteditable='true']"];
    }
  }

  function expandRoleNames(role, name) {
    const values = [];
    if (name) {
      values.push(name);
    }

    const normalizedName = normalize(name);
    if (normalize(role) === "button") {
    if (normalizedName.includes("compose")) {
      pushUnique(values, "Compose");
      pushUnique(values, "New message");
      pushUnique(values, "New mail");
    } else if (normalizedName.includes("reply")) {
      pushUnique(values, "Reply");
      pushUnique(values, "Reply all");
      pushUnique(values, "Send reply");
    } else if (normalizedName.includes("next") || normalizedName.includes("continue")) {
      pushUnique(values, "Next");
      pushUnique(values, "Continue");
        pushUnique(values, "Next question");
        pushUnique(values, "Go to next");
      } else if (normalizedName === "send" || normalizedName.includes("send")) {
        pushUnique(values, "Send");
        pushUnique(values, "Send now");
        pushUnique(values, "Schedule send");
      } else if (normalizedName.includes("submit")) {
        pushUnique(values, "Submit");
        pushUnique(values, "Finish");
        pushUnique(values, "Done");
      } else if (normalizedName.includes("schedule")) {
        pushUnique(values, "Schedule send");
        pushUnique(values, "More send options");
      }
    }

    return values.length > 0 ? values : [""];
  }

  function expandFieldQueries(label) {
    const values = [];
    if (label) {
      values.push(label);
    }

    const normalized = normalize(label);
    if (normalized.includes("to")) {
      pushUnique(values, "To");
      pushUnique(values, "Recipients");
      pushUnique(values, "To recipients");
    } else if (normalized.includes("subject")) {
      pushUnique(values, "Subject");
      pushUnique(values, "Add a subject");
    } else if (containsEditorSemanticLabel(normalized)) {
      pushUnique(values, "Message Body");
      pushUnique(values, "Message");
      pushUnique(values, "Compose email");
      pushUnique(values, "Reply");
      pushUnique(values, "Write a reply");
      pushUnique(values, "Type a message");
      pushUnique(values, "Send a message");
      pushUnique(values, "Chat");
    }

    return values;
  }

  function closestContainerText(element) {
    let current = element;
    let depth = 0;
    const parts = [];
    while (current && depth < 4) {
      const text = normalize(current.innerText || current.textContent);
      if (text) {
        parts.push(text);
      }
      current = current.parentElement;
      depth += 1;
    }

    return parts.join(" ");
  }

  function elementLabel(element) {
    if (!element) {
      return "";
    }

    return normalize(
      element.getAttribute?.("aria-label") ||
      element.getAttribute?.("aria-placeholder") ||
      element.getAttribute?.("data-placeholder") ||
      element.getAttribute?.("title") ||
      element.getAttribute?.("placeholder") ||
      element.getAttribute?.("name") ||
      element.innerText ||
      element.textContent ||
      element.value ||
      ""
    );
  }

  function textMatches(candidate, query) {
    const normalizedCandidate = normalize(candidate);
    const normalizedQuery = normalize(query);
    if (!normalizedCandidate || !normalizedQuery) {
      return false;
    }

    return normalizedCandidate.includes(normalizedQuery) || normalizedQuery.includes(normalizedCandidate);
  }

  function normalize(value) {
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function normalizeQuestionText(value) {
    return normalize(value)
      .replace(/^\d+\s*[\).:-]?\s*/, "")
      .replace(/\s*\*+\s*$/, "")
      .trim();
  }

  function normalizeOptionText(value) {
    return normalize(value)
      .replace(/^[a-z0-9]+\s*[\).:-]\s*/i, "")
      .trim();
  }

  function isGoogleFormsPage() {
    return /docs\.google\.com\/forms|forms\.gle/i.test(window.location.href);
  }

  function isVisible(element) {
    if (!(element instanceof Element)) {
      return false;
    }

    const rect = element.getBoundingClientRect();
    if (rect.width < 2 || rect.height < 2) {
      return false;
    }

    const style = window.getComputedStyle(element);
    return style.visibility !== "hidden" && style.display !== "none" && style.opacity !== "0";
  }

  function isLikelyClickable(element) {
    if (!(element instanceof Element)) {
      return false;
    }

    if (element.matches("button, a, input, label, [role='button'], [role='radio'], [role='checkbox'], [onclick]")) {
      return true;
    }

    const tabindex = element.getAttribute("tabindex");
    if (tabindex && tabindex !== "-1") {
      return true;
    }

    return window.getComputedStyle(element).cursor === "pointer";
  }

  function findChoiceLikeElement(optionText, questionText, type) {
    const candidates = Array.from(
      document.querySelectorAll("label, button, [role], [tabindex], [onclick], div, li, span")
    ).filter((element) => {
      if (!isVisible(element)) {
        return false;
      }

      const label = elementLabel(element);
      const context = closestContainerText(element);
      if (!textMatches(`${label} ${context}`, optionText)) {
        return false;
      }

      if (questionText && !textMatches(context, questionText) && !textMatches(label, questionText)) {
        return false;
      }

      return isLikelyClickable(element) || textMatches(label, optionText);
    });

    return candidates
      .sort((left, right) => scoreChoiceCandidate(right, optionText, questionText, type) - scoreChoiceCandidate(left, optionText, questionText, type))
      .at(0) || null;
  }

  function scoreChoiceCandidate(element, optionText, questionText, type) {
    const label = elementLabel(element);
    const context = closestContainerText(element);
    const rect = element.getBoundingClientRect();
    let score = 0;

    if (textMatches(label, optionText)) {
      score += 25;
    }

    if (textMatches(context, optionText)) {
      score += 12;
    }

    if (questionText && textMatches(context, questionText)) {
      score += 18;
    }

    if (element.matches(`input[type='${type}'], [role='${type}']`)) {
      score += 30;
    }

    if (isLikelyClickable(element)) {
      score += 10;
    }

    score -= Math.min((rect.width * rect.height) / 1200, 18);
    return score;
  }

  function findLikelyNavigationElement(aliases) {
    const candidates = Array.from(
      document.querySelectorAll("button, [role='button'], div, span")
    ).filter((element) => isVisible(element) && isLikelyClickable(element));

    const direct = candidates.find((element) => aliases.some((alias) => textMatches(elementLabel(element), alias) || textMatches(closestContainerText(element), alias)));
    if (direct) {
      return direct;
    }

    const ranked = candidates
      .map((element) => ({ element, score: scoreNavigationCandidate(element) }))
      .filter((entry) => entry.score > 0)
      .sort((left, right) => right.score - left.score);

    return ranked[0]?.element || null;
  }

  function scoreNavigationCandidate(element) {
    const rect = element.getBoundingClientRect();
    const label = `${elementLabel(element)} ${closestContainerText(element)}`.toLowerCase();
    let score = 0;

    if (/(next|continue|submit|finish|done)/.test(label)) {
      score += 50;
    }

    if (/(arrow|chevron|next)/.test((element.className || "").toString().toLowerCase())) {
      score += 20;
    }

    if (element.querySelector("svg, path")) {
      score += 10;
    }

    if (rect.left > window.innerWidth * 0.55) {
      score += 12;
    }

    if (rect.top > window.innerHeight * 0.45) {
      score += 12;
    }

    return score;
  }

  function isEditable(element) {
    if (!(element instanceof Element)) {
      return false;
    }

    const tagName = element.tagName.toLowerCase();
    const type = normalize(element.getAttribute("type"));
    if (tagName === "input" && ["button", "submit", "reset", "file", "hidden"].includes(type)) {
      return false;
    }

    return tagName === "input" ||
      tagName === "textarea" ||
      tagName === "select" ||
      isContentEditable(element) ||
      element.getAttribute("role") === "textbox";
  }

  function isContentEditable(element) {
    return element instanceof HTMLElement && element.isContentEditable;
  }

  function focusElement(element) {
    if (typeof element.focus === "function") {
      element.focus({ preventScroll: false });
    }
  }

  function verifyTextValue(element, text) {
    if (!isFieldValueApplied(element, text)) {
      throw new Error("Field value did not update as expected.");
    }
  }

  function dispatchInputEvents(element, text = "", inputType = "insertText") {
    try {
      element.dispatchEvent(new InputEvent("beforeinput", {
        bubbles: true,
        cancelable: true,
        data: text,
        inputType
      }));
    } catch {
      // Older pages may reject InputEvent construction.
    }

    try {
      element.dispatchEvent(new InputEvent("input", {
        bubbles: true,
        data: text,
        inputType
      }));
    } catch {
      element.dispatchEvent(new Event("input", { bubbles: true }));
    }

    element.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function setNativeValue(element, value) {
    const prototype =
      element instanceof HTMLTextAreaElement
        ? HTMLTextAreaElement.prototype
        : element instanceof HTMLInputElement
          ? HTMLInputElement.prototype
          : element instanceof HTMLSelectElement
            ? HTMLSelectElement.prototype
            : null;

    const setter = prototype
      ? Object.getOwnPropertyDescriptor(prototype, "value")?.set
      : null;

    if (setter) {
      setter.call(element, value);
      return;
    }

    element.value = value;
  }

  function setNativeCheckedValue(element, checked) {
    if (element instanceof HTMLInputElement) {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "checked")?.set;
      if (setter) {
        setter.call(element, checked);
        return;
      }
    }

    if ("checked" in element) {
      element.checked = checked;
    }
  }

  function containsMailBodyLabel(label) {
    return /message|body|compose|reply/i.test(label);
  }

  function containsChatBodyLabel(label) {
    return /chat|comment|message|reply|thread|type a message|send a message/i.test(label);
  }

  function containsEditorSemanticLabel(label) {
    return containsMailBodyLabel(label) || containsChatBodyLabel(label);
  }

  function containsSearchLikeLabel(label) {
    return /search|filter|find in page|look up/.test(label);
  }

  function isGmailPage() {
    return /mail\.google\.com/i.test(window.location.href);
  }

  function isDiscordPage() {
    return /discord\.com/i.test(window.location.href);
  }

  function findFirstVisibleEditable(selectors) {
    for (const selector of selectors) {
      const match = Array.from(document.querySelectorAll(selector))
        .map((element) => getEditableHost(element))
        .find((element) => element && isVisible(element) && isEditable(element));
      if (match) {
        return match;
      }
    }

    return null;
  }

  function findSiteSpecificEditorTarget(label) {
    const normalizedLabel = normalize(label || "Message");
    if (isGmailPage() && containsMailBodyLabel(normalizedLabel)) {
      return findFirstVisibleEditable([
        "div[role='textbox'][aria-label*='Message Body']",
        "div[contenteditable='true'][aria-label*='Message Body']",
        "div[role='textbox'][aria-label*='Write a reply']",
        "div[contenteditable='true'][aria-label*='Write a reply']",
        "div[role='textbox'][aria-label*='Reply']",
        "div[contenteditable='true'][aria-label*='Reply']"
      ]);
    }

    if (isDiscordPage() && containsChatBodyLabel(normalizedLabel)) {
      return findFirstVisibleEditable([
        "div[role='textbox'][data-slate-editor='true']",
        "[data-slate-editor='true'][contenteditable='true']",
        "div[role='textbox'][contenteditable='true']"
      ]);
    }

    return null;
  }

  async function openLikelyEditorSurface(label) {
    const normalizedLabel = normalize(label || "Message");
    const queries = expandFieldQueries(label || "Message")
      .map((value) => normalize(value))
      .filter(Boolean);
    if (queries.length === 0) {
      return false;
    }

    const siteSpecificOpened = await openSiteSpecificEditorSurface(label);
    if (siteSpecificOpened) {
      return true;
    }

    if ((isGmailPage() && containsMailBodyLabel(normalizedLabel)) || (isDiscordPage() && containsChatBodyLabel(normalizedLabel))) {
      return false;
    }

    const candidates = Array.from(
      document.querySelectorAll("button, a, [role='button'], [tabindex], [onclick], div, span")
    ).filter((element) => isVisible(element) && isLikelyClickable(element));

    const ranked = candidates
      .map((element) => ({
        element,
        score: scoreEditorLaunchCandidate(element, queries)
      }))
      .filter((entry) => entry.score > 0)
      .sort((left, right) => right.score - left.score);

    const target = ranked[0]?.element;
    if (!target) {
      return false;
    }

    clickElement(target);
    await delay(320);
    return true;
  }

  async function openSiteSpecificEditorSurface(label) {
    const normalizedLabel = normalize(label || "Message");
    if (isGmailPage() && containsMailBodyLabel(normalizedLabel)) {
      const queries = [/^reply$/, /^reply all$/, /^compose$/, /^new message$/, /^write a reply$/];
      const candidates = Array.from(document.querySelectorAll("button, a, [role='button'], [tabindex], div, span"))
        .filter((element) => isVisible(element) && isLikelyClickable(element))
        .filter((element) => !isGoogleAppLauncherCandidate(element))
        .map((element) => ({
          element,
          labelText: normalize(`${elementLabel(element)} ${closestContainerText(element)}`)
        }))
        .filter((entry) => queries.some((query) => query.test(entry.labelText)))
        .sort((left, right) => right.labelText.length - left.labelText.length);

      const target = candidates[0]?.element;
      if (!target) {
        return false;
      }

      clickElement(target);
      await delay(450);
      return true;
    }

    return false;
  }

  function isGoogleAppLauncherCandidate(element) {
    const searchable = normalize(`${elementLabel(element)} ${closestContainerText(element)}`);
    const href = normalize(element.getAttribute?.("href"));
    return /photos|drive|maps|calendar|youtube|play|meet|google apps|app launcher|gemini/.test(searchable) ||
      href.includes("photos.google.com") ||
      href.includes("drive.google.com");
  }

  function scoreEditorLaunchCandidate(element, queries) {
    const searchable = normalize(`${elementLabel(element)} ${closestContainerText(element)}`);
    if (!searchable || containsSearchLikeLabel(searchable)) {
      return 0;
    }

    if (isGoogleAppLauncherCandidate(element)) {
      return 0;
    }

    if (/(send|submit|delete|discard|archive|spam|photo|photos|image|gallery|drive|emoji|format|attach|attachment|link)/.test(searchable)) {
      return 0;
    }

    let score = 0;
    for (const query of queries) {
      if (textMatches(searchable, query)) {
        score += 30;
      }
    }

    if (/(^reply$|^reply all$|^compose$|^new message$|^message$|^chat$|^comment$|write a reply|send a message)/.test(searchable)) {
      score += 35;
    }

    return score;
  }

  function cssEscape(value) {
    return String(value || "").replace(/\\/g, "\\\\").replace(/"/g, '\\"');
  }

  function pushUnique(values, nextValue) {
    if (!values.some((value) => normalize(value) === normalize(nextValue))) {
      values.push(nextValue);
    }
  }

  function delay(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  function editorContentAlreadyVisible(text) {
    const rawText = String(text || "");
    const normalizedText = normalize(rawText);
    if (!normalizedText || normalizedText.length < 24) {
      return false;
    }

    const bodyText = normalize(document.body?.innerText || "");
    if (!bodyText) {
      return false;
    }

    if (bodyText.includes(normalizedText)) {
      return true;
    }

    const lines = rawText
      .split(/\r?\n/)
      .map((line) => normalize(line))
      .filter((line) => line.length >= 18);

    if (lines.length === 0) {
      const firstSlice = normalizedText.slice(0, Math.min(140, normalizedText.length));
      const lastSlice = normalizedText.slice(Math.max(0, normalizedText.length - 140));
      return (firstSlice.length >= 40 && bodyText.includes(firstSlice)) ||
        (lastSlice.length >= 40 && bodyText.includes(lastSlice));
    }

    const matchedLines = lines.filter((line) => bodyText.includes(line));
    if (matchedLines.length >= Math.min(2, lines.length)) {
      return true;
    }

    const firstSlice = normalizedText.slice(0, Math.min(140, normalizedText.length));
    const lastSlice = normalizedText.slice(Math.max(0, normalizedText.length - 140));
    return (firstSlice.length >= 40 && bodyText.includes(firstSlice)) ||
      (lastSlice.length >= 40 && bodyText.includes(lastSlice));
  }

  function isDuplicatedText(actualValue, expectedValue) {
    const actual = normalize(actualValue);
    const expected = normalize(expectedValue);
    if (!actual || !expected) {
      return false;
    }

    return actual === `${expected} ${expected}` ||
      actual === `${expected}${expected}` ||
      actual === `${expected}\n${expected}`;
  }

  function buildGoogleFormsQuestionBlock(container, fallbackIndex, headingOverride = null) {
    if (!(container instanceof Element) || !isVisible(container)) {
      return null;
    }

    const heading = headingOverride || Array.from(container.querySelectorAll("[role='heading']")).find(isVisible);
    const headingText = normalize(elementLabel(heading));
    const questionText = normalizeQuestionText(headingText || normalize(container.innerText || container.textContent));
    const contextText = normalize(container.innerText || container.textContent);
    const seenChoices = new Set();
    const seenResponseFields = new Set();
    let choiceOrdinal = 0;

    const choices = Array.from(
      container.querySelectorAll("label, [role='radio'], [role='checkbox'], input[type='radio'], input[type='checkbox']")
    )
      .filter(isVisible)
      .map((element) => {
        const labelText = elementLabel(element);
        const normalizedOption = normalizeOptionText(labelText);
        if (!normalizedOption) {
          return null;
        }

        const verificationTarget = resolveChoiceVerificationTarget(element) || element;
        const key = `${normalizedOption}|${verificationTarget.tagName}|${verificationTarget.getAttribute?.("aria-label") || ""}`;
        if (seenChoices.has(key)) {
          return null;
        }

        seenChoices.add(key);
        return {
          index: choiceOrdinal++,
          element,
          labelText,
          normalizedOption
        };
      })
      .filter(Boolean);

    const responseFields = Array.from(
      container.querySelectorAll("input, textarea, select, [role='textbox'], [contenteditable='true']")
    )
      .map((element) => getEditableHost(element))
      .filter((element) => {
        if (!element || !isVisible(element) || !isEditable(element) || isChoiceControlElement(element) || seenResponseFields.has(element)) {
          return false;
        }

        seenResponseFields.add(element);
        return true;
      });

    if (!questionText || (choices.length === 0 && responseFields.length === 0)) {
      return null;
    }

    return {
      index: fallbackIndex,
      container,
      heading,
      hasHeading: Boolean(heading && headingText),
      questionText,
      normalizedQuestion: normalizeQuestionText(questionText),
      contextText,
      choices,
      responseFields
    };
  }

  function isLikelyGoogleFormsQuestionBlock(block) {
    if (!block) {
      return false;
    }

    if (block.hasHeading) {
      return true;
    }

    if (Array.isArray(block.responseFields) && block.responseFields.length > 0) {
      return true;
    }

    if (!Array.isArray(block.choices) || block.choices.length < 2) {
      return false;
    }

    const firstChoice = block.choices[0];
    if (!firstChoice) {
      return false;
    }

    return scoreTextSimilarity(block.questionText, firstChoice.labelText) < 85;
  }

  function findGoogleFormsQuestionContainerForHeading(heading) {
    let current = heading instanceof Element ? heading : null;
    while (current && current !== document.body) {
      const block = buildGoogleFormsQuestionBlock(current, -1, heading);
      if (isLikelyGoogleFormsQuestionBlock(block)) {
        return current;
      }

      current = current.parentElement;
    }

    return null;
  }

  function findGoogleFormsQuestionContainerForControl(control) {
    let current = control instanceof Element ? control : null;
    while (current && current !== document.body) {
      const block = buildGoogleFormsQuestionBlock(current, -1);
      if (isLikelyGoogleFormsQuestionBlock(block)) {
        return current;
      }

      current = current.parentElement;
    }

    return null;
  }

  function sortGoogleFormsBlocksByDocumentOrder(blocks) {
    return [...blocks].sort((left, right) => {
      if (left.container === right.container) {
        return 0;
      }

      const position = left.container.compareDocumentPosition(right.container);
      if (position & Node.DOCUMENT_POSITION_FOLLOWING) {
        return -1;
      }

      if (position & Node.DOCUMENT_POSITION_PRECEDING) {
        return 1;
      }

      return 0;
    });
  }

  function dedupeGoogleFormsBlocks(blocks, options = {}) {
    const preserveDistinctHeadings = Boolean(options.preserveDistinctHeadings);
    const ordered = sortGoogleFormsBlocksByDocumentOrder(blocks).filter(Boolean);
    const deduped = [];

    for (const block of ordered) {
      const exactIndex = deduped.findIndex((existing) => existing.container === block.container);
      if (exactIndex >= 0) {
        if (block.hasHeading && !deduped[exactIndex].hasHeading) {
          deduped[exactIndex] = block;
        }
        continue;
      }

      const containingIndex = deduped.findIndex((existing) => existing.container.contains(block.container));
      if (containingIndex >= 0) {
        if (
          preserveDistinctHeadings &&
          deduped[containingIndex].hasHeading &&
          block.hasHeading &&
          deduped[containingIndex].normalizedQuestion &&
          block.normalizedQuestion &&
          deduped[containingIndex].normalizedQuestion !== block.normalizedQuestion
        ) {
          deduped.push(block);
          continue;
        }
        deduped[containingIndex] = block;
        continue;
      }

      if (deduped.some((existing) => {
        if (!block.container.contains(existing.container)) {
          return false;
        }

        if (
          preserveDistinctHeadings &&
          existing.hasHeading &&
          block.hasHeading &&
          existing.normalizedQuestion &&
          block.normalizedQuestion &&
          existing.normalizedQuestion !== block.normalizedQuestion
        ) {
          return false;
        }

        return true;
      })) {
        continue;
      }

      deduped.push(block);
    }

    return deduped;
  }

  function collectGoogleFormsQuestionBlocks() {
    const listItemBlocks = dedupeGoogleFormsBlocks(Array.from(document.querySelectorAll("[role='listitem']"))
      .filter(isVisible)
      .map((container, index) => buildGoogleFormsQuestionBlock(container, index))
      .filter((block) => isLikelyGoogleFormsQuestionBlock(block)));
    const candidateContainers = [];
    const candidateBlocks = [];
    const shouldPreserveDistinctHeadingBlock = (existingBlock, nextBlock) => {
      if (!existingBlock || !nextBlock || !existingBlock.hasHeading || !nextBlock.hasHeading) {
        return false;
      }

      if (!existingBlock.normalizedQuestion || !nextBlock.normalizedQuestion) {
        return false;
      }

      return existingBlock.normalizedQuestion !== nextBlock.normalizedQuestion;
    };
    const addCandidateBlock = (container, heading = null) => {
      if (!container) {
        return;
      }

      const block = buildGoogleFormsQuestionBlock(container, candidateBlocks.length, heading);
      if (!isLikelyGoogleFormsQuestionBlock(block)) {
        return;
      }

      const exactIndex = candidateContainers.findIndex((existing) => existing === container);
      if (exactIndex >= 0) {
        if (heading && !candidateBlocks[exactIndex]?.heading) {
          const refreshed = buildGoogleFormsQuestionBlock(container, candidateBlocks[exactIndex].index, heading);
          if (isLikelyGoogleFormsQuestionBlock(refreshed)) {
            candidateBlocks[exactIndex] = refreshed;
          }
        }
        return;
      }

      const containingIndex = candidateContainers.findIndex((existing, index) => {
        if (!existing.contains(container)) {
          return false;
        }

        return !shouldPreserveDistinctHeadingBlock(candidateBlocks[index], block);
      });
      if (containingIndex >= 0) {
        candidateContainers.splice(containingIndex, 1);
        candidateBlocks.splice(containingIndex, 1);
      } else if (candidateContainers.some((existing, index) => {
        if (!container.contains(existing)) {
          return false;
        }

        return !shouldPreserveDistinctHeadingBlock(candidateBlocks[index], block);
      })) {
        return;
      }

      candidateContainers.push(container);
      candidateBlocks.push(block);
    };

    const visibleHeadings = Array.from(document.querySelectorAll("[role='heading']")).filter(isVisible);

    for (const heading of visibleHeadings) {
      const container = findGoogleFormsQuestionContainerForHeading(heading);
      addCandidateBlock(container, heading);
    }

    const headingBlocks = dedupeGoogleFormsBlocks(candidateBlocks, { preserveDistinctHeadings: true });
    if (headingBlocks.length > 0) {
      return headingBlocks.map((block, questionOrdinal) => ({
        ...block,
        index: questionOrdinal,
        questionIndex: questionOrdinal + 1
      }));
    }

    const visibleControls = Array.from(
      document.querySelectorAll("input, textarea, select, [role='radio'], [role='checkbox'], [role='textbox'], [contenteditable='true']")
    ).filter((element) => isVisible(element) && (isChoiceControlElement(element) || isEditable(element) || element.tagName?.toLowerCase() === "select"));

    for (const control of visibleControls) {
      const container = findGoogleFormsQuestionContainerForControl(control);
      addCandidateBlock(container);
    }

    const combinedBlocks = dedupeGoogleFormsBlocks([
      ...candidateBlocks,
      ...listItemBlocks
    ]);

    return combinedBlocks.map((block, questionOrdinal) => ({
      ...block,
      index: questionOrdinal,
      questionIndex: questionOrdinal + 1
    }));
  }

  function isChoiceControlElement(element) {
    if (!(element instanceof Element)) {
      return false;
    }

    const target = getEditableHost(element) || element;
    const tagName = target.tagName.toLowerCase();
    const type = normalize(target.getAttribute("type"));
    return (tagName === "input" && (type === "radio" || type === "checkbox")) ||
      target.matches("[role='radio'], [role='checkbox']");
  }

  function extractChoiceIndex(value) {
    const normalizedValue = normalize(value);
    if (!normalizedValue) {
      return -1;
    }

    const letterMatch = normalizedValue.match(/^(?:option\s*)?([a-z])(?:[\).:-]|\s|$)/i);
    if (letterMatch) {
      const index = letterMatch[1].toLowerCase().charCodeAt(0) - 97;
      return index >= 0 && index < 26 ? index : -1;
    }

    const numericMatch = normalizedValue.match(/^(\d+)(?:[\).:-]|\s|$)/);
    if (numericMatch) {
      const index = Number.parseInt(numericMatch[1], 10) - 1;
      return Number.isFinite(index) && index >= 0 ? index : -1;
    }

    return -1;
  }

  function compactText(value) {
    return normalize(value).replace(/[^a-z0-9]+/gi, "");
  }

  function normalizeSimilarityToken(token) {
    const cleaned = normalize(token).replace(/[^a-z0-9]+/gi, "");
    if (cleaned.length <= 4) {
      return cleaned;
    }

    if (cleaned.endsWith("ies")) {
      return `${cleaned.slice(0, -3)}y`;
    }

    if (/(ses|xes|zes|ches|shes)$/.test(cleaned)) {
      return cleaned.slice(0, -2);
    }

    if (cleaned.endsWith("s") && !cleaned.endsWith("ss")) {
      return cleaned.slice(0, -1);
    }

    return cleaned;
  }

  function tokenizeForSimilarity(value) {
    return new Set(
      normalize(value)
        .split(/\s+/)
        .map(normalizeSimilarityToken)
        .filter((token) => token.length >= 3)
    );
  }

  function scoreTextSimilarity(candidate, query) {
    const normalizedCandidate = normalize(candidate);
    const normalizedQuery = normalize(query);
    if (!normalizedCandidate || !normalizedQuery) {
      return 0;
    }

    if (textMatches(normalizedCandidate, normalizedQuery)) {
      return 100;
    }

    const compactCandidate = compactText(normalizedCandidate);
    const compactQuery = compactText(normalizedQuery);
    if (compactCandidate && compactQuery &&
      (compactCandidate.includes(compactQuery) || compactQuery.includes(compactCandidate))) {
      return 92;
    }

    const candidateTokens = tokenizeForSimilarity(normalizedCandidate);
    const queryTokens = tokenizeForSimilarity(normalizedQuery);
    if (candidateTokens.size === 0 || queryTokens.size === 0) {
      return 0;
    }

    let overlap = 0;
    for (const token of queryTokens) {
      if (candidateTokens.has(token)) {
        overlap += 1;
      }
    }

    if (overlap === 0) {
      return 0;
    }

    const queryCoverage = overlap / queryTokens.size;
    const candidateCoverage = overlap / candidateTokens.size;
    if (overlap === 1 && queryTokens.size > 1 && candidateTokens.size > 1) {
      return Math.max(24, Math.round((queryCoverage * 38) + (candidateCoverage * 16)));
    }

    return Math.round((queryCoverage * 65) + (candidateCoverage * 25));
  }

  function scoreGoogleFormsChoice(choice, answerOption, explicitChoiceIndex = -1) {
    const normalizedChoice = normalize(choice.labelText);
    const normalizedChoiceOption = normalize(choice.normalizedOption);
    const normalizedAnswerOption = normalizeOptionText(answerOption);
    let score = 0;

    if (explicitChoiceIndex >= 0 && explicitChoiceIndex === choice.index) {
      score += 80;
    }

    if (normalizedChoiceOption === normalizedAnswerOption) {
      score += 60;
    } else {
      score += Math.round(scoreTextSimilarity(normalizedChoiceOption, normalizedAnswerOption) * 0.45);
      score += Math.round(scoreTextSimilarity(normalizedChoice, normalizedAnswerOption) * 0.22);
    }

    const choiceIndex = explicitChoiceIndex >= 0 ? explicitChoiceIndex : extractChoiceIndex(answerOption);
    if (choiceIndex >= 0 && choiceIndex === choice.index) {
      score += 65;
    }

    return score;
  }

  function isGoogleFormsBlockQuestionMatch(block, desiredQuestion) {
    const normalizedDesiredQuestion = normalizeQuestionText(desiredQuestion);
    if (!block || !normalizedDesiredQuestion) {
      return true;
    }

    const questionSimilarity = scoreTextSimilarity(block.normalizedQuestion, normalizedDesiredQuestion);
    const contextSimilarity = scoreTextSimilarity(block.contextText, normalizedDesiredQuestion);
    return questionSimilarity >= 55 || contextSimilarity >= 68;
  }

  function findChoiceInGoogleFormsBlock(block, answerOption, explicitChoiceIndex = -1) {
    if (!block || !Array.isArray(block.choices) || block.choices.length === 0) {
      return null;
    }

    let bestChoice = null;
    let bestScore = 0;
    for (const choice of block.choices) {
      const score = scoreGoogleFormsChoice(choice, answerOption, explicitChoiceIndex);
      if (score > bestScore) {
        bestScore = score;
        bestChoice = choice;
      }
    }

    if (bestChoice) {
      return bestChoice.element;
    }

    const fallbackIndex = explicitChoiceIndex >= 0 ? explicitChoiceIndex : extractChoiceIndex(answerOption);
    if (fallbackIndex >= 0 && fallbackIndex < block.choices.length) {
      return block.choices[fallbackIndex]?.element || null;
    }

    return null;
  }

  function findGoogleFormsChoice(answer, questionBlocks) {
    const desiredQuestion = normalizeQuestionText(answer.question);
    const desiredOption = normalizeOptionText(answer.option);
    const explicitQuestionIndex = Number.isInteger(answer.questionIndex) && answer.questionIndex > 0
      ? answer.questionIndex
      : -1;
    const explicitChoiceIndex = Number.isInteger(answer.choiceIndex) && answer.choiceIndex >= 0
      ? answer.choiceIndex
      : extractChoiceIndex(answer.option);
    if (!desiredOption) {
      return null;
    }

    let bestMatch = null;
    let bestScore = 0;

    for (const block of questionBlocks) {
      let blockScore = 0;
      const exactQuestionIndexMatch = explicitQuestionIndex > 0 && block.questionIndex === explicitQuestionIndex;

      if (explicitQuestionIndex > 0) {
        if (exactQuestionIndexMatch) {
          blockScore += 130;
        } else {
          blockScore += Math.max(0, 28 - (Math.abs(block.questionIndex - explicitQuestionIndex) * 10));
        }
      }

      if (desiredQuestion) {
        const questionSimilarity = scoreTextSimilarity(block.normalizedQuestion, desiredQuestion);
        const contextSimilarity = scoreTextSimilarity(block.contextText, desiredQuestion);
        if (questionSimilarity >= 55) {
          blockScore += Math.round(questionSimilarity * 0.8);
        } else if (contextSimilarity >= 72) {
          blockScore += Math.round(contextSimilarity * 0.5);
        } else {
          continue;
        }
      }

      for (const choice of block.choices) {
        const choiceLabel = normalize(choice.labelText);
        const matchesByChoiceIndex = explicitChoiceIndex >= 0 && explicitChoiceIndex === choice.index;
        const optionSimilarity = scoreTextSimilarity(choice.normalizedOption, desiredOption);
        const labelSimilarity = scoreTextSimilarity(choiceLabel, answer.option);
        if (!matchesByChoiceIndex && optionSimilarity <= 0 && labelSimilarity <= 0) {
          continue;
        }

        let score = blockScore;
        if (choice.normalizedOption === desiredOption) {
          score += 45;
        } else {
          score += Math.max(Math.round(optionSimilarity * 0.35), Math.round(labelSimilarity * 0.2), 24);
        }

        if (isChoiceSelected(choice.element)) {
          score += 6;
        }

        if (score > bestScore) {
          bestScore = score;
          bestMatch = choice.element;
        }
      }
    }

    return bestMatch;
  }

  async function tryApplyGoogleFormsAnswerEntry(answer, questionBlocks, preferredBlockIndex = -1) {
    const explicitQuestionIndex = Number.isInteger(answer.questionIndex) && answer.questionIndex > 0
      ? answer.questionIndex
      : -1;
    const explicitChoiceIndex = Number.isInteger(answer.choiceIndex) && answer.choiceIndex >= 0
      ? answer.choiceIndex
      : extractChoiceIndex(answer.option);
    let match = findGoogleFormsChoice(answer, questionBlocks);
    if (!match && explicitQuestionIndex > 0) {
      const directBlock = questionBlocks.find((block) => block.questionIndex === explicitQuestionIndex);
      if (directBlock && isGoogleFormsBlockQuestionMatch(directBlock, answer.question)) {
        match = findChoiceInGoogleFormsBlock(directBlock, answer.option, explicitChoiceIndex);
      }
    }
    if (!match && preferredBlockIndex >= 0 && preferredBlockIndex < questionBlocks.length) {
      match = findChoiceInGoogleFormsBlock(questionBlocks[preferredBlockIndex], answer.option, explicitChoiceIndex);
    }

    if (!match) {
      const responseField = findGoogleFormsResponseField(answer, questionBlocks, preferredBlockIndex);
      if (!responseField) {
        return false;
      }

      try {
        return await fillGoogleFormsResponseField(responseField, answer.option);
      } catch {
        return false;
      }
    }

    await ensureChoiceApplied(match, "choice", answer.option);
    return isChoiceSelected(match);
  }

  function findGoogleFormsResponseField(answer, questionBlocks, preferredBlockIndex = -1) {
    const desiredQuestion = normalizeQuestionText(answer.question);
    const explicitQuestionIndex = Number.isInteger(answer.questionIndex) && answer.questionIndex > 0
      ? answer.questionIndex
      : -1;
    let bestField = null;
    let bestScore = 0;

    for (const block of questionBlocks) {
      if (!Array.isArray(block.responseFields) || block.responseFields.length === 0) {
        continue;
      }

      let blockScore = 0;
      if (preferredBlockIndex >= 0 && preferredBlockIndex === block.index) {
        blockScore += 18;
      }

      if (explicitQuestionIndex > 0) {
        if (block.questionIndex === explicitQuestionIndex) {
          blockScore += 130;
        } else {
          blockScore += Math.max(0, 24 - (Math.abs(block.questionIndex - explicitQuestionIndex) * 9));
        }
      }

      if (desiredQuestion) {
        const questionSimilarity = scoreTextSimilarity(block.normalizedQuestion, desiredQuestion);
        const contextSimilarity = scoreTextSimilarity(block.contextText, desiredQuestion);
        if (questionSimilarity > 0) {
          blockScore += Math.round(questionSimilarity * 0.8);
        } else if (contextSimilarity > 0) {
          blockScore += Math.round(contextSimilarity * 0.5);
        } else {
          continue;
        }
      }

      for (const field of block.responseFields) {
        const score = scoreGoogleFormsResponseField(field, block, answer.option, blockScore);
        if (score > bestScore) {
          bestScore = score;
          bestField = field;
        }
      }
    }

    return bestField;
  }

  function scoreGoogleFormsResponseField(field, block, answerText, blockScore) {
    const tagName = field.tagName.toLowerCase();
    const label = elementLabel(field);
    const context = closestContainerText(field);
    const placeholder = normalize(
      field.getAttribute("placeholder") ||
      field.getAttribute("aria-placeholder") ||
      field.getAttribute("data-placeholder")
    );

    let score = blockScore;
    if (tagName === "textarea") {
      score += 20;
    }

    if (tagName === "input") {
      score += 14;
    }

    if (tagName === "select") {
      score += 12;
    }

    if (normalize(field.getAttribute("aria-multiline")) === "true") {
      score += 10;
    }

    if (textMatches(`${label} ${placeholder} ${context}`, "answer") || textMatches(block.contextText, "answer")) {
      score += 12;
    }

    if (answerText.length > 80 && (tagName === "textarea" || normalize(field.getAttribute("aria-multiline")) === "true")) {
      score += 8;
    }

    return score;
  }

  async function fillGoogleFormsResponseField(field, answerText) {
    if (!(field instanceof Element)) {
      return false;
    }

    const tagName = field.tagName.toLowerCase();
    if (tagName === "select") {
      const option = Array.from(field.options).find((item) => textMatches(item.textContent, answerText));
      if (!option) {
        return false;
      }

      setNativeValue(field, option.value);
      dispatchInputEvents(field, answerText, "insertReplacementText");
      await delay(60);
      return normalize(field.value) === normalize(option.value);
    }

    await fillField(field, answerText, { forceEditor: isRichTextEditor(field) });
    await delay(70);
    return isFieldValueApplied(field, answerText);
  }

  function buildAnswerKeyResult(applied, pending) {
    const unresolved = pending
      .flatMap((entry) => {
        if (Array.isArray(entry?.answers)) {
          return entry.answers.map((answer) => ({
            question: answer?.question || entry?.question || "",
            option: answer?.option || ""
          }));
        }

        return [{
          question: entry?.answer?.question || "",
          option: entry?.answer?.option || ""
        }];
      })
      .filter((entry) => entry.question || entry.option);

    if (unresolved.length === 0) {
      return {
        applied,
        unresolvedCount: 0
      };
    }

    return {
      applied,
      unresolvedCount: unresolved.length,
      unresolved,
      warning: `Applied ${applied} answer(s), but ${unresolved.length} question(s) still need manual review.`
    };
  }

  function buildGoogleFormsAnswerGroups(answers) {
    const groupedQuestions = new Map();
    let nextGroupIndex = 0;
    const groups = [];

    for (const answer of answers) {
      const normalizedQuestion = normalizeQuestionText(answer?.question);
      const explicitQuestionIndex = Number.isInteger(answer?.questionIndex) && answer.questionIndex > 0
        ? answer.questionIndex
        : undefined;
      let groupIndex;
      const groupKey = explicitQuestionIndex !== undefined
        ? `question-index:${explicitQuestionIndex}`
        : normalizedQuestion
          ? `question-text:${normalizedQuestion}`
          : "";

      if (groupKey) {
        groupIndex = groupedQuestions.get(groupKey);
        if (groupIndex === undefined) {
          groupIndex = nextGroupIndex;
          groupedQuestions.set(groupKey, groupIndex);
          nextGroupIndex += 1;
          groups.push({
            groupIndex,
            questionIndex: explicitQuestionIndex,
            question: answer?.question || "",
            normalizedQuestion,
            answers: []
          });
        }
      } else {
        groupIndex = nextGroupIndex;
        nextGroupIndex += 1;
        groups.push({
          groupIndex,
          questionIndex: explicitQuestionIndex,
          question: answer?.question || "",
          normalizedQuestion,
          answers: []
        });
      }

      const group = groups.find((entry) => entry.groupIndex === groupIndex);
      if (group.questionIndex === undefined && explicitQuestionIndex !== undefined) {
        group.questionIndex = explicitQuestionIndex;
      }
      group.answers.push(answer);
    }

    return groups;
  }

  function scoreGoogleFormsBlockForGroup(block, group, preferredOrder = -1) {
    if (!block || !group) {
      return 0;
    }

    let score = 0;
    if (Number.isInteger(group.questionIndex) && group.questionIndex > 0) {
      if (block.questionIndex === group.questionIndex) {
        score += 130;
      } else {
        score += Math.max(0, 26 - (Math.abs(block.questionIndex - group.questionIndex) * 10));
      }
    }

    if (preferredOrder >= 0) {
      score += Math.max(0, 20 - (Math.abs(block.index - preferredOrder) * 4));
    }

    if (group.normalizedQuestion) {
      const questionSimilarity = scoreTextSimilarity(block.normalizedQuestion, group.normalizedQuestion);
      const contextSimilarity = scoreTextSimilarity(block.contextText, group.normalizedQuestion);
      const exactQuestionIndexMatch = Number.isInteger(group.questionIndex) &&
        group.questionIndex > 0 &&
        block.questionIndex === group.questionIndex;

      if (questionSimilarity <= 0 && contextSimilarity <= 0 && !exactQuestionIndexMatch) {
        return 0;
      }

      score += Math.round(questionSimilarity * 0.9);
      score += Math.round(contextSimilarity * 0.45);
    }

    let matchedChoiceAnswers = 0;
    let matchedResponseAnswers = 0;
    for (const answer of group.answers) {
      const explicitChoiceIndex = Number.isInteger(answer.choiceIndex) && answer.choiceIndex >= 0
        ? answer.choiceIndex
        : extractChoiceIndex(answer.option);
      const bestChoiceScore = Array.isArray(block.choices)
        ? block.choices.reduce((best, choice) => Math.max(best, scoreGoogleFormsChoice(choice, answer.option, explicitChoiceIndex)), 0)
        : 0;
      if (bestChoiceScore > 0) {
        score += Math.round(bestChoiceScore * 0.6);
        matchedChoiceAnswers += 1;
        continue;
      }

      if (Array.isArray(block.responseFields) && block.responseFields.length > 0) {
        const responseScore = block.responseFields.reduce(
          (best, field) => Math.max(best, scoreGoogleFormsResponseField(field, block, answer.option, 0)),
          0
        );
        if (responseScore > 0) {
          score += responseScore;
          matchedResponseAnswers += 1;
        }
      }
    }

    if (matchedChoiceAnswers === group.answers.length && group.answers.length > 0) {
      score += 60;
    }

    if (matchedChoiceAnswers === 0 && matchedResponseAnswers === 0 && score < 40) {
      return 0;
    }

    return score;
  }

  function findBestGoogleFormsBlockForGroup(group, questionBlocks, usedBlockIndexes = new Set(), preferredOrder = -1) {
    let bestBlock = null;
    let bestScore = 0;

    for (const block of questionBlocks) {
      if (!block || usedBlockIndexes.has(block.index)) {
        continue;
      }

      const score = scoreGoogleFormsBlockForGroup(block, group, preferredOrder);
      if (score > bestScore) {
        bestScore = score;
        bestBlock = block;
      }
    }

    return bestBlock;
  }

  function findExactGoogleFormsBlockForGroup(group, questionBlocks, usedBlockIndexes = new Set()) {
    if (!group) {
      return null;
    }

    if (Number.isInteger(group.questionIndex) && group.questionIndex > 0) {
      const exactBlock = questionBlocks.find((block) => block.questionIndex === group.questionIndex && !usedBlockIndexes.has(block.index));
      if (exactBlock && isGoogleFormsBlockQuestionMatch(exactBlock, group.normalizedQuestion)) {
        return exactBlock;
      }
    }

    if (!group.normalizedQuestion) {
      return null;
    }

    let bestBlock = null;
    let bestScore = 0;
    for (const block of questionBlocks) {
      if (!block || usedBlockIndexes.has(block.index)) {
        continue;
      }

      const questionSimilarity = scoreTextSimilarity(block.normalizedQuestion, group.normalizedQuestion);
      const contextSimilarity = scoreTextSimilarity(block.contextText, group.normalizedQuestion);
      const score = Math.max(questionSimilarity, Math.round(contextSimilarity * 0.75));
      if (score > bestScore) {
        bestScore = score;
        bestBlock = block;
      }
    }

    return bestScore >= 92 ? bestBlock : null;
  }

  async function tryApplyGoogleFormsAnswerGroup(group, block) {
    if (!group || !block) {
      return 0;
    }

    let appliedCount = 0;
    for (let index = group.answers.length - 1; index >= 0; index -= 1) {
      const answer = group.answers[index];
      if (!await tryApplyGoogleFormsAnswerEntry(answer, [block], 0)) {
        continue;
      }

      group.answers.splice(index, 1);
      appliedCount += 1;
    }

    return appliedCount;
  }

  async function applyGoogleFormsAnswerKey(step, answers) {
    const pending = buildGoogleFormsAnswerGroups(answers);
    const maxPages = step.advancePages ? Math.min(Math.max(pending.length + 1, 2), 12) : 1;
    let applied = 0;

    for (let pageIndex = 0; pageIndex < maxPages && pending.length > 0; pageIndex += 1) {
      let appliedThisPage = 0;
      let questionBlocks = collectGoogleFormsQuestionBlocks();
      const usedBlockIndexes = new Set();

      for (let index = pending.length - 1; index >= 0; index -= 1) {
        const group = pending[index];
        const exactBlock = findExactGoogleFormsBlockForGroup(group, questionBlocks, usedBlockIndexes);
        let matchedBlock = exactBlock || findBestGoogleFormsBlockForGroup(group, questionBlocks, usedBlockIndexes, group.groupIndex);
        if (!matchedBlock) {
          continue;
        }

        let appliedForGroup = await tryApplyGoogleFormsAnswerGroup(group, matchedBlock);
        if (appliedForGroup <= 0 && exactBlock) {
          const fallbackUsedBlocks = new Set(usedBlockIndexes);
          fallbackUsedBlocks.add(exactBlock.index);
          matchedBlock = findBestGoogleFormsBlockForGroup(group, questionBlocks, fallbackUsedBlocks, group.groupIndex);
          if (matchedBlock) {
            appliedForGroup = await tryApplyGoogleFormsAnswerGroup(group, matchedBlock);
          }
        }

        if (appliedForGroup <= 0) {
          continue;
        }

        applied += appliedForGroup;
        appliedThisPage += appliedForGroup;
        usedBlockIndexes.add(matchedBlock.index);
        if (group.answers.length === 0) {
          pending.splice(index, 1);
        }
      }

      if (pending.length > 0) {
        questionBlocks = collectGoogleFormsQuestionBlocks();
        usedBlockIndexes.clear();
        for (let index = pending.length - 1; index >= 0; index -= 1) {
          const group = pending[index];
          const exactBlock = findExactGoogleFormsBlockForGroup(group, questionBlocks, usedBlockIndexes);
          let matchedBlock = exactBlock || findBestGoogleFormsBlockForGroup(group, questionBlocks, usedBlockIndexes, group.groupIndex);
          if (!matchedBlock) {
            continue;
          }

          let appliedForGroup = await tryApplyGoogleFormsAnswerGroup(group, matchedBlock);
          if (appliedForGroup <= 0 && exactBlock) {
            const fallbackUsedBlocks = new Set(usedBlockIndexes);
            fallbackUsedBlocks.add(exactBlock.index);
            matchedBlock = findBestGoogleFormsBlockForGroup(group, questionBlocks, fallbackUsedBlocks, group.groupIndex);
            if (matchedBlock) {
              appliedForGroup = await tryApplyGoogleFormsAnswerGroup(group, matchedBlock);
            }
          }

          if (appliedForGroup <= 0) {
            continue;
          }

          applied += appliedForGroup;
          appliedThisPage += appliedForGroup;
          usedBlockIndexes.add(matchedBlock.index);
          if (group.answers.length === 0) {
            pending.splice(index, 1);
          }
        }
      }

      if (pending.length === 0 || !step.advancePages) {
        break;
      }

      const nextElement = findLikelyNavigationElement(["Next", "Continue", "Go to next", "Done", "Submit"]);
      if (!nextElement) {
        break;
      }

      clickElement(nextElement);
      await delay(appliedThisPage > 0 ? 360 : 240);
    }

    if (applied === 0) {
      throw new Error("Could not match the answer key to visible Google Forms options in this tab.");
    }

    return buildAnswerKeyResult(applied, pending);
  }

  async function applyAnswerKey(step) {
    const answers = Array.isArray(step.answers) ? step.answers.filter((answer) => answer?.option) : [];
    if (answers.length === 0) {
      throw new Error("apply_answer_key requires answers.");
    }

    if (isGoogleFormsPage()) {
      return await applyGoogleFormsAnswerKey(step, answers);
    }

    const pending = [...answers];
    const maxPages = Math.min(Math.max(pending.length + 1, 2), 12);
    let applied = 0;

    for (let pageIndex = 0; pageIndex < maxPages && pending.length > 0; pageIndex += 1) {
      let appliedThisPage = 0;
      let pagePass = 0;
      let madeProgress = true;

      while (pagePass < 3 && madeProgress && pending.length > 0) {
        madeProgress = false;

        for (let index = pending.length - 1; index >= 0; index -= 1) {
          const answer = pending[index];
          if (!await tryApplyAnswerEntry(answer)) {
            continue;
          }

          pending.splice(index, 1);
          applied += 1;
          appliedThisPage += 1;
          madeProgress = true;
        }

        pagePass += 1;
        if (madeProgress) {
          await delay(85);
        }
      }

      if (pending.length > 0) {
        for (let index = pending.length - 1; index >= 0; index -= 1) {
          const answer = pending[index];
          if (!await tryApplyAnswerEntry(answer)) {
            continue;
          }

          pending.splice(index, 1);
          applied += 1;
          appliedThisPage += 1;
        }
      }

      if (pending.length === 0) {
        break;
      }

      if (!step.advancePages) {
        break;
      }

      const nextElement = findLikelyNavigationElement(["Next", "Continue", "Go to next", "Done", "Submit"]);
      if (!nextElement) {
        break;
      }

      clickElement(nextElement);
      await delay(appliedThisPage > 0 ? 820 : 620);
    }

    if (applied === 0) {
      throw new Error("Could not match the answer key to responsive quiz options in this tab.");
    }

    return buildAnswerKeyResult(applied, pending);
  }

  function findTextResponseField(questionText, answerText) {
    const fields = getFieldCandidates().filter((element) => {
      const tagName = element.tagName.toLowerCase();
      const type = normalize(element.getAttribute("type"));
      return tagName === "textarea" ||
        tagName === "select" ||
        tagName === "input" && !["radio", "checkbox", "button", "submit", "reset", "file", "hidden"].includes(type) ||
        isContentEditable(element);
    });

    const best = fields
      .map((element) => ({
        element,
        score: scoreTextFieldCandidate(element, questionText, answerText)
      }))
      .filter((entry) => entry.score > 0)
      .sort((left, right) => right.score - left.score)
      .at(0);

    return best?.element || null;
  }

  function scoreTextFieldCandidate(element, questionText, answerText) {
    const label = elementLabel(element);
    const context = closestContainerText(element);
    const placeholder = normalize(element.getAttribute("placeholder"));
    const type = normalize(element.getAttribute("type"));
    let score = 0;

    if (questionText && textMatches(context, questionText)) {
      score += 35;
    }

    if (questionText && textMatches(label, questionText)) {
      score += 28;
    }

    if (textMatches(`${label} ${placeholder}`, "answer") || textMatches(context, "answer")) {
      score += 12;
    }

    if (textMatches(`${label} ${placeholder}`, "response") || textMatches(context, "response")) {
      score += 12;
    }

    if (type === "email" && /@/.test(answerText || "")) {
      score += 18;
    }

    if (element.tagName.toLowerCase() === "textarea" || isContentEditable(element)) {
      score += 10;
    }

    return score;
  }
})();
