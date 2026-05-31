function normalize(value = "") {
  return String(value).trim().toLowerCase();
}

function includesAny(haystack, needles) {
  return needles.some((needle) => haystack.includes(needle));
}

function containsWholePhrase(haystack, phrase) {
  const escaped = String(phrase).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`(^|\\b)${escaped}(\\b|$)`, "i").test(String(haystack || ""));
}

function looksLikeMailSurface({ url, title, visibleText }) {
  if (includesAny(url, ["mail.google.com", "outlook.office.com", "outlook.live.com"])) {
    return true;
  }

  const headingText = `${title} ${visibleText}`;
  return [
    "compose",
    "new message",
    "inbox",
    "reply",
    "reply all",
    "schedule send"
  ].some((phrase) => containsWholePhrase(headingText, phrase));
}

export function detectBrowserTaskPack({ browserContext, contentType, action, voiceCommand }) {
  const url = normalize(browserContext?.url);
  const title = normalize(browserContext?.title);
  const visibleText = normalize(browserContext?.visibleText);
  const contextText = `${url} ${title} ${visibleText}`;
  const normalizedContentType = normalize(contentType);
  const normalizedAction = normalize(action).replaceAll("_", " ");
  const normalizedVoiceCommand = normalize(voiceCommand);

  if (looksLikeMailSurface({ url, title, visibleText })) {
    return {
      id: "mail_compose",
      label: "Mail Compose",
      guidance: [
        "Mail pack: prioritize compose/send/schedule flows.",
        "If compose fields are visible, prefer filling To, Subject, and message fields directly.",
        "If compose is not open but a Compose/New message button is visible, opening compose first is acceptable.",
        "Use the generated result as the email body unless the voice command clearly asks for another destination."
      ].join(" ")
    };
  }

  if (includesAny(contextText, ["discord.com", "discord", "message @", "direct messages"])) {
    return {
      id: "discord",
      label: "Discord",
      guidance: [
        "Discord pack: prioritize opening the correct DM or channel, focusing the message composer, and typing the generated message.",
        "Use fill_editor for the composer when possible, and fall back to type_active only when the editor is already focused.",
        "Only trigger send when the command explicitly asks for it, and keep confirmation required."
      ].join(" ")
    };
  }

  if (includesAny(contextText, ["docs.google.com/forms", "forms.gle", "google forms"])) {
    return {
      id: "google_forms",
      label: "Google Forms",
      guidance: [
        "Google Forms pack: prioritize check_radio, check_checkbox, fill_label, and select_option.",
        "For MCQ/question flows, map each answer to the visible question block and option label.",
        "Avoid submit unless the voice command explicitly requests submission."
      ].join(" ")
    };
  }

  if (includesAny(contextText, ["docs.google.com/document", "google docs"])) {
    return {
      id: "google_docs",
      label: "Google Docs",
      guidance: [
        "Google Docs pack: prefer typing or replacing content in the active document editor.",
        "Use type_active when the document editor is already focused; otherwise focus the editor before typing."
      ].join(" ")
    };
  }

  if (includesAny(contextText, ["notion.so", "notion.site", "notion"])) {
    return {
      id: "notion",
      label: "Notion",
      guidance: [
        "Notion pack: prioritize focusing the active editor block and typing or pasting generated content.",
        "Use visible page controls only when they are clearly present in the page context."
      ].join(" ")
    };
  }

  if (
    normalizedContentType === "product" ||
    includesAny(contextText, ["amazon", "flipkart", "walmart", "shopping", "price", "reviews"])
  ) {
    return {
      id: "shopping",
      label: "Shopping",
      guidance: [
        "Shopping pack: prioritize compare/reviews/details flows.",
        "Use current product context to open reviews, compare options, or extract visible purchase details.",
        "Avoid purchase/checkout actions unless the command explicitly asks for them, and require confirmation."
      ].join(" ")
    };
  }

  if (
    normalizedContentType === "mcq" ||
    (normalizedContentType === "question" && includesAny(`${normalizedAction} ${normalizedVoiceCommand}`, ["fill", "check", "select", "submit"]))
  ) {
    return {
      id: "qa_form",
      label: "Question Form",
      guidance: [
        "Question form pack: interpret the generated answer as input for visible question fields and answer controls.",
        "Prefer question-specific selection over generic clicking."
      ].join(" ")
    };
  }

  return null;
}
