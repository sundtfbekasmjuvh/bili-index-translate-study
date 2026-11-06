"use strict";

// 选项页: 读取已存设置, 保存与校验, Save & test 走真实后台链路, 术语表计数, 修改设置后清空内存缓存.
const { test, expect, dragSelect, requestsOf } = require("./fixtures");

async function openOptions(context, extensionId) {
  const page = await context.newPage();
  await page.goto("chrome-extension://" + extensionId + "/options.html");
  await expect(page.locator("#glossaryCount")).not.toBeEmpty();
  return page;
}

test.describe("选项页", () => {
  test("显示已存设置, Save & test 通过后台发出测试翻译并显示线路和模型", async ({ context, extensionId, upstream }) => {
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#base")).toHaveValue(upstream.base);
    await expect(options.locator("#proxy")).toHaveValue("");
    await expect(options.locator("#key")).toHaveValue("e2e-key");
    await expect(options.locator("#model")).toHaveValue("Index-Translate-9B");

    await options.locator("#test").click();
    const status = options.locator("#status");
    await expect(status).toHaveText(
      "OK via direct (Index-Translate-9B):\n译文：The quick brown fox jumps over the lazy dog."
    );
    await expect(status).toHaveClass("ok");
    const [req] = requestsOf(upstream, "translate");
    expect(req.body.stream).toBe(false);
    expect(req.authorization).toBe("Bearer e2e-key");
    await options.close();
  });

  test("保存时去掉末尾斜杠, 拒绝非 http(s) 地址, 术语表计数去重并忽略注释", async ({ context, extensionId, serviceWorker, upstream }) => {
    const options = await openOptions(context, extensionId);
    const status = options.locator("#status");

    await options.locator("#base").fill("ftp://example.com/v1");
    await options.locator("#save").click();
    await expect(status).toHaveText("Base URL must be an http(s) URL.");
    await expect(status).toHaveClass("err");

    await options.locator("#base").fill(upstream.base);
    await options.locator("#proxy").fill("not a url");
    await options.locator("#save").click();
    await expect(status).toHaveText("Relay prefix must be empty or an http(s) URL.");

    await options.locator("#proxy").fill(upstream.origin + "/relay/v1///");
    await options.locator("#glossary").fill("# comment\nexpedition => 远征队\nExpedition -> 探险队\nFlash-Attention\n\n// note");
    await expect(options.locator("#glossaryCount")).toHaveText("2 rules");
    await options.locator("#save").click();
    await expect(status).toHaveText("Saved (2 glossary rules active).");

    const stored = await serviceWorker.evaluate(async () => ({
      sync: await chrome.storage.sync.get(["base", "proxy"]),
      local: await chrome.storage.local.get("glossary")
    }));
    expect(stored.sync).toEqual({ base: upstream.base, proxy: upstream.origin + "/relay/v1" });
    expect(stored.local.glossary).toBe("# comment\nexpedition => 远征队\nExpedition -> 探险队\nFlash-Attention\n\n// note");
    await options.close();
  });

  test("上游拒绝时 Save & test 显示 HTTP 状态和响应片段", async ({ context, extensionId, upstream }) => {
    upstream.override("translate", { status: 401, body: "invalid key" });
    const options = await openOptions(context, extensionId);
    await options.locator("#test").click();
    await expect(options.locator("#status")).toHaveText("Failed: HTTP 401 invalid key");
    await expect(options.locator("#status")).toHaveClass("err");
    await options.close();
  });

  test("在选项页改模型后, 页面划词改用新模型且不复用旧缓存", async ({ context, extensionId, page, upstream }) => {
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-word");
    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-9B · directS 词卡");

    const options = await openOptions(context, extensionId);
    await options.locator("#model").selectOption("Index-Translate-35B-A3B");
    await options.locator("#save").click();
    await expect(options.locator("#status")).toHaveText("Saved.");
    await options.close();

    await page.bringToFront();
    await page.keyboard.press("Escape");
    await dragSelect(page, "#p-word");
    await expect(panel.locator(".bit-foot")).toHaveText("Index-Translate-35B-A3B · directS 词卡");
    expect(requestsOf(upstream, "translate").map((r) => r.body.model)).toEqual([
      "Index-Translate-9B",
      "Index-Translate-35B-A3B"
    ]);
  });

  test("Reset defaults 恢复官方地址且不发请求", async ({ context, extensionId, serviceWorker, upstream }) => {
    const options = await openOptions(context, extensionId);
    await options.locator("#reset").click();
    await expect(options.locator("#status")).toHaveText("Defaults restored.");
    await expect(options.locator("#base")).toHaveValue("https://index-translate.bilibili.com/v1");
    await expect(options.locator("#key")).toHaveValue("bili");
    const stored = await serviceWorker.evaluate(() => chrome.storage.sync.get(["base", "proxy", "key"]));
    expect(stored).toEqual({ base: "https://index-translate.bilibili.com/v1", proxy: "", key: "bili" });
    expect(upstream.requests).toHaveLength(0);
    await options.close();
  });
});