"use strict";

// Alt+A 双语对照: 视口优先分批, 跳过中文和代码块, 滚动续翻, 仅译/模型切换, 无损还原.
const { test, expect, runCommand, requestsOf } = require("./fixtures");

const VISIBLE = {
  "h1": "Release notes",
  "#p-word": "ephemeral",
  "#p-sentence": "The expedition persevered toward the summit despite the storm.",
  "#p-second": "Every checkpoint was logged before sunrise.",
  "#li-one": "Install the unpacked extension from the build folder."
};

function pageRequests(upstream) {
  return upstream.requests.filter((r) => r.kind === "immersive" || r.kind === "translate");
}

function sentItems(upstream) {
  return pageRequests(upstream).flatMap((r) =>
    r.kind === "immersive" ? r.source.split("\n").map((line) => line.replace(/^〖P\d+〗/, "")) : [r.source]
  );
}

test.describe("双语对照", () => {
  test("Alt+A 只翻译首屏段落, 跳过中文段落和代码块, 每批不超过 4 段", async ({ page, upstream }) => {
    const before = await page.locator("body").innerHTML();
    await page.keyboard.press("Alt+KeyA");
    const bar = page.locator("#bili-index-imm-bar");

    await expect(bar).toHaveAttribute("data-open", "");
    await expect(bar.locator(".bit-imm-status")).toHaveText("双语就绪 5/6");
    for (const [selector, source] of Object.entries(VISIBLE)) {
      await expect(page.locator(selector + " > .bit-imm-block")).toHaveText("译文：" + source);
      await expect(page.locator(selector)).toHaveAttribute("data-bit-imm-ready", "1");
    }
    await expect(page.locator("#p-zh .bit-imm-block")).toHaveCount(0);
    await expect(page.locator("pre .bit-imm-block")).toHaveCount(0);
    await expect(page.locator("#p-below .bit-imm-block")).toHaveCount(0);

    expect(sentItems(upstream).sort()).toEqual(Object.values(VISIBLE).sort());
    for (const req of pageRequests(upstream)) {
      expect(req.body.model).toBe("Index-Translate-2B");
      expect((req.source.match(/〖P\d+〗/g) || []).length).toBeLessThanOrEqual(4);
    }

    await page.keyboard.press("Alt+KeyA");
    await expect(bar).not.toHaveAttribute("data-open", "");
    await expect(page.locator(".bit-imm-block")).toHaveCount(0);
    await expect(page.locator("[data-bit-imm-host]")).toHaveCount(0);
    expect(await page.locator("body").innerHTML()).toBe(before);
  });

  test("滚动到下方后续翻新进入视口的段落", async ({ page, upstream }) => {
    await page.keyboard.press("Alt+KeyA");
    const status = page.locator("#bili-index-imm-bar .bit-imm-status");
    await expect(status).toHaveText("双语就绪 5/6");
    const firstBatch = pageRequests(upstream).length;

    await page.locator("#p-below").scrollIntoViewIfNeeded();
    await expect(page.locator("#p-below > .bit-imm-block")).toHaveText("译文：This paragraph sits far below the first screen.");
    await expect(status).toHaveText("双语就绪 6/6");
    expect(pageRequests(upstream)).toHaveLength(firstBatch + 1);
  });

  test("仅译模式隐藏原文, 切到 9B 后用新模型重新翻译", async ({ page, upstream }) => {
    await page.keyboard.press("Alt+KeyA");
    const bar = page.locator("#bili-index-imm-bar");
    await expect(bar.locator(".bit-imm-status")).toHaveText("双语就绪 5/6");

    const [modeBtn, modelBtn] = await bar.locator(".bit-imm-btn").all();
    await expect(modeBtn).toHaveText("双语");
    await modeBtn.click();
    await expect(modeBtn).toHaveText("仅译");
    await expect(page.locator("html")).toHaveAttribute("data-bit-imm-mode", "target-only");
    const sizes = await page.locator("#p-sentence").evaluate((el) => ({
      host: getComputedStyle(el).fontSize,
      block: getComputedStyle(el.querySelector(".bit-imm-block")).fontSize
    }));
    expect(sizes.host).toBe("0px");
    expect(sizes.block).toBe("16px");

    await expect(modelBtn).toHaveText("2B");
    upstream.reset();
    await modelBtn.click();
    await expect(modelBtn).toHaveText("9B");
    await expect(bar.locator(".bit-imm-status")).toHaveText("双语就绪 5/6");
    expect(new Set(pageRequests(upstream).map((r) => r.body.model))).toEqual(new Set(["Index-Translate-9B"]));

    await bar.locator(".bit-imm-close").click();
    await expect(page.locator("html")).not.toHaveAttribute("data-bit-imm-mode", /.*/);
    await expect(page.locator(".bit-imm-block")).toHaveCount(0);
  });

  test("再次开启时已译段落全部命中缓存, 不再请求上游", async ({ page, upstream }) => {
    await page.keyboard.press("Alt+KeyA");
    const status = page.locator("#bili-index-imm-bar .bit-imm-status");
    await expect(status).toHaveText("双语就绪 5/6");
    const count = upstream.requests.length;

    await page.keyboard.press("Alt+KeyA");
    await page.keyboard.press("Alt+KeyA");
    await expect(status).toHaveText("双语就绪 5/6");
    await expect(page.locator("#p-sentence > .bit-imm-block")).toHaveText("译文：" + VISIBLE["#p-sentence"]);
    expect(upstream.requests).toHaveLength(count);
  });

  test("上游出错的批次标出 HTTP 状态, 其余批次正常", async ({ page, upstream }) => {
    upstream.override("immersive", { status: 500, body: "boom" });
    await page.keyboard.press("Alt+KeyA");
    const status = page.locator("#bili-index-imm-bar .bit-imm-status");
    await expect(status).toHaveText("双语就绪 5/6");

    const failed = page.locator(".bit-imm-block", { hasText: "[HTTP 500]" });
    const ok = page.locator(".bit-imm-block", { hasText: "译文：" });
    expect((await failed.count()) + (await ok.count())).toBe(5);
    expect(await failed.count()).toBeGreaterThan(0);
    expect(await ok.count()).toBeGreaterThan(0);
  });

  test("扩展快捷键命令和页面右键菜单都能切换双语对照", async ({ page, serviceWorker }) => {
    const bar = page.locator("#bili-index-imm-bar");
    await runCommand(serviceWorker, "toggle-immersive");
    await expect(bar).toHaveAttribute("data-open", "");
    await expect(bar.locator(".bit-imm-status")).toHaveText("双语就绪 5/6");
    await runCommand(serviceWorker, "toggle-immersive");
    await expect(bar).not.toHaveAttribute("data-open", "");

    await serviceWorker.evaluate(async (url) => {
      const tab = (await chrome.tabs.query({})).find((t) => t.url === url);
      chrome.contextMenus.onClicked.dispatch({ menuItemId: "bili-immersive-page", frameId: 0 }, tab);
    }, page.url());
    await expect(bar).toHaveAttribute("data-open", "");
  });
});