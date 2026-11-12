"use strict";

// 自定义 JSON 接口模板: 选项页的预设, 校验, 保存与测试, 划词, 学习卡提示不支持, Alt+A 批量请求.
const { test, expect, dragSelect, requestsOf } = require("./fixtures");

const TEST_TEXT = "The quick brown fox jumps over the lazy dog.";
const SENTENCE = "The expedition persevered toward the summit despite the storm.";
const VISIBLE = [
  "Release notes",
  "ephemeral",
  SENTENCE,
  "Every checkpoint was logged before sunrise.",
  "Install the unpacked extension from the build folder."
];

// 模拟服务的 /custom-api 用的是自拟的形状, 只能靠模板描述.
const TEMPLATE = {
  customName: "Mock MT",
  customMethod: "POST",
  customHeaders: "X-Api-Key: {apiKey}",
  customBody: '{"q": {texts}, "to": "{targetLangLower}"}',
  customResultPath: "result.items[*].out",
  customErrorPath: "error.message"
};
const custom = (upstream) => ({ sync: { apiType: "custom", base: upstream.origin + "/custom-api", ...TEMPLATE } });

test.describe("自定义接口模板", () => {
  test("选项页: 预设只填表单不保存, 无法工作的模板拒绝保存, 填好后 Save & test 按模板发请求并显示译文", async ({ context, extensionId, serviceWorker, upstream }) => {
    const options = await context.newPage();
    await options.goto("chrome-extension://" + extensionId + "/options.html");
    await expect(options.locator("#glossaryCount")).not.toBeEmpty();
    const status = options.locator("#status");
    // The stored format may be absent or the default; what matters is that it has not become custom.
    const storedService = () =>
      serviceWorker.evaluate(async () => {
        const s = await chrome.storage.sync.get(["apiType", "base"]);
        return { custom: s.apiType === "custom", base: s.base };
      });
    await expect(options.locator("#customFields")).toBeHidden();

    await options.locator("#apiType").selectOption("custom");
    await expect(options.locator("#customFields")).toBeVisible();
    for (const id of ["#model", "#immersiveModel", "#visionModel", "#glossary"]) {
      await expect(options.locator(id)).toBeDisabled();
    }

    await options.locator("#customPreset").selectOption("deepl-free");
    await expect(options.locator("#base")).toHaveValue("https://api-free.deepl.com/v2/translate");
    await expect(options.locator("#customHeaders")).toHaveValue("Authorization: DeepL-Auth-Key {apiKey}");
    await expect(options.locator("#customBody")).toHaveValue('{"text": {texts}, "target_lang": "{targetLang}"}');
    await expect(options.locator("#customResultPath")).toHaveValue("translations[*].text");
    await expect(status).toContainText("Preset loaded: DeepL");
    expect(await storedService()).toEqual({ custom: false, base: upstream.base });

    // 结果路径为空的模板无法工作: 拒绝保存, 也不申请主机权限.
    await options.locator("#customResultPath").fill("");
    await options.locator("#save").click();
    await expect(status).toContainText("Custom API: the result path is empty");
    await expect(status).toHaveClass("err");
    expect(await storedService()).toEqual({ custom: false, base: upstream.base });

    await options.locator("#customMethod").selectOption("GET");
    await expect(options.locator("#customBody")).toBeDisabled();
    await options.locator("#customMethod").selectOption("POST");
    await expect(options.locator("#customBody")).toBeEnabled();

    await options.locator("#base").fill(upstream.origin + "/custom-api");
    await options.locator("#customName").fill(TEMPLATE.customName);
    await options.locator("#customHeaders").fill(TEMPLATE.customHeaders);
    await options.locator("#customBody").fill(TEMPLATE.customBody);
    await options.locator("#customResultPath").fill(TEMPLATE.customResultPath);
    await options.locator("#customErrorPath").fill(TEMPLATE.customErrorPath);
    await options.locator("#test").click();
    await expect(status).toHaveText("OK via direct (Mock MT):\n译文：" + TEST_TEXT);
    await expect(status).toHaveClass("ok");

    expect(upstream.requests).toHaveLength(1);
    const [req] = requestsOf(upstream, "custom");
    expect(req.path).toBe("/custom-api");
    expect(req.apiKeyHeader).toBe("e2e-key");
    expect(req.authorization).toBe("");
    expect(req.body).toEqual({ q: [TEST_TEXT], to: "zh" });

    await options.reload();
    await expect(options.locator("#apiType")).toHaveValue("custom");
    await expect(options.locator("#customFields")).toBeVisible();
    await expect(options.locator("#customName")).toHaveValue("Mock MT");
    await expect(options.locator("#customBody")).toHaveValue(TEMPLATE.customBody);
    await expect(options.locator("#customResultPath")).toHaveValue(TEMPLATE.customResultPath);
    await options.close();
  });

  test("划词按模板请求一次, 页脚显示模板的名称; 接口报错时显示错误路径里的文字; 按 S 说明学习卡不可用", async ({ page, upstream, settings }) => {
    await settings(custom(upstream));
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");
    await expect(panel.locator(".bit-body")).toHaveText("译文：" + SENTENCE);
    await expect(panel.locator(".bit-foot")).toHaveText("Mock MT · direct");
    expect(upstream.requests.map((r) => [r.kind, r.path, r.apiKeyHeader, r.body])).toEqual([
      ["custom", "/custom-api", "e2e-key", { q: [SENTENCE], to: "zh" }]
    ]);

    await page.keyboard.press("s");
    await expect(page.locator("#bili-index-study-panel .bit-error")).toContainText("The custom API format only translates text");
    expect(upstream.requests).toHaveLength(1);

    upstream.override("custom", { status: 403, contentType: "application/json", body: '{"error":{"message":"Key rejected by Mock MT"}}' });
    await page.keyboard.press("Escape");
    await dragSelect(page, "#p-second");
    const error = panel.locator(".bit-error");
    await expect(error).toContainText("HTTP 403");
    await expect(error).toContainText("Key rejected by Mock MT");
    await expect(error).not.toContainText("{");
    await page.waitForTimeout(300);
    expect(upstream.requests).toHaveLength(2);
  });

  test("Alt+A 把每批段落作为数组发成一次请求, 隐藏模型切换按钮, 再次开启命中缓存", async ({ page, upstream, settings }) => {
    await settings(custom(upstream));
    await page.keyboard.press("Alt+KeyA");
    const bar = page.locator("#bili-index-imm-bar");
    const status = bar.locator(".bit-imm-status");
    await expect(status).toHaveText("双语就绪 5/6");
    await expect(page.locator("#p-sentence > .bit-imm-block")).toHaveText("译文：" + SENTENCE);
    await expect(page.locator("#li-one > .bit-imm-block")).toHaveText("译文：" + VISIBLE[4]);

    const sent = upstream.requests.length;
    expect(upstream.requests.every((r) => r.kind === "custom" && Array.isArray(r.body.q))).toBe(true);
    expect(upstream.requests.flatMap((r) => r.body.q).sort()).toEqual([...VISIBLE].sort());
    expect(sent).toBeLessThan(VISIBLE.length);

    const [modeBtn, modelBtn] = await bar.locator(".bit-imm-btn").all();
    await expect(modeBtn).toBeVisible();
    await expect(modelBtn).toBeHidden();

    await page.keyboard.press("Alt+KeyA");
    await page.keyboard.press("Alt+KeyA");
    await expect(status).toHaveText("双语就绪 5/6");
    expect(upstream.requests).toHaveLength(sent);
  });
});
