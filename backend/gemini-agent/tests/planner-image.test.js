import test from "node:test";
import assert from "node:assert/strict";
import { createBrowserActionPlanner, createBrowserActionPlanRefiner } from "../src/browserActionPlanner.js";
import { extendedAlternativesForType } from "../src/contentClassifier.js";
import { describeDominantColorsFromImage } from "../src/imageAnalysis.js";

const RED_PIXEL_PNG_BASE64 =
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jPwPAfAAUAAf+mXJtdAAAAAElFTkSuQmCC";

test("image extended alternatives avoid document-only defaults", () => {
  const actions = extendedAlternativesForType("image");

  assert.ok(actions.includes("extract_dominant_colors"));
  assert.doesNotMatch(actions.join(","), /ocr_extract_text/);
  assert.doesNotMatch(actions.join(","), /extract_table_data/);
});

test("dominant color extraction uses deterministic local image analysis", () => {
  const description = describeDominantColorsFromImage(RED_PIXEL_PNG_BASE64, "image/png");

  assert.match(description, /#FF0000/i);
  assert.match(description, /red/i);
});

test("browser planner rejects numeric quiz hallucinations and falls back to visible answer-key text", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "answer_current_quiz",
        summary: "Answer the current quiz question by selecting '12'.",
        requiresConfirmation: false,
        steps: [
          {
            tool: "check_radio",
            option: "12"
          }
        ]
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    })
  });

  const plan = await planner({
    originalText: `The day before yesterday, Suzie was 17. Next year, she will be 19. What day is her birthday?
A) February 29
B) January 1
C) April 23
D) December 31`,
    resultText: "Q1 [Suzie's birthday]: February 29 - It only fits on a leap-year timeline.",
    action: "answer_question",
    voiceCommand: "attempt all questions",
    contentType: "mcq",
    browserContext: {
      url: "https://example.com/quiz",
      title: "IQ test quiz",
      visibleText: "2 / 11 February 29 January 1 April 23 December 31 Next",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "fill_form_answers");
  const answerKeyStep = plan.steps.find((step) => step.tool === "apply_answer_key");
  assert.ok(answerKeyStep);
  assert.equal(answerKeyStep.advancePages, true);
  assert.ok(answerKeyStep.answers.some((answer) => answer.option === "February 29"));
});

test("mail reply planner uses the visible reply composer without refilling recipient or subject", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => {
      throw new Error("Mail reply with visible composer should not call model planner.");
    }
  });

  const plan = await planner({
    originalText: "Hi, can you please confirm tomorrow's session?",
    resultText: "Dear Hewen,\n\nThank you for the reminder. I'll join tomorrow.\n\nBest,\nTanush",
    action: "draft_reply",
    voiceCommand: "reply to this email",
    contentType: "email",
    browserContext: {
      url: "https://mail.google.com/mail/u/0/#inbox/FMfcgzQ",
      title: "Inbox - Gmail",
      visibleText: "Reply Send Dear Hewen Best regards",
      interactiveElements: [
        { role: "textbox", label: "Write a reply", nameAttribute: "", type: "textbox", options: [] },
        { role: "button", label: "Send", nameAttribute: "", type: "button", options: [] }
      ]
    }
  });

  assert.equal(plan.goal, "reply_to_email");
  assert.ok(plan.steps.some((step) => step.tool === "fill_editor" && step.label === "Write a reply"));
  assert.ok(!plan.steps.some((step) => step.tool === "fill_label" && /to recipients|subject/i.test(step.label || "")));
  assert.ok(!plan.steps.some((step) => step.tool === "click_role" && step.name === "Reply"));
});

test("shopping planner opens amazon and flipkart comparison tabs for product-like image results", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => {
      throw new Error("Shopping task pack should use direct comparison plan.");
    }
  });

  const plan = await planner({
    originalText: "",
    resultText: "A black silicone iPhone 16 cover with raised camera protection.",
    action: "describe_image",
    voiceCommand: "",
    contentType: "image",
    browserContext: {
      url: "https://example.com/gallery",
      title: "Product screenshot",
      visibleText: "Compare product details and price",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "compare_product_prices");
  const openTabs = plan.steps.filter((step) => step.tool === "open_new_tab");
  assert.equal(openTabs.length, 2);
  assert.match(openTabs[0].url, /amazon\.in/);
  assert.match(openTabs[1].url, /flipkart\.com/);
});

test("shopping planner honors explicit execution instruction to use only amazon for add-to-cart flow", async () => {
  const planner = createBrowserActionPlanner({
    generateText: async () => {
      throw new Error("Explicit shopping execution instruction should use direct fallback planning.");
    }
  });

  const plan = await planner({
    originalText: "",
    resultText: "This image features a single ripe strawberry prominently displayed against a soft green background.",
    action: "describe_image",
    voiceCommand: "",
    executionInstruction: "Open only Amazon, not Flipkart, search for strawberry, and add to cart.",
    contentType: "image",
    browserContext: {
      url: "https://example.com/gallery",
      title: "Fruit screenshot",
      visibleText: "Search products",
      interactiveElements: []
    }
  });

  assert.equal(plan.goal, "search_product_and_add_to_cart");
  const openTabs = plan.steps.filter((step) => step.tool === "open_new_tab");
  assert.equal(openTabs.length, 1);
  assert.match(openTabs[0].url, /amazon\.in/);
  assert.doesNotMatch(openTabs[0].url, /flipkart\.com/);
  assert.ok(plan.steps.some((step) => step.tool === "click_role" && /add to cart/i.test(step.name || "")));
});

test("browser plan refiner can add an official site destination without disturbing the base plan path", async () => {
  const refiner = createBrowserActionPlanRefiner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "compare_product_prices",
        summary: "Open Amazon, Flipkart, and Logitech tabs for MX Creative Console research.",
        requiresConfirmation: false,
        steps: [
          { tool: "open_new_tab", url: "https://www.amazon.in/s?k=mx+creative+console" },
          { tool: "wait_ms", waitMs: 900 },
          { tool: "open_new_tab", url: "https://www.flipkart.com/search?q=mx+creative+console" },
          { tool: "wait_ms", waitMs: 900 },
          { tool: "open_new_tab", url: "https://www.logitech.com/en-in/search.html?q=mx%20creative%20console" }
        ]
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    }),
    planBrowserAction: async () => {
      throw new Error("Refiner should not need the fallback planner when the model returns valid JSON.");
    }
  });

  const plan = await refiner({
    originalText: "MX Creative Console",
    resultText: "Open Amazon and Flipkart search tabs to compare prices for mx creative console.",
    action: "extract_product_info",
    voiceCommand: "",
    executionInstruction: "Also open the official Logitech website for this product.",
    contentType: "product",
    browserContext: {
      url: "https://example.com/search",
      title: "Shopping page",
      visibleText: "Compare prices and details",
      interactiveElements: []
    },
    currentPlan: {
      goal: "compare_product_prices",
      summary: "Open Amazon and Flipkart search tabs to compare prices for mx creative console.",
      requiresConfirmation: false,
      steps: [
        { tool: "open_new_tab", url: "https://www.amazon.in/s?k=mx+creative+console" },
        { tool: "wait_ms", waitMs: 900 },
        { tool: "open_new_tab", url: "https://www.flipkart.com/search?q=mx+creative+console" }
      ]
    }
  });

  const openTabs = plan.steps.filter((step) => step.tool === "open_new_tab");
  assert.equal(openTabs.length, 3);
  assert.ok(openTabs.some((step) => /amazon\.in/.test(step.url || "")));
  assert.ok(openTabs.some((step) => /flipkart\.com/.test(step.url || "")));
  assert.ok(openTabs.some((step) => /logitech\.com/.test(step.url || "")));
});

test("browser plan refiner preserves the existing plan when refinement output is unsafe", async () => {
  const currentPlan = {
    goal: "compare_product_prices",
    summary: "Open Amazon and Flipkart search tabs to compare prices for mx creative console.",
    requiresConfirmation: false,
    steps: [
      { tool: "open_new_tab", url: "https://www.amazon.in/s?k=mx+creative+console" },
      { tool: "open_new_tab", url: "https://www.flipkart.com/search?q=mx+creative+console" }
    ]
  };

  const refiner = createBrowserActionPlanRefiner({
    generateText: async () => ({
      text: JSON.stringify({
        goal: "bad_plan",
        summary: "Unsafe refinement",
        requiresConfirmation: false,
        steps: [
          { tool: "unsupported_tool", url: "https://example.com" }
        ]
      }),
      model: "fake-test-model",
      latencyMs: 5,
      usage: { inputTokens: 10, outputTokens: 10 }
    }),
    planBrowserAction: async () => {
      throw new Error("Current plan should be preserved before fallback planner is used.");
    }
  });

  const plan = await refiner({
    originalText: "MX Creative Console",
    resultText: "Open Amazon and Flipkart search tabs to compare prices for mx creative console.",
    action: "extract_product_info",
    voiceCommand: "",
    executionInstruction: "Also open the official Logitech website for this product.",
    contentType: "product",
    browserContext: {
      url: "https://example.com/search",
      title: "Shopping page",
      visibleText: "Compare prices and details",
      interactiveElements: []
    },
    currentPlan
  });

  assert.deepEqual(plan.steps, currentPlan.steps);
});
