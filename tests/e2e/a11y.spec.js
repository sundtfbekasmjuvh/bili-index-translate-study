"use strict";

// 无障碍: 读屏播报 (aria-live / aria-busy), 焦点不抢不丢, 减少动态效果, 可访问名字, 键盘焦点轮廓.
const { test, expect, dragSelect } = require("./fixtures");

const SENTENCE = "The expedition persevered toward the summit despite the storm.";
const MAIN = "#bili-index-translate-panel";
const STUDY = "#bili-index-study-panel";

test.describe("无障碍", () => {
  test("翻译面板: 流式期间 aria-busy=true, 完成后 false, 始终 aria-live=polite", async ({ page, upstream }) => {
    const release = upstream.hold("translate");
    const body = page.locator(MAIN + " .bit-body");
    await dragSelect(page, "#p-sentence");
    await expect(page.locator(MAIN + " .bit-foot")).toContainText("流式输出中");
    await expect(body).toHaveAttribute("aria-busy", "true");
    await expect(body).toHaveAttribute("aria-live", "polite");

    release();
    await expect(body).toHaveText("译文：" + SENTENCE);
    await expect(body).toHaveAttribute("aria-busy", "false");
    await expect(body).toHaveAttribute("aria-live", "polite");
  });

  test("上游 400 时显示错误后 aria-busy=false", async ({ page, upstream }) => {
    upstream.override("translate", { status: 400, body: "bad request" });
    await dragSelect(page, "#p-sentence");
    const body = page.locator(MAIN + " .bit-body");
    await expect(body.locator(".bit-error")).toContainText("400");
    await expect(body).toHaveAttribute("aria-busy", "false");
    await expect(body).toHaveAttribute("aria-live", "polite");
  });

  test("划词打开面板不抢焦点, 选区保留", async ({ page }) => {
    await dragSelect(page, "#p-sentence");
    await expect(page.locator(MAIN + " .bit-body")).toHaveText("译文：" + SENTENCE);
    const state = await page.evaluate((sel) => ({
      inPanel: document.querySelector(sel).contains(document.activeElement),
      selection: getSelection().toString()
    }), MAIN);
    expect(state.inPanel).toBe(false);
    expect(state.selection.length).toBeGreaterThan(0);
  });

  test("关闭面板时焦点回到打开前的元素", async ({ page }) => {
    // A mouse drag would blur the page's own control, so the selection is made in script and the
    // mouseup is dispatched while the control still has focus.
    await page.evaluate(() => {
      const b = document.createElement("button");
      b.id = "page-btn";
      b.textContent = "page button";
      document.body.prepend(b);
      b.focus();
      const p = document.getElementById("p-sentence");
      const range = document.createRange();
      range.selectNodeContents(p);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      const r = p.getBoundingClientRect();
      p.dispatchEvent(
        new MouseEvent("mouseup", { bubbles: true, button: 0, clientX: r.left + 5, clientY: r.top + 5 })
      );
    });
    const panel = page.locator(MAIN);
    await expect(panel.locator(".bit-body")).toHaveText("译文：" + SENTENCE);
    await expect(page.locator("#page-btn")).toBeFocused();

    await panel.locator(".bit-close").focus();
    expect(await page.evaluate((sel) => document.querySelector(sel).contains(document.activeElement), MAIN)).toBe(true);
    await page.keyboard.press("Escape");
    await expect(panel).not.toHaveAttribute("data-open", "");
    await expect(page.locator("#page-btn")).toBeFocused();
  });

  test("减少动态效果: 加载骨架和沉浸式加载标记不再有动画", async ({ page, upstream }) => {
    const names = () =>
      page.evaluate(() => {
        const name = (sel) => {
          const el = document.querySelector(sel);
          return el ? getComputedStyle(el).animationName : null;
        };
        return { skel: name("#bili-index-study-panel .bit-skel-bar"), imm: name(".bit-imm-block[data-loading]") };
      });
    const release = upstream.hold("study");
    const releaseImm = upstream.hold("immersive");
    await dragSelect(page, "#p-sentence");
    await expect(page.locator(MAIN + " .bit-foot")).toContainText("S 词卡");
    await page.keyboard.press("s");
    await expect(page.locator(STUDY + " .bit-skel-bar").first()).toBeVisible();
    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator(".bit-imm-block[data-loading]").first()).toBeAttached();

    await page.emulateMedia({ reducedMotion: "no-preference" });
    const normal = await names();
    expect(normal.skel).not.toBe("none");
    expect(normal.imm).not.toBe("none");

    await page.emulateMedia({ reducedMotion: "reduce" });
    const reduced = await names();
    expect(reduced.skel).toBe("none");
    expect(reduced.imm).toBe("none");
    release();
    releaseImm();
  });

  test("键盘聚焦面板按钮时有可见轮廓", async ({ page }) => {
    await dragSelect(page, "#p-sentence");
    await expect(page.locator(MAIN + " .bit-body")).toHaveText("译文：" + SENTENCE);
    await page.evaluate(() => getSelection().removeAllRanges());
    const btn = page.locator(MAIN + " .bit-close");
    await btn.focus();
    // A real Tab press makes :focus-visible apply; start from the previous control.
    await page.locator(MAIN + " .bit-study-trigger").focus();
    await page.keyboard.press("Tab");
    await expect(btn).toBeFocused();
    // The browser's own focus ring is "auto 1px"; these values come only from the rule in content.css.
    const outline = await btn.evaluate((el) => {
      const cs = getComputedStyle(el);
      return [cs.outlineStyle, cs.outlineWidth, cs.outlineColor, cs.outlineOffset].join(" ");
    });
    expect(outline).toBe("solid 2px rgb(37, 99, 235) 2px");
  });

  test("学习卡片: aria-live=polite, 加载中 aria-busy=true, 完成后 false", async ({ page, upstream }) => {
    const release = upstream.hold("study");
    await dragSelect(page, "#p-sentence");
    await expect(page.locator(MAIN + " .bit-foot")).toContainText("S 词卡");
    await page.keyboard.press("s");
    const body = page.locator(STUDY + " .bit-study-body");
    await expect(page.locator(STUDY + " .bit-skel-bar").first()).toBeVisible();
    await expect(body).toHaveAttribute("aria-busy", "true");
    await expect(body).toHaveAttribute("aria-live", "polite");

    release();
    await expect(page.locator(STUDY + " .bit-term").first()).toBeVisible();
    await expect(body).toHaveAttribute("aria-busy", "false");
    await expect(body).toHaveAttribute("aria-live", "polite");
  });

  test("两个面板和只有图标的按钮都有非空的可访问名字", async ({ page }) => {
    await dragSelect(page, "#p-sentence");
    await expect(page.locator(MAIN + " .bit-foot")).toContainText("S 词卡");
    await page.keyboard.press("s");
    await expect(page.locator(STUDY + " .bit-term").first()).toBeVisible();
    await page.keyboard.press("Alt+KeyA");
    await expect(page.locator("#bili-index-imm-bar")).toHaveAttribute("data-open", "");

    const nameless = await page.evaluate(() => {
      const bad = [];
      const nameOf = (el) =>
        (el.getAttribute("aria-label") || "").trim() || (el.textContent || "").trim() || (el.title || "").trim();
      for (const id of ["bili-index-translate-panel", "bili-index-study-panel"]) {
        const root = document.getElementById(id);
        if (!(root.getAttribute("aria-label") || "").trim()) bad.push(id);
        for (const b of root.querySelectorAll("button")) if (!(b.getAttribute("aria-label") || "").trim() && !(b.textContent || "").trim()) bad.push(id + " " + b.className);
      }
      for (const b of document.querySelectorAll("#bili-index-imm-bar button"))
        if (!nameOf(b) || !((b.getAttribute("aria-label") || "").trim() || (b.textContent || "").trim())) bad.push("imm " + b.className);
      return bad;
    });
    expect(nameless).toEqual([]);

    // Buttons that have a tooltip also expose it as their name.
    const titled = await page.evaluate(() =>
      [...document.querySelectorAll("#bili-index-translate-panel button[title], #bili-index-study-panel .bit-icon-btn[title], #bili-index-imm-bar .bit-imm-close")]
        .filter((b) => !(b.textContent || "").trim() || b.classList.contains("bit-imm-close"))
        .map((b) => [b.title, b.getAttribute("aria-label")])
    );
    expect(titled.length).toBeGreaterThan(0);
    for (const [title, label] of titled) expect(label).toBeTruthy();
  });
});
