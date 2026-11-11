"use strict";

// DeepLX 接口格式: 选项页保存, 测试与格式不匹配提示, 划词, 学习卡提示不支持, Alt+A 合并请求与模型按钮.
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

const endpoint = (upstream) => upstream.origin + "/translate?token={apiKey}";
const deeplx = (upstream) => ({ sync: { apiType: "deeplx", base: endpoint(upstream) } });

test.describe("DeepLX 接口格式", () => {
  test("选项页保存接口格式和带 {apiKey} 的地址, Save & test 把 key 填进地址并显示译文", async ({ context, extensionId, serviceWorker, upstream }) => {
    const options = await context.newPage();
    await options.goto("chrome-extension://" + extensionId + "/options.html");
    await expect(options.locator("#glossaryCount")).not.toBeEmpty();
    await expect(options.locator("#apiType")).toHaveValue("openai");

    await options.locator("#apiType").selectOption("deeplx");
    // 模型和术语表对 DeepLX 接口无效, 选中后置灰.
    for (const id of ["#model", "#immersiveModel", "#visionModel", "#glossary"]) {
      await expect(options.locator(id)).toBeDisabled();
    }
    await expect(options.locator("#base")).toHaveAttribute("placeholder", "https://host/translate?token={apiKey}");
    await options.locator("#base").fill(endpoint(upstream));
    await options.locator("#save").click();
    const status = options.locator("#status");
    await expect(status).toHaveText("Saved.");
    const stored = await serviceWorker.evaluate(() => chrome.storage.sync.get(["apiType", "base"]));
    expect(stored).toEqual({ apiType: "deeplx", base: endpoint(upstream) });
    expect(upstream.requests).toHaveLength(0);

    await options.locator("#test").click();
    await expect(status).toHaveText("OK via direct (DeepLX):\n译文：" + TEST_TEXT);
    await expect(status).toHaveClass("ok");
    expect(upstream.requests).toHaveLength(1);
    const [req] = requestsOf(upstream, "deeplx");
    expect(req.path).toBe("/translate");
    expect(req.token).toBe("e2e-key");
    expect(req.authorization).toBe("");
    expect(req.body).toEqual({ text: TEST_TEXT, source_lang: "auto", target_lang: "ZH" });

    await options.reload();
    await expect(options.locator("#apiType")).toHaveValue("deeplx");
    await expect(options.locator("#base")).toHaveValue(endpoint(upstream));
    await expect(options.locator("#model")).toBeDisabled();
    await options.locator("#apiType").selectOption("openai");
    await expect(options.locator("#model")).toBeEnabled();
    await expect(options.locator("#glossary")).toBeEnabled();
    await options.close();
  });

  test("接口格式与地址不匹配: OpenAI 格式下带 {apiKey} 的地址拒绝保存, 其余情况保存并给出提示", async ({ context, extensionId, serviceWorker, upstream }) => {
    const options = await context.newPage();
    await options.goto("chrome-extension://" + extensionId + "/options.html");
    await expect(options.locator("#glossaryCount")).not.toBeEmpty();
    const status = options.locator("#status");

    await options.locator("#base").fill(endpoint(upstream));
    await options.locator("#save").click();
    await expect(status).toContainText("only the DeepLX API format fills in");
    await expect(status).toHaveClass("err");
    expect(await serviceWorker.evaluate(() => chrome.storage.sync.get(["apiType", "base"]))).toEqual({ base: upstream.base });

    await options.locator("#base").fill(upstream.origin + "/translate");
    await options.locator("#save").click();
    await expect(status).toHaveText(
      "Saved.\nThis URL looks like a DeepLX endpoint. If requests fail, set API format to DeepLX-compatible."
    );

    await options.locator("#apiType").selectOption("deeplx");
    await options.locator("#base").fill(upstream.base);
    await options.locator("#save").click();
    await expect(status).toContainText("Saved.");
    await expect(status).toContainText("looks like an OpenAI-compatible root");
    expect(upstream.requests).toHaveLength(0);
    await options.close();
  });

  test("划词只请求一次 DeepLX 接口, 页脚显示 DeepLX 且不提示词卡; 按 S 说明学习卡需要 OpenAI 兼容接口", async ({ page, upstream, settings }) => {
    await settings(deeplx(upstream));
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");

    await expect(panel.locator(".bit-body")).toHaveText("译文：" + SENTENCE);
    await expect(panel.locator(".bit-foot")).toHaveText("DeepLX · direct");
    expect(upstream.requests.map((r) => [r.kind, r.path, r.token, r.source])).toEqual([
      ["deeplx", "/translate", "e2e-key", SENTENCE]
    ]);

    await page.keyboard.press("s");
    const study = page.locator("#bili-index-study-panel");
    await expect(study.locator(".bit-error")).toContainText("Study Card needs an OpenAI-compatible chat model");
    expect(upstream.requests).toHaveLength(1);
  });

  test("DeepLX 接口拒绝时显示 HTTP 状态和响应片段, 只请求一次", async ({ page, upstream, settings }) => {
    await settings(deeplx(upstream));
    upstream.override("deeplx", { status: 403, body: "token rejected" });
    const panel = page.locator("#bili-index-translate-panel");
    await dragSelect(page, "#p-sentence");

    const error = panel.locator(".bit-error");
    await expect(error).toContainText("HTTP 403");
    await expect(error).toContainText("token rejected");
    await expect(panel.locator(".bit-foot")).toHaveText("via direct");
    await page.waitForTimeout(300);
    expect(upstream.requests).toHaveLength(1);
  });

  test("Alt+A 把段落合并成少量 DeepLX 请求并隐藏模型切换按钮, 再次开启命中缓存, 改回 OpenAI 格式后按钮恢复", async ({ page, upstream, settings }) => {
    await settings(deeplx(upstream));
    await page.keyboard.press("Alt+KeyA");
    const bar = page.locator("#bili-index-imm-bar");
    const status = bar.locator(".bit-imm-status");
    await expect(status).toHaveText("双语就绪 5/6");
    await expect(page.locator("#p-sentence > .bit-imm-block")).toHaveText("译文：" + SENTENCE);
    await expect(page.locator("#li-one > .bit-imm-block")).toHaveText("译文：" + VISIBLE[4]);
    await expect(page.locator("#p-zh .bit-imm-block")).toHaveCount(0);

    // 首屏 5 段按行合并发送: 每段恰好发出一次, 请求数少于段落数, 每个请求不超过 4 行.
    const sent = upstream.requests.length;
    expect(upstream.requests.every((r) => r.kind === "deeplx")).toBe(true);
    expect(upstream.requests.flatMap((r) => r.source.split("\n")).sort()).toEqual([...VISIBLE].sort());
    expect(sent).toBeLessThan(VISIBLE.length);
    expect(Math.max(...upstream.requests.map((r) => r.source.split("\n").length))).toBeLessThanOrEqual(4);
    expect(new Set(upstream.requests.map((r) => r.body.target_lang))).toEqual(new Set(["ZH"]));

    const [modeBtn, modelBtn] = await bar.locator(".bit-imm-btn").all();
    await expect(modeBtn).toBeVisible();
    await expect(modelBtn).toBeHidden();

    await page.keyboard.press("Alt+KeyA");
    await page.keyboard.press("Alt+KeyA");
    await expect(status).toHaveText("双语就绪 5/6");
    await expect(page.locator("#p-sentence > .bit-imm-block")).toHaveText("译文：" + SENTENCE);
    expect(upstream.requests).toHaveLength(sent);

    await page.keyboard.press("Alt+KeyA");
    await settings();
    await page.keyboard.press("Alt+KeyA");
    await expect(status).toHaveText("双语就绪 5/6");
    await expect(modelBtn).toBeVisible();
    await expect(modelBtn).toHaveText("2B");
    expect(upstream.requests.slice(sent).every((r) => r.path === "/v1/chat/completions")).toBe(true);
  });
});
