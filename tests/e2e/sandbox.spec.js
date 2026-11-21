"use strict";

// 预览沙盒 (bili-study-sandbox): 页面在不装扩展的普通浏览器里加载扩展的内容脚本.
// 这里只检查页面能正常启动: 它引用的扩展文件都能取到, 内容脚本运行无报错, 界面文案跟随浏览器语言.
// 不触发翻译, 因为沙盒服务会把翻译请求发往真实接口.
const path = require("path");
const net = require("net");
const http = require("http");
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

// 发一条原始 HTTP 请求, 这样 Host 和 Origin 头可以随意指定.
function rawRequest({ method = "GET", path: target = "/", headers = {}, body = "" }) {
  return new Promise((resolve, reject) => {
    const { hostname, port } = new URL(origin);
    const req = http.request({ host: hostname, port, method, path: target, headers }, (res) => {
      let text = "";
      res.on("data", (chunk) => (text += chunk));
      res.on("end", () => resolve({ status: res.statusCode, text }));
    });
    req.on("error", reject);
    req.end(body);
  });
}

test.describe("预览沙盒的访问限制", () => {
  const prefs = JSON.stringify({ type: "BILI_GET_IMMERSIVE_PREFS" });

  test("只监听本机时, 用别的主机名寻址或由别的网站发起的请求被拒绝, 沙盒页面自己的请求正常", async () => {
    expect((await rawRequest({})).status).toBe(200);
    expect((await rawRequest({ headers: { Host: "localhost:3001" } })).status).toBe(200);
    expect((await rawRequest({ headers: { Host: "attacker.example" } })).status).toBe(403);

    const own = await rawRequest({ method: "POST", path: "/api/runtime", headers: { Origin: origin }, body: prefs });
    expect(own.status).toBe(200);
    expect(JSON.parse(own.text).ok).toBe(true);

    const foreign = await rawRequest({ method: "POST", path: "/api/runtime", headers: { Origin: "https://attacker.example" }, body: prefs });
    expect(foreign.status).toBe(403);
  });

  test("请求体不是 JSON 时返回错误信息, 服务不退出", async () => {
    const bad = await rawRequest({ method: "POST", path: "/api/translate-stream", headers: { Origin: origin }, body: "{not json" });
    expect(bad.status).toBe(200);
    expect(JSON.parse(bad.text).ok).toBe(false);
    expect((await rawRequest({})).status).toBe(200);
  });
  test("请求体的汉字字节被切在两个数据块之间时, 服务端仍按 UTF-8 正确解码", async () => {
    // 请求体故意不是 JSON: 服务端的 JSON.parse 报错信息会原样带上它收到的文字, 借此观察解码结果, 不用碰任何上游接口.
    const body = Buffer.from("\u6c49\u5b57\u6c49\u5b57", "utf8");
    const cut = 4; // 第二个汉字 (3 字节) 的第 2 个字节之后
    const { hostname, port } = new URL(origin);
    const text = await new Promise((resolve, reject) => {
      const socket = net.connect({ host: hostname, port: Number(port) });
      const received = [];
      socket.on("data", (chunk) => received.push(chunk));
      socket.on("end", () => resolve(Buffer.concat(received).toString("utf8")));
      socket.on("error", reject);
      socket.write(
        "POST /api/translate-stream HTTP/1.1\r\nHost: " + hostname + ":" + port + "\r\nOrigin: " + origin +
          "\r\nContent-Type: application/json\r\nContent-Length: " + body.length + "\r\nConnection: close\r\n\r\n"
      );
      socket.write(body.subarray(0, cut));
      setTimeout(() => socket.write(body.subarray(cut)), 60);
    });
    expect(text).toContain("\u6c49\u5b57\u6c49\u5b57");
    expect(text).not.toContain("\ufffd");
  });
});
