"use strict";

// Alt+A 双语对照: 视口优先分批, 跳过中文和代码块, 滚动续翻, 仅译/模型切换, 无损还原.
const { test, expect, runCommand, requestsOf, openWithExtension } = require("./fixtures");

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

  test("很长的页面: 跳到页尾, 再跳回中部, 新进入视口的段落都会续翻, 远处的段落不翻", async ({ page, serviceWorker, upstream }) => {
    await openWithExtension(page, serviceWorker, upstream.origin + "/long.html");
    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator("#p0 > .bit-imm-block")).toHaveText("译文：Entry 0 records one change made to the scheduler.");

    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await expect(page.locator("#p899 > .bit-imm-block")).toHaveText("译文：Entry 899 records one change made to the scheduler.");

    await page.locator("#p450").evaluate((el) => el.scrollIntoView({ block: "center" }));
    await expect(page.locator("#p450 > .bit-imm-block")).toHaveText("译文：Entry 450 records one change made to the scheduler.");

    // 状态条数的是页面上符合条件的全部段落 (900 段加标题); 没到过的位置不请求.
    await expect(page.locator("#bili-index-imm-bar .bit-imm-status")).toHaveText(/双语就绪 \d+\/901/);
    await expect(page.locator("#p225 > .bit-imm-block")).toHaveCount(0);
    await expect(page.locator("#p700 > .bit-imm-block")).toHaveCount(0);
    expect(sentItems(upstream)).not.toContain("Entry 225 records one change made to the scheduler.");
  });

  test("页面不停变动时, 新出现的段落仍会在两秒左右被扫描并翻译", async ({ page }) => {
    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator("#bili-index-imm-bar .bit-imm-status")).toHaveText("双语就绪 5/6");

    // 每 100 ms 加一个节点, 比等待页面安静的 260 ms 短: 只等安静的话永远等不到.
    await page.evaluate(() => {
      window.__ticker = setInterval(() => {
        const tick = document.createElement("div");
        tick.className = "tick";
        document.body.appendChild(tick);
      }, 100);
      const late = document.createElement("p");
      late.id = "p-late";
      late.textContent = "A late paragraph arrives while the page keeps changing.";
      document.querySelector("h1").after(late);
    });
    await expect(page.locator("#p-late > .bit-imm-block")).toHaveText(
      "译文：A late paragraph arrives while the page keeps changing.",
      { timeout: 5000 }
    );
    expect(await page.evaluate(() => document.querySelectorAll(".tick").length)).toBeGreaterThan(5);
    await page.evaluate(() => clearInterval(window.__ticker));
  });

  test("页面删掉已翻译的段落后, 状态条的计数随之减少", async ({ page }) => {
    await page.keyboard.press("Alt+KeyA");
    const status = page.locator("#bili-index-imm-bar .bit-imm-status");
    await expect(status).toHaveText("双语就绪 5/6");

    await page.evaluate(() => document.querySelector("#p-second").remove());
    await expect(status).toHaveText("双语就绪 4/5");
  });

  test("页面改了已翻译段落的文字: 译文按新原文重译; 改成中文后译文撤掉, 计数减少", async ({ page, upstream }) => {
    await page.keyboard.press("Alt+KeyA");
    const status = page.locator("#bili-index-imm-bar .bit-imm-status");
    await expect(status).toHaveText("双语就绪 5/6");
    await expect(page.locator("#p-second > .bit-imm-block")).toHaveText("译文：Every checkpoint was logged before sunrise.");

    // 原地改文本节点 (单页应用更新内容的常见方式): 旧译文不能留在新原文下面.
    await page.evaluate(() => {
      document.querySelector("#p-second").firstChild.nodeValue = "Every checkpoint was logged after sunset.";
    });
    await expect(page.locator("#p-second > .bit-imm-block")).toHaveText("译文：Every checkpoint was logged after sunset.");
    await expect(page.locator("#p-second > .bit-imm-block")).toHaveCount(1);
    await expect(status).toHaveText("双语就绪 5/6");
    expect(sentItems(upstream)).toContain("Every checkpoint was logged after sunset.");

    await page.evaluate(() => {
      document.querySelector("#p-word").firstChild.nodeValue = "这一段现在已经是中文了，不需要翻译。";
    });
    await expect(page.locator("#p-word > .bit-imm-block")).toHaveCount(0);
    await expect(page.locator("#p-word")).not.toHaveAttribute("data-bit-imm-host", "1");
    await expect(status).toHaveText("双语就绪 4/5");
  });

  test("页面用同样的文字重建了段落 (译文被一并抹掉): 译文放回去, 不重新请求; 页面反复抹掉时最多放回 3 次", async ({ page, upstream }) => {
    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator("#bili-index-imm-bar .bit-imm-status")).toHaveText("双语就绪 5/6");
    const sent = pageRequests(upstream).length;

    await page.evaluate(() => {
      document.querySelector("#p-word").textContent = "ephemeral";
    });
    await expect(page.locator("#p-word > .bit-imm-block")).toHaveCount(0);
    await expect(page.locator("#p-word > .bit-imm-block")).toHaveText("译文：ephemeral");

    // 这个段落每次出现译文就被页面重建一遍.
    await page.evaluate(() => {
      const el = document.querySelector("#li-one");
      const text = el.firstChild.nodeValue;
      window.__rebuilds = 0;
      const rebuild = () => {
        window.__rebuilds++;
        el.textContent = text;
      };
      new MutationObserver(() => {
        if (el.querySelector(".bit-imm-block")) rebuild();
      }).observe(el, { childList: true });
      rebuild();
    });
    await expect.poll(() => page.evaluate(() => window.__rebuilds)).toBe(4);
    await page.waitForTimeout(1200);
    expect(await page.evaluate(() => window.__rebuilds)).toBe(4);
    await expect(page.locator("#li-one > .bit-imm-block")).toHaveCount(0);
    expect(pageRequests(upstream).length).toBe(sent);
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
    // 500 会自动重试 (共 3 次), 所以让同一批次连续失败 3 次才算最终出错.
    upstream.override("immersive", { status: 500, body: "boom", times: 3, match: (r) => r.source.includes(VISIBLE["#p-word"]) });
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