"use strict";

// 预览沙盒 (bili-study-sandbox): 页面在不装扩展的普通浏览器里加载扩展的内容脚本.
// 这里只检查页面能正常启动: 它引用的扩展文件都能取到, 内容脚本运行无报错, 界面文案跟随浏览器语言.
// 不触发翻译, 因为沙盒服务会把翻译请求发往真实接口.
const path = require("path");
const net = require("net");
const { spawn } = require("child_process");
const { test, expect } = require("@playwright/test");

const SERVER = path.resolve(__dirname, "../../bili-study-sandbox/server.js");

function freePort() {
  return new Promise((resolve, reject) => {
    const probe = net.createServer();
    probe.once("error", reject);
    probe.listen(0, "127.0.0.1", () => {
      const { port } = probe.address();
      probe.close(() => resolve(port));
    });
  });
}

let server;
let origin;

test.beforeAll(async () => {
  const port = await freePort();
  origin = "http://127.0.0.1:" + port;
  server = spawn(process.execPath, [SERVER], { env: { ...process.env, PORT: String(port), HOST: "127.0.0.1" }, stdio: ["ignore", "pipe", "pipe"] });
  await new Promise((resolve, reject) => {
    let output = "";
    const onData = (chunk) => {
      output += chunk;
      if (output.includes("listening on " + origin)) resolve();
    };
    server.stdout.on("data", onData);
    server.stderr.on("data", onData);
    server.once("exit", (code) => reject(new Error("sandbox server exited with code " + code + ": " + output)));
    setTimeout(() => reject(new Error("sandbox server did not start: " + output)), 10000);
  });
});

test.afterAll(() => {
  if (server) server.kill();
});

// 与扩展用例一致, 使用完整的 Chromium (CI 只安装了它).
test.use({ channel: "chromium" });

for (const [locale, hint] of [
  ["en-US", "Drag over an image, a comic panel or video subtitles to translate \u00b7 Esc cancels"],
  ["zh-CN", "\u62d6\u62fd\u6846\u9009\u56fe\u7247 / \u6f2b\u753b / \u89c6\u9891\u5b57\u5e55\u533a\u57df\u76f4\u8bd1 \u00b7 Esc \u53d6\u6d88"]
]) {
  test.describe("预览沙盒, 浏览器语言 " + locale, () => {
    test.use({ locale });

    test("页面引用的扩展文件都能加载, 内容脚本无报错, 框选提示跟随浏览器语言", async ({ page }) => {
      const errors = [];
      const failed = [];
      const external = [];
      page.on("pageerror", (err) => errors.push(String(err)));
      page.on("console", (msg) => msg.type() === "error" && errors.push(msg.text()));
      page.on("response", (res) => res.status() >= 400 && failed.push(res.status() + " " + res.url()));
      page.on("request", (req) => new URL(req.url()).hostname !== "127.0.0.1" && external.push(req.url()));

      await page.goto(origin + "/");
      const scripts = await page.locator("script[src]").evaluateAll((els) => els.map((el) => el.getAttribute("src")));
      expect(scripts).toEqual(["ext/i18n.js", "ext/content.js"]);
      expect(await page.evaluate(() => window.__biliIndexTranslateLoaded === true && typeof window.BiliI18n === "object")).toBe(true);

      await page.locator("#btn-vision-snip-demo").click();
      await expect(page.locator("#bili-index-snip-mask .bit-snip-hint")).toHaveText(hint);
      await page.keyboard.press("Escape");
      await expect(page.locator("#bili-index-snip-mask")).toHaveCount(0);

      expect(failed).toEqual([]);
      expect(errors).toEqual([]);
      expect(external).toEqual([]);
    });
  });
}
