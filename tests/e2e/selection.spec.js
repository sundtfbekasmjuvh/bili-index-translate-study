"use strict";

// 划词翻译: 流式上屏, 缓存, 取消, 术语表注入, 412 不自动改走中继, 显式中继.
const { test, expect, dragSelect, requestsOf } = require("./fixtures");

const SENTENCE = "The expedition persevered toward the summit despite the storm.";

test.describe("划词翻译", () => {
  test("拖选后流式显示译文, 页脚标出模型和线路, 再次划同一句命中内存缓存", async ({ page, upstream }) => {
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");

    await expect(panel).toHaveAttribute("data-open", "");
    await expect(panel.locator(".bit-body")).toHaveText("译文：" + SENTENCE);
    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-9B · directS 词卡");

    const [req] = requestsOf(upstream, "translate");
    expect(req.path).toBe("/v1/chat/completions");
    expect(req.authorization).toBe("Bearer e2e-key");
    expect(req.body).toMatchObject({ model: "Index-Translate-9B", stream: true, chat_template_kwargs: { enable_thinking: false } });
    expect(req.source).toBe(SENTENCE);

    await page.keyboard.press("Escape");
    await expect(panel).not.toHaveAttribute("data-open", "");

    await dragSelect(page, "#p-sentence");
    await expect(panel.locator(".bit-foot")).toContainText("⚡瞬时");
    await expect(panel.locator(".bit-body")).toHaveText("译文：" + SENTENCE);
    expect(upstream.requests).toHaveLength(1);
  });

  test("上游流未结束时先显示已到达的部分, 结束后替换为完整译文", async ({ page, upstream }) => {
    const release = upstream.hold("translate");
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");

    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-9B · 流式输出中…");
    const partial = await panel.locator(".bit-body").textContent();
    expect(partial.length).toBeGreaterThan(0);
    expect(("译文：" + SENTENCE).startsWith(partial)).toBe(true);
    expect(partial).not.toBe("译文：" + SENTENCE);

    release();
    await expect(panel.locator(".bit-body")).toHaveText("译文：" + SENTENCE);
    await expect(panel.locator(".bit-foot")).toContainText("direct");
  });

  test("流式过程中改划另一段会取消上一条上游请求", async ({ page, upstream }) => {
    const release = upstream.hold("translate", (r) => r.source === SENTENCE);
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");
    await expect(panel.locator(".bit-foot")).toContainText("流式输出中");

    // #p-word sits above the panel, so the press lands on the page rather than on the panel.
    await dragSelect(page, "#p-word");
    await expect(panel.locator(".bit-body")).toHaveText("译文：ephemeral");

    const first = upstream.requests.find((r) => r.source === SENTENCE);
    await expect.poll(() => first.aborted).toBe(true);
    expect(first.completed).toBe(false);
    release();
  });

  test("术语表只注入本句命中的条目, 页脚显示命中数", async ({ page, upstream, settings }) => {
    await settings({ local: { glossary: "expedition => 远征队\nsunrise => 日出\nFlash-Attention" } });
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");

    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-9B · 术语×1 · directS 词卡");
    const [req] = requestsOf(upstream, "translate");
    const content = req.body.messages[0].content;
    expect(content).toContain("【约束要求】");
    expect(content).toContain("expedition→远征队");
    expect(content).not.toContain("日出");
    expect(content).not.toContain("Flash-Attention");
  });

  test("直连返回 412 时只请求一次, 显示错误和手动设置中继的提示", async ({ page, upstream }) => {
    upstream.override("translate", { status: 412, body: "<html>precondition failed</html>" });
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");

    const error = panel.locator(".bit-error");
    await expect(error).toContainText("HTTP 412");
    await expect(error).toContainText("precondition failed");
    await expect(error).toContainText("扩展不会自动切换线路");
    await expect(panel.locator(".bit-foot")).toHaveText("via direct");

    await page.waitForTimeout(300);
    expect(upstream.requests.map((r) => r.path)).toEqual(["/v1/chat/completions"]);
  });

  test("填写中继前缀后请求只发往中继, 中继 412 时提示检查中继设置", async ({ page, upstream, settings }) => {
    await settings({ sync: { proxy: upstream.origin + "/relay/v1" } });
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");

    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-9B · relayS 词卡");
    expect(upstream.requests.map((r) => r.path)).toEqual(["/relay/v1/chat/completions"]);

    upstream.override("translate", { status: 412, body: "relay says no" });
    await page.keyboard.press("Escape");
    await dragSelect(page, "#p-second");
    await expect(panel.locator(".bit-error")).toContainText("中继也被拒绝");
    await expect(panel.locator(".bit-foot")).toHaveText("via relay");
    expect(upstream.requests.map((r) => r.path)).toEqual(["/relay/v1/chat/completions", "/relay/v1/chat/completions"]);
  });

  test("在面板外按下鼠标会关闭面板, 少于 2 个字符的选区不触发翻译", async ({ page, upstream }) => {
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");
    await expect(panel.locator(".bit-foot")).toContainText("direct");

    await page.mouse.click(900, 700);
    await expect(panel).not.toHaveAttribute("data-open", "");

    await page.evaluate(() => {
      const node = document.querySelector("#p-word").firstChild;
      const range = document.createRange();
      range.setStart(node, 0);
      range.setEnd(node, 1);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
    });
    const box = await page.locator("#p-word").boundingBox();
    await page.mouse.move(box.x + 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + 6, box.y + box.height / 2);
    await page.mouse.up();
    await page.waitForTimeout(200);
    await expect(panel).not.toHaveAttribute("data-open", "");
    expect(upstream.requests).toHaveLength(1);
  });
});