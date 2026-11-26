"use strict";

// Alt+A "只注释生词": 默认关闭时行为不变; 开启后只显示生词标签, 标"会了"/撤销, 切回整段翻译, 日文和坏回复退回整段翻译.
const { test, expect, requestsOf, readAnnotateKnown, clearAnnotateCache } = require("./fixtures");

const BAR = "#bili-index-imm-bar";
const TAG = "button.bit-imm-note";

// Words below rank 5000 in the English list are "known" at the default level, these are not.
const P1 = "The meticulous gardener pruned the wisteria.";
const P2 = "A quaint harbor with a meticulous lantern.";
const ZH1 = "国家的宪法保障公民的权利。";
const JA = "今日は天気がいいので、公園へ散歩に行きます。";

async function setBody(page, paragraphs) {
  await page.evaluate((list) => {
    document.body.replaceChildren();
    list.forEach((text, i) => {
      const p = document.createElement("p");
      p.id = "t" + (i + 1);
      p.textContent = text;
      document.body.appendChild(p);
    });
  }, paragraphs);
}

async function annotatePage(page, settings, serviceWorker, paragraphs, overrides = {}) {
  await settings({ sync: overrides.sync, local: { annotateEnabled: true } });
  await clearAnnotateCache(serviceWorker);
  await setBody(page, paragraphs);
  await page.keyboard.press("Alt+KeyA");
  await expect(page.locator(BAR + " .bit-imm-status")).toContainText("就绪");
}

async function markWord(page, key) {
  await page.locator(TAG + '[data-key="' + key + '"]').click();
  await page.locator(".bit-imm-known").click();
}

const texts = (page, selector) => page.locator(selector).evaluateAll((els) => els.map((el) => el.textContent));

test.describe("只注释生词", () => {
  test("开关默认关闭: Alt+A 整段翻译, 没有切换按钮, 不发注释请求", async ({ page, upstream }) => {
    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator("#p-sentence > .bit-imm-block")).toHaveText(
      "译文：The expedition persevered toward the summit despite the storm."
    );
    await expect(page.locator(BAR + " .bit-imm-annot-btn")).toBeHidden();
    await expect(page.locator(BAR + " .bit-imm-btn", { hasText: "双语" })).toBeVisible();
    await expect(page.locator(TAG)).toHaveCount(0);
    expect(requestsOf(upstream, "annotate")).toHaveLength(0);
  });

  test("开启后(目标语言中文, 英文页): 只有标签没有整段译文, 切换按钮在, 2B/9B 和仅译按钮隐藏", async ({
    page,
    settings,
    serviceWorker,
    upstream
  }) => {
    await annotatePage(page, settings, serviceWorker, [P1, P2]);
    const t1 = page.locator("#t1 " + TAG);
    await expect(t1).toHaveText([
      "meticulous · 注:meticulous",
      "gardener · 注:gardener",
      "pruned · 注:pruned",
      "wisteria · 注:wisteria"
    ]);
    // The word already shown in the first paragraph is not annotated again in the second.
    await expect(page.locator("#t2 " + TAG)).toHaveText(["quaint · 注:quaint", "harbor · 注:harbor", "lantern · 注:lantern"]);

    for (const id of ["#t1", "#t2"]) {
      await expect(page.locator(id + " > .bit-imm-block")).toHaveCount(1);
      await expect(page.locator(id + " > .bit-imm-block")).not.toContainText("译文");
    }
    // The original text is intact and visible.
    await expect(page.locator("#t1")).toContainText(P1);
    await expect(page.locator("html")).not.toHaveAttribute("data-bit-imm-mode", "target-only");

    const bar = page.locator(BAR);
    const annotBtn = bar.locator(".bit-imm-annot-btn");
    await expect(annotBtn).toBeVisible();
    await expect(annotBtn).toHaveText("注释");
    await expect(bar.locator(".bit-imm-btn", { hasText: "2B" })).toBeHidden();
    await expect(bar.locator(".bit-imm-btn", { hasText: "9B" })).toBeHidden();
    await expect(bar.locator(".bit-imm-btn", { hasText: "双语" })).toBeHidden();
    await expect(bar.locator(".bit-imm-btn", { hasText: "仅译" })).toBeHidden();

    const asked = requestsOf(upstream, "annotate");
    expect(asked.length).toBeGreaterThan(0);
    expect(requestsOf(upstream, "immersive")).toHaveLength(0);
    expect(asked[0].body.stream).toBe(false);
  });

  test("开启后(目标语言 English, 中文页): 中文段落出标签", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [ZH1], { sync: { target: "English" } });
    await expect(page.locator("#t1 " + TAG)).toHaveText(["宪法 · note:宪法", "保障 · note:保障"]);
    await expect(page.locator("#t1 > .bit-imm-block")).not.toContainText("译文");
  });

  test("整页去重: 同时在请求的两批里出现同一个生词, 整页只注一次", async ({ page, settings, serviceWorker, upstream }) => {
    const others = ["gardener", "harbor", "lantern", "wisteria", "quaint", "pruned", "ephemeral", "meticulous"];
    // 8 paragraphs = 2 batches that run at the same time, so both are asked before either has an answer.
    const paragraphs = others.map((w, i) => "The meticulous " + w + " number " + (i + 1) + " was noted.");
    await annotatePage(page, settings, serviceWorker, paragraphs);
    await expect(page.locator(BAR + " .bit-imm-status")).toContainText("8/8");
    expect(requestsOf(upstream, "annotate").length).toBeGreaterThanOrEqual(2);
    await expect(page.locator(TAG + '[data-key="meticulous"]')).toHaveCount(1);
    const keys = await texts(page, TAG);
    expect(new Set(keys).size).toBe(keys.length);
  });

  test("点标签再点会了: 标签消失, 存储里有这个词, 再开一次 Alt+A 不再出现", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [P1, P2]);
    await expect(page.locator(TAG + '[data-key="meticulous"]')).toHaveCount(1);

    await page.locator(TAG + '[data-key="meticulous"]').click();
    await expect(page.locator(".bit-imm-known")).toHaveText("会了");
    await page.locator(".bit-imm-known").click();

    await expect(page.locator(TAG + '[data-key="meticulous"]')).toHaveCount(0);
    await expect(page.locator(BAR + " .bit-imm-notice")).toContainText("已标记「meticulous」");
    await expect(page.locator(BAR + " .bit-imm-undo")).toHaveText("撤销");
    await expect.poll(() => readAnnotateKnown(serviceWorker)).toMatchObject({ v: 1, en: ["meticulous"] });
    await expect(page.locator("#t1 " + TAG)).toHaveCount(3);

    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator(TAG)).toHaveCount(0);
    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator("#t1 " + TAG)).toHaveCount(3);
    await expect(page.locator(TAG + '[data-key="meticulous"]')).toHaveCount(0);
    // The word is not annotated by the other paragraph either.
    await expect(page.locator("#t2 " + TAG)).toHaveCount(3);
  });

  test("一段的标签全部标成会了后, 这一段的标签行也撤掉", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, ["The meticulous wisteria."]);
    await expect(page.locator("#t1 " + TAG)).toHaveCount(2);
    await markWord(page, "meticulous");
    await expect(page.locator("#t1 " + TAG)).toHaveCount(1);
    await markWord(page, "wisteria");
    await expect(page.locator("#t1 " + TAG)).toHaveCount(0);
    await expect(page.locator("#t1 .bit-imm-block")).toHaveCount(0);
    // Only the newest notice stays on the bar.
    await expect(page.locator(BAR + " .bit-imm-undo")).toHaveCount(1);
    await expect(page.locator(BAR + " .bit-imm-notice")).toContainText("已标记「wisteria」");

    await page.locator(BAR + " .bit-imm-undo").click();
    await expect(page.locator("#t1 " + TAG)).toHaveText(["wisteria · 注:wisteria"]);
    await expect(page.locator("#t1 .bit-imm-block")).toHaveCount(1);
  });

  test("撤销: 标签按原位置放回, 存储里的词被移除", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [P1, P2]);
    const before = await texts(page, "#t1 " + TAG);
    expect(before).toHaveLength(4);

    await markWord(page, "gardener");
    await expect(page.locator(TAG + '[data-key="gardener"]')).toHaveCount(0);
    await expect.poll(() => readAnnotateKnown(serviceWorker)).toMatchObject({ en: ["gardener"] });

    await page.locator(BAR + " .bit-imm-undo").click();
    await expect(page.locator(TAG + '[data-key="gardener"]')).toHaveCount(1);
    expect(await texts(page, "#t1 " + TAG)).toEqual(before);
    await expect(page.locator(BAR + " .bit-imm-notice")).toBeEmpty();
    await expect.poll(() => readAnnotateKnown(serviceWorker)).toMatchObject({ en: [] });
  });

  test("按 Esc 或点别处收起会了按钮, 不标记", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [P1]);
    const tag = page.locator(TAG + '[data-key="gardener"]');
    await tag.click();
    await expect(page.locator(".bit-imm-known")).toHaveCount(1);
    await page.keyboard.press("Escape");
    await expect(page.locator(".bit-imm-known")).toHaveCount(0);

    await tag.click();
    await expect(page.locator(".bit-imm-known")).toHaveCount(1);
    await page.locator("#t1").click({ position: { x: 3, y: 3 } });
    await expect(page.locator(".bit-imm-known")).toHaveCount(0);
    await expect(page.locator(TAG)).toHaveCount(4);
    expect(await readAnnotateKnown(serviceWorker)).toBeUndefined();
  });

  test("功能在页面打开后被关掉: 标记失败, 提示注释功能已关闭, 标签保留", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [P1]);
    await serviceWorker.evaluate(() => chrome.storage.local.set({ annotateEnabled: false }));
    await markWord(page, "gardener");
    await expect(page.locator(BAR + " .bit-imm-notice")).toHaveText("注释功能已关闭");
    await expect(page.locator(TAG + '[data-key="gardener"]')).toHaveCount(1);
    await expect(page.locator(BAR + " .bit-imm-undo")).toHaveCount(0);
  });

  test("别的标签页标记了一个词: 本页的标签随之消失", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [P1]);
    await expect(page.locator("#t1 " + TAG)).toHaveCount(4);
    await serviceWorker.evaluate(() =>
      chrome.storage.local.set({ annotateKnown: { v: 1, zh: [], en: ["wisteria"], es: [] } })
    );
    await expect(page.locator(TAG + '[data-key="wisteria"]')).toHaveCount(0);
    await expect(page.locator("#t1 " + TAG)).toHaveCount(3);
    // Not a mark made on this page: no undo is offered.
    await expect(page.locator(BAR + " .bit-imm-undo")).toHaveCount(0);
  });

  test("切到翻译模式: 整段翻译, 2B/9B 回来, 再切回注释", async ({ page, settings, serviceWorker, upstream }) => {
    await annotatePage(page, settings, serviceWorker, [P1]);
    await expect(page.locator("#t1 " + TAG)).toHaveCount(4);
    const bar = page.locator(BAR);

    await bar.locator(".bit-imm-annot-btn").click();
    await expect(bar.locator(".bit-imm-annot-btn")).toHaveText("翻译");
    await expect(page.locator("#t1 > .bit-imm-block")).toHaveText("译文：" + P1);
    await expect(page.locator(TAG)).toHaveCount(0);
    await expect(bar.locator(".bit-imm-btn", { hasText: "2B" })).toBeVisible();
    await expect(bar.locator(".bit-imm-btn", { hasText: "双语" })).toBeVisible();
    // A single paragraph is sent as a plain translate request, several as an immersive batch.
    expect(requestsOf(upstream, "immersive").length + requestsOf(upstream, "translate").length).toBeGreaterThan(0);

    await bar.locator(".bit-imm-annot-btn").click();
    await expect(bar.locator(".bit-imm-annot-btn")).toHaveText("注释");
    await expect(page.locator("#t1 " + TAG)).toHaveCount(4);
    await expect(page.locator("#t1")).not.toContainText("译文");
  });

  test("日文段落照常整段翻译, 同批的英文段落仍然注释", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [P1, JA]);
    await expect(page.locator("#t2 > .bit-imm-block")).toHaveText("译文：" + JA);
    await expect(page.locator("#t2 " + TAG)).toHaveCount(0);
    await expect(page.locator("#t1 " + TAG)).toHaveCount(4);
  });

  test("模型回复格式不合格: 退回整段翻译", async ({ page, settings, serviceWorker, upstream }) => {
    upstream.override("annotate", { reply: "Sorry, here is a summary instead of the list.", times: 10 });
    await annotatePage(page, settings, serviceWorker, [P1, P2]);
    await expect(page.locator("#t1 > .bit-imm-block")).toHaveText("译文：" + P1);
    await expect(page.locator("#t2 > .bit-imm-block")).toHaveText("译文：" + P2);
    await expect(page.locator(TAG)).toHaveCount(0);
  });

  test("整批失败时显示 [HTTP 400]", async ({ page, settings, serviceWorker, upstream }) => {
    upstream.override("annotate", { status: 400, body: "bad request", times: 10 });
    await annotatePage(page, settings, serviceWorker, [P1]);
    await expect(page.locator("#t1 > .bit-imm-block")).toHaveText("[HTTP 400]");
  });

  test("无障碍: 标签和会了按钮都有可访问名称, 按 Esc 后焦点回到标签", async ({ page, settings, serviceWorker }) => {
    await annotatePage(page, settings, serviceWorker, [P1]);
    const tag = page.locator(TAG + '[data-key="gardener"]');
    await expect(tag).toHaveAccessibleName("gardener · 注:gardener，点击可标为已会");
    await expect(tag).toHaveAttribute("aria-expanded", "false");
    expect(await tag.evaluate((el) => el.tagName + ":" + el.type)).toBe("BUTTON:button");

    await tag.focus();
    await page.keyboard.press("Enter");
    const known = page.locator(".bit-imm-known");
    await expect(known).toHaveAccessibleName("会了：gardener");
    await expect(tag).toHaveAttribute("aria-expanded", "true");
    await page.keyboard.press("Tab");
    await expect(known).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(known).toHaveCount(0);
    await expect(tag).toBeFocused();

    const bar = page.locator(BAR);
    await expect(bar.locator(".bit-imm-annot-btn")).toHaveAccessibleName("注释");
    await markWord(page, "gardener");
    await expect(bar.locator(".bit-imm-undo")).toHaveAccessibleName("撤销");
    await expect(bar.locator(".bit-imm-notice")).toHaveAttribute("role", "status");
  });

  test("释义里的 HTML 只作为文字显示", async ({ page, settings, serviceWorker, upstream }) => {
    upstream.override("annotate", {
      reply: [
        "1. meticulous => 注<img src=x onerror=window.__pwned=1>",
        "2. gardener => 注<b>粗</b>",
        "3. pruned => 注",
        "4. wisteria => 注"
      ].join("\n")
    });
    await annotatePage(page, settings, serviceWorker, [P1]);
    await expect(page.locator("#t1 " + TAG).first()).toContainText("<img src=x");
    await expect(page.locator("#t1 " + TAG).nth(1)).toContainText("<b>粗</b>");
    expect(await page.locator("#t1 .bit-imm-block img, #t1 .bit-imm-block b").count()).toBe(0);
    expect(await page.evaluate(() => window.__pwned)).toBeUndefined();
  });
});
