"use strict";

// 直连规则的作用范围. 官方接口对带扩展 Origin 头的请求返回 412, 所以扩展去掉这个头并设置客户端名;
// 这只应该发生在扩展自己发往官方地址的请求上. 这里把官方域名解析到本机的模拟上游来检查请求头.
const { test, expect, dragSelect, requestsOf, OFFICIAL_HOST } = require("./fixtures");

const OFFICIAL_BASE = "http://" + OFFICIAL_HOST + "/v1";
const CLIENT_NAME = "Index-Translate-Client/1.0";

test.use({ mapOfficialHost: true });

test.describe("直连规则", () => {
  test("扩展发往官方地址的请求不带 Origin, 客户端名是 " + CLIENT_NAME, async ({ page, upstream, settings }) => {
    await settings({ sync: { base: OFFICIAL_BASE } });
    await dragSelect(page, "#p-sentence");
    await expect(page.locator("#bili-index-translate-panel .bit-body")).toHaveText(
      "译文：The expedition persevered toward the summit despite the storm."
    );

    const [req] = requestsOf(upstream, "translate");
    expect(req.origin).toBe("");
    expect(req.userAgent).toBe(CLIENT_NAME);
  });

  test("网页自己发往同一地址的请求保持原样: Origin 和浏览器的 User-Agent 都在", async ({ page, upstream }) => {
    await page.evaluate(async (url) => {
      // 不带自定义头的跨源 POST 不需要预检; 响应读不到, 但请求会到达服务器.
      await fetch(url, {
        method: "POST",
        mode: "no-cors",
        body: JSON.stringify({ stream: false, messages: [{ role: "user", content: "x\n\nfrom the page" }] })
      });
    }, OFFICIAL_BASE + "/chat/completions");

    await expect.poll(() => upstream.requests.length).toBe(1);
    const [req] = upstream.requests;
    expect(req.origin).toBe(upstream.origin);
    expect(req.userAgent).toContain("Mozilla/5.0");
    expect(req.userAgent).not.toContain(CLIENT_NAME);
  });

  test("扩展发往其他地址的请求不改写", async ({ page, upstream, extensionId }) => {
    // 默认夹具把接口地址设成 127.0.0.1 上的模拟上游, 不是官方域名.
    await dragSelect(page, "#p-sentence");
    await expect(page.locator("#bili-index-translate-panel .bit-foot")).toContainText("direct");

    const [req] = requestsOf(upstream, "translate");
    expect(req.origin).toBe("chrome-extension://" + extensionId);
    expect(req.userAgent).not.toContain(CLIENT_NAME);
  });

  test("规则登记为一条动态规则, 发起方是本扩展, 后台再次启动时不重复写入", async ({ serviceWorker, extensionId }) => {
    const rules = await serviceWorker.evaluate(() => chrome.declarativeNetRequest.getDynamicRules());
    expect(rules).toHaveLength(1);
    expect(rules[0].condition.urlFilter).toBe("||" + OFFICIAL_HOST + "/v1/");
    expect(rules[0].condition.initiatorDomains).toEqual([extensionId]);
    expect(await serviceWorker.evaluate(() => installDirectRule())).toBe(false);
  });
});
