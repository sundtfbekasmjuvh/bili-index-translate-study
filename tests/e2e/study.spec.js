"use strict";

// 伴随学习卡片: S 键开合, 单词/句子两种模式, 卡片切换与自测, M 键切换 35B, 右键菜单入口, 缓存.
const { test, expect, dragSelect, clickContextMenu, requestsOf } = require("./fixtures");

const SENTENCE = "The expedition persevered toward the summit despite the storm.";

async function translateSelection(page, selector) {
  await dragSelect(page, selector);
  await expect(page.locator("#bili-index-translate-panel .bit-foot")).toContainText("S 词卡");
}

test.describe("学习卡片", () => {
  test("单词模式: S 打开 1 张卡, 内容来自上游 JSON, 再按 S 收起", async ({ page, upstream }) => {
    await translateSelection(page, "#p-word");
    const study = page.locator("#bili-index-study-panel");

    await page.keyboard.press("s");
    await expect(study).toHaveAttribute("data-open", "");
    await expect(study.locator(".bit-term")).toHaveText("ephemeral");
    await expect(study.locator(".bit-meaning-box")).toHaveText("ephemeral 的释义");
    await expect(study.locator(".bit-reading")).toHaveText("/ephemeral/");
    await expect(study.locator(".bit-example-tgt")).toHaveText("含有 ephemeral 的例句。");
    await expect(study.locator(".bit-lang-badge")).toHaveText("EN-US");
    await expect(study.locator(".bit-word-chip")).toHaveCount(0);
    await expect(study.locator(".bit-grammar-box")).toHaveCount(0);

    const [req] = requestsOf(upstream, "study");
    expect(req.body).toMatchObject({ model: "Index-Translate-9B", stream: true, max_tokens: 1100 });
    expect(req.source).toBe("ephemeral");

    await page.keyboard.press("s");
    await expect(study).not.toHaveAttribute("data-open", "");
    await expect(page.locator("#bili-index-translate-panel")).toHaveAttribute("data-open", "");
  });

  test("句子模式: 显示句法说明和 3 张卡, 数字键和方向键切换, F 遮住释义后可揭晓", async ({ page }) => {
    await translateSelection(page, "#p-sentence");
    const study = page.locator("#bili-index-study-panel");
    await page.keyboard.press("s");

    await expect(study.locator(".bit-grammar-box")).toContainText("主谓宾结构的陈述句。");
    await expect(study.locator(".bit-word-chip")).toHaveText(["1. expedition", "2. persevered", "3. despite"]);
    await expect(study.locator(".bit-term")).toHaveText("expedition");
    await expect(study.locator(".bit-study-foot")).toContainText("1/3");

    await page.keyboard.press("3");
    await expect(study.locator(".bit-term")).toHaveText("despite");
    await page.keyboard.press("ArrowRight");
    await expect(study.locator(".bit-term")).toHaveText("expedition");
    await page.keyboard.press("ArrowLeft");
    await expect(study.locator(".bit-term")).toHaveText("despite");
    await expect(study.locator(".bit-study-foot")).toContainText("3/3");

    await page.keyboard.press("f");
    await expect(study.locator(".bit-meaning-box")).toHaveAttribute("data-masked", "");
    await expect(study.locator(".bit-example-tgt")).toHaveCount(0);
    await page.keyboard.press("f");
    await expect(study.locator(".bit-meaning-box")).toHaveText("despite 的释义");
    await expect(study.locator(".bit-meaning-box")).not.toHaveAttribute("data-masked", "");
  });

  test("M 键改用 35B-A3B 重新拆解, 同一句再次打开命中缓存", async ({ page, upstream }) => {
    await translateSelection(page, "#p-word");
    const study = page.locator("#bili-index-study-panel");
    await page.keyboard.press("s");
    // Wait for DONE: closing the card earlier cancels the stream and nothing is cached.
    await expect(study.locator(".bit-study-foot")).toContainText("9B · direct");

    await page.keyboard.press("m");
    await expect(study.locator(".bit-study-foot")).toContainText("35B-A3B · direct");
    await expect.poll(() => requestsOf(upstream, "study").map((r) => r.body.model)).toEqual([
      "Index-Translate-9B",
      "Index-Translate-35B-A3B"
    ]);

    await page.keyboard.press("s");
    await page.keyboard.press("s");
    await expect(study.locator(".bit-study-foot")).toContainText("⚡瞬时");
    expect(requestsOf(upstream, "study")).toHaveLength(2);

    await page.keyboard.press("m");
    await expect(study.locator(".bit-study-foot")).toContainText("9B");
    await expect(study.locator(".bit-study-foot")).toContainText("⚡瞬时");
    expect(requestsOf(upstream, "study")).toHaveLength(2);
  });

  test("右键菜单的学习卡片入口同时打开译文面板和卡片", async ({ page, upstream, serviceWorker }) => {
    await page.evaluate(() => {
      const range = document.createRange();
      range.selectNodeContents(document.querySelector("#li-one"));
      getSelection().removeAllRanges();
      getSelection().addRange(range);
    });
    await clickContextMenu(serviceWorker, page, {
      menuItemId: "bili-study-selection",
      selectionText: "Install the unpacked extension from the build folder."
    });

    await expect(page.locator("#bili-index-translate-panel .bit-body")).toHaveText(
      "译文：Install the unpacked extension from the build folder."
    );
    await expect(page.locator("#bili-index-study-panel .bit-word-chip")).toHaveText([
      "1. extension",
      "2. unpacked",
      "3. install"
    ]);
    expect(requestsOf(upstream, "study")).toHaveLength(1);
    expect(requestsOf(upstream, "translate")).toHaveLength(1);
  });

  test("上游返回错误时卡片显示 HTTP 状态, 点重试后恢复", async ({ page, upstream }) => {
    upstream.override("study", { status: 503, body: "busy" });
    await translateSelection(page, "#p-word");
    const study = page.locator("#bili-index-study-panel");
    await page.keyboard.press("s");

    await expect(study.locator(".bit-error")).toContainText("HTTP 503");
    await study.locator(".bit-retry-btn").click();
    await expect(study.locator(".bit-term")).toHaveText("ephemeral");
    expect(requestsOf(upstream, "study")).toHaveLength(2);
  });

  test("Esc 关闭两个面板并断开学习卡流", async ({ page, upstream }) => {
    const release = upstream.hold("study");
    await translateSelection(page, "#p-sentence");
    await page.keyboard.press("s");
    const study = page.locator("#bili-index-study-panel");
    await expect(study).toHaveAttribute("data-open", "");
    await expect.poll(() => requestsOf(upstream, "study").length).toBe(1);

    await page.keyboard.press("Escape");
    await expect(study).not.toHaveAttribute("data-open", "");
    await expect(page.locator("#bili-index-translate-panel")).not.toHaveAttribute("data-open", "");
    const [req] = requestsOf(upstream, "study");
    await expect.poll(() => req.aborted).toBe(true);
    expect(req.source).toBe(SENTENCE);
    release();
  });
});