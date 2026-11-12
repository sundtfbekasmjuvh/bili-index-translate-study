"use strict";

// 零 OCR 识图: Alt+S 框选截图与裁剪, 〖译文〗/〖原文〗双轨流式, 右键图片, 缓存, 从识图原文打开学习卡.
const { test, expect, clickContextMenu, runCommand, requestsOf } = require("./fixtures");
const { pngSize, VISION_SOURCE, VISION_TRANSLATION } = require("../support/mock-upstream");

async function snip(page, from, to) {
  await page.keyboard.press("Alt+KeyS");
  await expect(page.locator("#bili-index-snip-mask")).toBeVisible();
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 6 });
  await page.mouse.up();
  await expect(page.locator("#bili-index-snip-mask")).toHaveCount(0);
}

async function snipSample(page) {
  const box = await page.locator("#sample").boundingBox();
  await snip(page, { x: box.x + 10, y: box.y + 10 }, { x: box.x + 210, y: box.y + 110 });
}

function imageOf(request) {
  return request.body.messages[0].content.find((part) => part.type === "image_url").image_url.url;
}

// Gray levels down the middle column of the cropped capture, sampled at the centre of each stripe.
function stripeLevels(page, dataUrl) {
  return page.evaluate(async (url) => {
    const img = new Image();
    img.src = url;
    await img.decode();
    const canvas = document.createElement("canvas");
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const ctx = canvas.getContext("2d");
    ctx.drawImage(img, 0, 0);
    const out = [];
    for (const y of [14, 30, 46, 62, 78, 94]) {
      const [r, g, b] = ctx.getImageData(100, y, 1, 1).data;
      out.push([r, g, b]);
    }
    return out;
  }, dataUrl);
}

test.describe("识图直译", () => {
  test("Alt+S 框选图片区域: 只上传裁剪后的区域, 截图中没有遮罩, 先出译文再回填原文", async ({ page, upstream }) => {
    await snipSample(page);
    const panel = page.locator("#bili-index-translate-panel");

    await expect(panel.locator(".bit-body > div").first()).toHaveText(VISION_TRANSLATION);
    await expect(panel.locator(".bit-vision-src")).toHaveText("原文" + VISION_SOURCE);
    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-35B-A3B · 识图 · directS 词卡");

    const [req] = requestsOf(upstream, "vision");
    expect(req.body).toMatchObject({ model: "Index-Translate-35B-A3B", stream: true, max_tokens: 1024 });
    expect(req.body.messages[0].content[0].text).toContain("〖译文〗");
    expect(pngSize(imageOf(req))).toEqual({ width: 200, height: 100 });

    const levels = await stripeLevels(page, imageOf(req));
    const expected = [238, 34, 238, 34, 238, 34];
    levels.forEach(([r, g, b], i) => {
      expect(Math.abs(r - expected[i]), "stripe " + i + " " + [r, g, b]).toBeLessThanOrEqual(6);
      expect(Math.abs(g - expected[i])).toBeLessThanOrEqual(6);
      expect(Math.abs(b - expected[i])).toBeLessThanOrEqual(6);
    });
  });

  test("〖原文〗到达前只显示译文, 学习卡入口在原文回填后才可用", async ({ page, upstream }) => {
    const release = upstream.hold("vision");
    await snipSample(page);
    const panel = page.locator("#bili-index-translate-panel");

    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-35B-A3B · 识图流式中…");
    await expect(panel.locator(".bit-body")).toHaveText(VISION_TRANSLATION);
    await expect(panel.locator(".bit-vision-src")).toHaveCount(0);
    await expect(panel.locator(".bit-study-trigger")).toHaveAttribute("data-disabled", "");

    release();
    await expect(panel.locator(".bit-vision-src")).toHaveText("原文" + VISION_SOURCE);
    await expect(panel.locator(".bit-study-trigger")).not.toHaveAttribute("data-disabled", "");
  });

  test("识图完成后按 S 用回填的原文生成学习卡片", async ({ page, upstream }) => {
    await snipSample(page);
    await expect(page.locator("#bili-index-translate-panel .bit-foot")).toContainText("S 词卡");

    await page.keyboard.press("s");
    const study = page.locator("#bili-index-study-panel");
    await expect(study.locator(".bit-word-chip")).toHaveText(["1. exponential", "2. scheduler", "3. retries"]);
    const [req] = requestsOf(upstream, "study");
    expect(req.source).toBe(VISION_SOURCE);
  });

  test("Esc 和过小的框选都会取消, 不发请求", async ({ page, upstream }) => {
    await page.keyboard.press("Alt+KeyS");
    await expect(page.locator("#bili-index-snip-mask")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.locator("#bili-index-snip-mask")).toHaveCount(0);

    await snip(page, { x: 300, y: 300 }, { x: 310, y: 340 });
    await page.waitForTimeout(300);
    await expect(page.locator("#bili-index-translate-panel")).toHaveCount(0);
    expect(upstream.requests).toHaveLength(0);
  });

  test("右键图片菜单上传原图, 同一张图第二次命中缓存", async ({ page, upstream, serviceWorker }) => {
    const panel = page.locator("#bili-index-translate-panel");
    const info = { menuItemId: "bili-translate-image", srcUrl: upstream.origin + "/sample.png" };

    await page.locator("#sample").dispatchEvent("contextmenu");
    await clickContextMenu(serviceWorker, page, info);
    await expect(panel.locator(".bit-vision-src")).toHaveText("原文" + VISION_SOURCE);
    const [req] = requestsOf(upstream, "vision");
    expect(imageOf(req).startsWith("data:image/png;base64,")).toBe(true);
    expect(pngSize(imageOf(req))).toEqual({ width: 64, height: 32 });

    const img = await page.locator("#sample").boundingBox();
    const box = await panel.boundingBox();
    expect(box.y).toBeGreaterThanOrEqual(img.y + img.height);

    await page.keyboard.press("Escape");
    await clickContextMenu(serviceWorker, page, info);
    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-35B-A3B · 识图 · ⚡瞬时S 词卡");
    expect(requestsOf(upstream, "vision")).toHaveLength(1);
  });

  test("页面上还没有面板时图片下载失败, 也会打开面板显示错误, 不调用模型", async ({ page, upstream, serviceWorker }) => {
    const pageErrors = [];
    page.on("console", (msg) => msg.type() === "error" && pageErrors.push(msg.text()));
    await clickContextMenu(serviceWorker, page, {
      menuItemId: "bili-translate-image",
      srcUrl: upstream.origin + "/missing.png"
    });
    await expect(page.locator("#bili-index-translate-panel")).toHaveAttribute("data-open", "");
    await expect(page.locator("#bili-index-translate-panel .bit-error")).toHaveText(
      "Request failed\nFailed to fetch image (HTTP 404)."
    );
    expect(requestsOf(upstream, "vision")).toHaveLength(0);
    expect(pageErrors).toEqual([]);
  });

  // 声明了大小的下载在收到响应头时就放弃, 所以服务端发出的字节远不到 8 MB; 分块传输的要读过 8 MB 才能发现.
  const MB = 1024 * 1024;
  for (const [name, query, sentBelow] of [
    ["响应头声明了大小", "?declared=1", 8 * MB],
    ["分块传输, 没有 Content-Length", "", 32 * MB]
  ]) {
    test("右键超过 8 MB 的图片 (" + name + "): 不把整张图下载完, 提示改用框选, 不调用模型", async ({ page, upstream, serviceWorker }) => {
      await clickContextMenu(serviceWorker, page, {
        menuItemId: "bili-translate-image",
        srcUrl: upstream.origin + "/huge.png" + query
      });
      await expect(page.locator("#bili-index-translate-panel .bit-error")).toHaveText(
        "Request failed\nImage exceeds 8 MB limit; use Alt+S region snip instead."
      );
      await expect.poll(() => upstream.downloads.map((d) => d.closedEarly)).toEqual([true]);
      expect(upstream.downloads[0].sent).toBeLessThan(sentBelow);
      expect(requestsOf(upstream, "vision")).toHaveLength(0);
    });
  }

  test("纯文本模型被配置为识图模型时自动改用 35B-A3B; 快捷键命令也能启动框选", async ({ page, upstream, settings, serviceWorker }) => {
    await settings({ sync: { visionModel: "Index-Nailong-9B" } });
    await runCommand(serviceWorker, "snip-image-translate");
    await expect(page.locator("#bili-index-snip-mask")).toBeVisible();
    await page.keyboard.press("Escape");

    await snipSample(page);
    await expect(page.locator("#bili-index-translate-panel .bit-vision-src")).toBeVisible();
    expect(requestsOf(upstream, "vision").map((r) => r.body.model)).toEqual(["Index-Translate-35B-A3B"]);
  });
});