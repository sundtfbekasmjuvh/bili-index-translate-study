"use strict";

// 界面语言: 英文界面下的面板, 学习卡片, 双语对照条和选项页; 在选项页切换语言; 已打开的页面跟随设置变化; "自动"跟随浏览器语言.
const { test, expect, dragSelect, requestsOf } = require("./fixtures");

const SENTENCE = "The expedition persevered toward the summit despite the storm.";

async function openOptions(context, extensionId) {
  const page = await context.newPage();
  await page.goto("chrome-extension://" + extensionId + "/options.html");
  await expect(page.locator("#glossaryCount")).not.toBeEmpty();
  return page;
}

test.describe("界面语言: 英文", () => {
  test.use({ uiLang: "en" });

  test("划词面板, 学习卡片和双语对照条都是英文, 模型返回的内容不受影响", async ({ page }) => {
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");
    await expect(panel.locator(".bit-body")).toHaveText("译文：" + SENTENCE);
    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-9B · directS Study Card");
    await expect(panel.locator(".bit-study-trigger")).toHaveText("Study Card");
    await expect(panel.locator(".bit-study-trigger")).toHaveAttribute("title", "Show or hide the Study Card (S)");
    await expect(panel.locator(".bit-close")).toHaveAttribute("aria-label", "Close");

    const study = page.locator("#bili-index-study-panel");
    await page.keyboard.press("s");
    await expect(study.locator(".bit-study-title")).toContainText("Study Card");
    await expect(study.locator(".bit-grammar-box")).toContainText("Grammar");
    await expect(study.locator(".bit-study-foot")).toContainText("Space speak · F quiz · D compact");
    await expect(study.locator(".bit-pill-ctrl").first()).toHaveText("Detailed");
    await page.keyboard.press("Escape");

    await page.keyboard.press("Alt+KeyA");
    const bar = page.locator("#bili-index-imm-bar");
    await expect(bar.locator(".bit-imm-status")).toHaveText("Ready 5/6");
    const modeBtn = bar.locator(".bit-imm-btn").first();
    await expect(modeBtn).toHaveText("Bilingual");
    await modeBtn.click();
    await expect(modeBtn).toHaveText("Target only");
    await expect(bar.locator(".bit-imm-close")).toHaveAttribute("title", "Leave bilingual mode and restore the page (Alt+A)");
  });

  test("直连 412 的提示和图片下载错误是英文", async ({ page, upstream, serviceWorker }) => {
    upstream.override("translate", { status: 412, body: "blocked" });
    await dragSelect(page, "#p-sentence");
    const error = page.locator("#bili-index-translate-panel .bit-error");
    await expect(error).toContainText("HTTP 412");
    await expect(error).toContainText("Upstream rejected the direct request.");

    await page.keyboard.press("Escape");
    await serviceWorker.evaluate(
      async ({ url, srcUrl }) => {
        const tab = (await chrome.tabs.query({})).find((t) => t.url === url);
        chrome.contextMenus.onClicked.dispatch({ frameId: 0, menuItemId: "bili-translate-image", srcUrl }, tab);
      },
      { url: page.url(), srcUrl: upstream.origin + "/missing.png" }
    );
    await expect(error).toHaveText("Request failed\nFailed to fetch image (HTTP 404).");
  });

  test("选项页是英文; 改选简体中文后页面立即切换但不写入设置, 保存后生效, 改回英文时标记完全还原", async ({ context, extensionId, serviceWorker }) => {
    const options = await openOptions(context, extensionId);
    const status = options.locator("#status");
    const storedLang = () => serviceWorker.evaluate(async () => (await chrome.storage.sync.get("uiLang")).uiLang);
    const helpHtml = () => options.locator('[data-i18n="custom.placeholdersHelp"]').evaluate((el) => el.innerHTML);

    await expect(options.locator("html")).toHaveAttribute("lang", "en");
    await expect(options.locator("#uiLang")).toHaveValue("en");
    await expect(options.locator("#save")).toHaveText("Save");
    await expect(options.locator('label[for="base"]')).toHaveText("Base URL");
    await expect(options.locator("#glossaryCount")).toHaveText("0 rules");
    const englishHelp = await helpHtml();
    await options.locator("#save").click();
    await expect(status).toHaveText("Saved.");

    await options.locator("#uiLang").selectOption("zh-CN");
    await expect(options.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(options).toHaveTitle("Bili Index Translate — 设置");
    await expect(options.locator("#save")).toHaveText("保存");
    await expect(options.locator("#test")).toHaveText("保存并测试");
    await expect(options.locator('label[for="base"]')).toHaveText("接口地址 (Base URL)");
    await expect(options.locator("#glossaryCount")).toHaveText("0 条规则");
    await expect(options.locator('#apiType option[value="custom"]')).toHaveText("自定义 JSON 接口（模板）");
    await expect(options.locator("#proxy")).toHaveAttribute("placeholder", "留空即直连 Bilibili 接口（已启用 MV3 DNR）");
    expect(await helpHtml()).not.toBe(englishHelp);
    expect(await storedLang()).toBe("en");

    await options.locator("#uiLang").selectOption("en");
    await expect(options).toHaveTitle("Bili Index Translate — Settings");
    await expect(options.locator("#save")).toHaveText("Save");
    expect(await helpHtml()).toBe(englishHelp);

    await options.locator("#uiLang").selectOption("zh-CN");
    await options.locator("#save").click();
    await expect(status).toHaveText("已保存。");
    expect(await storedLang()).toBe("zh-CN");

    await options.reload();
    await expect(options.locator("#uiLang")).toHaveValue("zh-CN");
    await expect(options.locator("#save")).toHaveText("保存");
    await options.close();
  });

  test("设置改变时, 已打开页面上的面板关闭并在下次用新语言重建, 双语对照条保持打开并换成新语言", async ({ page, settings, upstream }) => {
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");
    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-9B · directS Study Card");
    await page.keyboard.press("Alt+KeyA");
    const bar = page.locator("#bili-index-imm-bar");
    await expect(bar.locator(".bit-imm-status")).toHaveText("Ready 5/6");

    await settings({ sync: { uiLang: "zh-CN" } });
    await expect(panel).toHaveCount(0);
    await expect(bar).toHaveAttribute("data-open", "");
    await expect(bar.locator(".bit-imm-status")).toHaveText("双语就绪 5/6");
    await expect(bar.locator(".bit-imm-btn").first()).toHaveText("双语");
    await expect(page.locator("#p-sentence > .bit-imm-block")).toHaveText("译文：" + SENTENCE);

    await dragSelect(page, "#p-second");
    await expect(panel.locator(".bit-study-trigger")).toHaveText("学习卡片");
    await expect(panel.locator(".bit-foot")).toContainText("S 词卡");
    expect(requestsOf(upstream, "translate").length).toBeGreaterThan(0);
  });
});

test.describe("界面语言: 自动", () => {
  test.use({ uiLang: "auto" });

  // 浏览器语言随运行测试的机器而不同, 而且 Playwright 测试运行器会把网页的语言模拟成 en-US,
  // 所以期望值取自被检查的那个环境自己报告的 chrome.i18n.getUILanguage().
  const isChinese = (lang) => /^zh\b/i.test(lang);

  test("内容脚本和选项页各自跟随所在环境的浏览器语言: 中文显示中文, 其余显示英文", async ({ page, context, extensionId, serviceWorker }) => {
    const pageLang = await serviceWorker.evaluate(async (url) => {
      const tab = (await chrome.tabs.query({})).find((t) => t.url === url);
      const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: () => chrome.i18n.getUILanguage() });
      return res.result;
    }, page.url());
    await dragSelect(page, "#p-sentence");
    await expect(page.locator("#bili-index-translate-panel .bit-study-trigger")).toHaveText(
      isChinese(pageLang) ? "学习卡片" : "Study Card"
    );

    const options = await openOptions(context, extensionId);
    const chinese = isChinese(await options.evaluate(() => chrome.i18n.getUILanguage()));
    await expect(options.locator("#uiLang")).toHaveValue("auto");
    await expect(options.locator("html")).toHaveAttribute("lang", chinese ? "zh-CN" : "en");
    await expect(options.locator("#save")).toHaveText(chinese ? "保存" : "Save");
    await expect(options.locator('#uiLang option[value="auto"]')).toHaveText(chinese ? "自动（跟随浏览器语言）" : "Auto (browser language)");
    await options.close();
  });
});
