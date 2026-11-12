"use strict";

const path = require("path");
const fs = require("fs");
const os = require("os");
const base = require("@playwright/test");
const { startMockUpstream } = require("../support/mock-upstream");

const EXTENSION_DIR = path.resolve(__dirname, "../../bili-index-translate");
const { expect } = base;

// One browser with the unpacked extension per worker; settings and caches are reset per test.
const test = base.test.extend({
  upstream: [
    async ({}, use) => {
      const upstream = await startMockUpstream();
      await use(upstream);
      await upstream.close();
    },
    { scope: "worker" }
  ],

  // Interface language the extension is set to. The browser's own language differs between machines
  // (zh-CN on a Chinese desktop, en-US on CI), so every test pins it; override with test.use({ uiLang }).
  uiLang: ["zh-CN", { option: true }],

  externalRequests: [
    async ({}, use) => {
      await use([]);
    },
    { scope: "worker" }
  ],

  extensionContext: [
    async ({ externalRequests }, use) => {
      const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "bit-e2e-"));
      const context = await base.chromium.launchPersistentContext(userDataDir, {
        channel: "chromium",
        headless: true,
        viewport: { width: 1280, height: 800 },
        args: ["--disable-extensions-except=" + EXTENSION_DIR, "--load-extension=" + EXTENSION_DIR]
      });
      context.on("request", (request) => {
        const url = new URL(request.url());
        if (/^https?:$/.test(url.protocol) && url.hostname !== "127.0.0.1") externalRequests.push(request.url());
      });
      await use(context);
      await context.close();
      fs.rmSync(userDataDir, { recursive: true, force: true });
    },
    { scope: "worker" }
  ],

  serviceWorker: [
    async ({ extensionContext }, use) => {
      let [worker] = extensionContext.serviceWorkers();
      if (!worker) worker = await extensionContext.waitForEvent("serviceworker");
      await use(worker);
    },
    { scope: "worker" }
  ],

  extensionId: [
    async ({ serviceWorker }, use) => {
      await use(new URL(serviceWorker.url()).host);
    },
    { scope: "worker" }
  ],

  context: async ({ extensionContext }, use) => {
    await use(extensionContext);
  },

  // Every request must stay on the local mock; nothing may reach the real API or a relay.
  stayLocal: [
    async ({ externalRequests }, use) => {
      externalRequests.length = 0;
      await use();
      expect(externalRequests, "requests that left 127.0.0.1").toEqual([]);
    },
    { auto: true }
  ],

  // Auto so that tests which only open extension pages also start from known settings and an empty request log.
  settings: [
    async ({ serviceWorker, upstream, uiLang }, use) => {
      const apply = async (overrides = {}) => {
        await serviceWorker.evaluate(
          async ({ sync, local }) => {
            // Write the new values before removing leftovers, so that no setting (the interface
            // language in particular) is briefly missing and seen at its default by an open page.
            for (const [area, values] of [[chrome.storage.sync, sync], [chrome.storage.local, local]]) {
              const stale = Object.keys(await area.get(null)).filter((key) => !(key in values));
              await area.set(values);
              if (stale.length) await area.remove(stale);
            }
            volatileTranslateCache.clear();
            volatileStudyCache.clear();
            volatileVisionCache.clear();
          },
          {
            sync: {
              base: upstream.base,
              proxy: "",
              target: "中文",
              model: "Index-Translate-9B",
              immersiveModel: "Index-Translate-2B",
              visionModel: "Index-Translate-35B-A3B",
              uiLang: uiLang,
              ...(overrides.sync || {})
            },
            // The API key lives in chrome.storage.local, not in the synced area.
            local: { glossary: "", key: "e2e-key", ...(overrides.local || {}) }
          }
        );
      };
      await apply();
      upstream.reset();
      await use(apply);
    },
    { auto: true }
  ],

  page: async ({ extensionContext, serviceWorker, settings, upstream }, use) => {
    const page = await extensionContext.newPage();
    await page.goto(upstream.origin + "/article.html");
    await expect
      .poll(() => contentScriptLoaded(serviceWorker, page.url()), { message: "content script injected", timeout: 10000 })
      .toBe(true);
    await page.bringToFront();
    await use(page);
    await page.close();
  }
});

async function contentScriptLoaded(serviceWorker, url) {
  return serviceWorker.evaluate(async (target) => {
    const tab = (await chrome.tabs.query({})).find((t) => t.url === target);
    if (!tab) return false;
    try {
      const [res] = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        // The language marker appears once the content script has read the saved interface language.
        func: () => window.__biliIndexTranslateLoaded === true && typeof window.__biliIndexTranslateLang === "string"
      });
      return Boolean(res && res.result);
    } catch (_) {
      return false;
    }
  }, url);
}

// Drags across an element with the left button, which is how a user selects text. Any previous
// selection is cleared first, as a click elsewhere would; pressing inside a selection starts a drag.
async function dragSelect(page, selector) {
  await page.evaluate(() => getSelection().removeAllRanges());
  const box = await page.locator(selector).boundingBox();
  if (!box) throw new Error("element not visible: " + selector);
  await page.mouse.move(box.x + 1, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 1, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
}

// Native context menus cannot be clicked from Playwright, so the click event is dispatched in the
// service worker with the same info object Chrome would pass.
async function clickContextMenu(serviceWorker, page, info) {
  await serviceWorker.evaluate(
    async ({ url, info }) => {
      const tab = (await chrome.tabs.query({})).find((t) => t.url === url);
      chrome.contextMenus.onClicked.dispatch({ frameId: 0, ...info }, tab);
    },
    { url: page.url(), info }
  );
}

async function runCommand(serviceWorker, command) {
  await serviceWorker.evaluate((name) => chrome.commands.onCommand.dispatch(name), command);
}

function requestsOf(upstream, kind) {
  return upstream.requests.filter((r) => r.kind === kind);
}

module.exports = { test, expect, dragSelect, clickContextMenu, runCommand, requestsOf, EXTENSION_DIR };