"use strict";

// 选项页的"只注释生词"一节 (界面语言默认简体中文, 英文见最后一组):
// 默认值, 保存与回显, 已存的词的数量 / 列表 / 删除 / 清空, 恢复默认不清词表, 可访问名字, 中英文切换.
const { test, expect, seedAnnotateKnown, readAnnotateKnown, readAnnotateSettings } = require("./fixtures");

async function openOptions(context, extensionId) {
  const page = await context.newPage();
  await page.goto("chrome-extension://" + extensionId + "/options.html");
  await expect(page.locator("#glossaryCount")).not.toBeEmpty();
  return page;
}

const WORDS = { zh: ["关键", "字幕"], en: ["ephemeral", "cacophony"], es: ["efímero"] };
const COUNT_TEXT = "已存 5 个词（中文 2 · 英文 2 · 西班牙文 1）";

test.describe("选项页: 只注释生词", () => {
  test("默认: 未勾选, 水平线是默认值, 显示还没有标记过的词, 没有列表和清空按钮可用", async ({ context, extensionId }) => {
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateEnabled")).not.toBeChecked();
    await expect(options.locator("#annotateLevelZh")).toHaveValue("4");
    await expect(options.locator("#annotateLevelEn")).toHaveValue("5000");
    await expect(options.locator("#annotateLevelEs")).toHaveValue("5000");
    await expect(options.locator("#annotateCount")).toHaveText("还没有标记过的词");
    await expect(options.locator("#annotateDetails")).toBeHidden();
    await expect(options.locator("#annotateClear")).toBeDisabled();

    // 中文是 HSK 1-6 级加 7-9 级, 值 1 到 7; 英文和西班牙文是六档词频.
    await expect(options.locator("#annotateLevelZh option")).toHaveText([
      "HSK 1 级", "HSK 2 级", "HSK 3 级", "HSK 4 级", "HSK 5 级", "HSK 6 级", "HSK 7-9 级"
    ]);
    expect(await options.locator("#annotateLevelZh option").evaluateAll((os) => os.map((o) => o.value))).toEqual([
      "1", "2", "3", "4", "5", "6", "7"
    ]);
    for (const id of ["#annotateLevelEn", "#annotateLevelEs"]) {
      expect(await options.locator(id + " option").evaluateAll((os) => os.map((o) => o.value))).toEqual([
        "1000", "2000", "3000", "5000", "10000", "20000"
      ]);
    }
    await expect(options.locator("#annotateLevelEn option").first()).toHaveText("最常用的前 1,000 个词算认识");
    await options.close();
  });

  test("勾选并改水平线, 点保存才写入 local; 刷新后表单回显", async ({ context, extensionId, serviceWorker }) => {
    const options = await openOptions(context, extensionId);
    await options.locator("#annotateEnabled").check();
    await options.locator("#annotateLevelZh").selectOption("6");
    await options.locator("#annotateLevelEn").selectOption("10000");
    await options.locator("#annotateLevelEs").selectOption("2000");
    // 和其他设置一样, 不点保存就不写.
    expect(await readAnnotateSettings(serviceWorker)).toEqual({});

    await options.locator("#save").click();
    await expect(options.locator("#status")).toHaveText("已保存。");
    expect(await readAnnotateSettings(serviceWorker)).toEqual({
      annotateEnabled: true,
      annotateLevels: { zh: 6, en: 10000, es: 2000 }
    });

    await options.reload();
    await expect(options.locator("#annotateEnabled")).toBeChecked();
    await expect(options.locator("#annotateLevelZh")).toHaveValue("6");
    await expect(options.locator("#annotateLevelEn")).toHaveValue("10000");
    await expect(options.locator("#annotateLevelEs")).toHaveValue("2000");
    await options.close();
  });

  test("存储里缺键或类型不对时, 表单用默认值", async ({ context, extensionId, serviceWorker }) => {
    await serviceWorker.evaluate(() =>
      chrome.storage.local.set({ annotateEnabled: "yes", annotateLevels: { zh: "x", en: 7777, es: null } })
    );
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateEnabled")).not.toBeChecked();
    await expect(options.locator("#annotateLevelZh")).toHaveValue("4");
    await expect(options.locator("#annotateLevelEn")).toHaveValue("5000");
    await expect(options.locator("#annotateLevelEs")).toHaveValue("5000");
    await options.close();
  });

  test("已存的词: 显示数量和分语言的列表; 功能关闭时也能删除, 存储和界面都更新", async ({ context, extensionId, serviceWorker }) => {
    await seedAnnotateKnown(serviceWorker, WORDS);
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateEnabled")).not.toBeChecked();
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);
    await expect(options.locator("#annotateDetails")).toBeVisible();
    await options.locator("#annotateDetails summary").click();
    await expect(options.locator("#annotateList .known-group")).toHaveCount(3);
    await expect(options.locator('#annotateList [data-lang="zh"] li span')).toHaveText(["字幕", "关键"]); // 最近的在前
    await expect(options.locator('#annotateList [data-lang="en"] li span')).toHaveText(["cacophony", "ephemeral"]);
    await expect(options.locator('#annotateList [data-lang="es"] li span')).toHaveText(["efímero"]);

    await options.getByRole("button", { name: "删除 cacophony" }).click();
    await expect(options.locator("#status")).toHaveText("已删除「cacophony」。");
    await expect(options.locator("#annotateCount")).toHaveText("已存 4 个词（中文 2 · 英文 1 · 西班牙文 1）");
    await expect(options.getByRole("button", { name: "删除 cacophony" })).toHaveCount(0);
    expect(await readAnnotateKnown(serviceWorker)).toEqual({ v: 1, zh: ["关键", "字幕"], en: ["ephemeral"], es: ["efímero"] });

    // 删到某一种语言没有词, 那一组消失; 全部删完显示空状态.
    for (const name of ["删除 efímero", "删除 ephemeral", "删除 字幕", "删除 关键"]) {
      await options.getByRole("button", { name }).click();
    }
    await expect(options.locator("#annotateCount")).toHaveText("还没有标记过的词");
    await expect(options.locator("#annotateDetails")).toBeHidden();
    await expect(options.locator("#annotateClear")).toBeDisabled();
    expect(await readAnnotateKnown(serviceWorker)).toEqual({ v: 1, zh: [], en: [], es: [] });
    await options.close();
  });

  test("功能开启时删除同样可用; 删除后焦点留在相邻的删除按钮上", async ({ context, extensionId, serviceWorker }) => {
    await seedAnnotateKnown(serviceWorker, { en: ["alpha", "bravo", "charlie"] }, { annotateEnabled: true });
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateEnabled")).toBeChecked();
    await options.locator("#annotateDetails summary").click();
    await options.getByRole("button", { name: "删除 bravo" }).focus();
    await options.keyboard.press("Enter");
    await expect(options.getByRole("button", { name: "删除 bravo" })).toHaveCount(0);
    expect((await readAnnotateKnown(serviceWorker)).en).toEqual(["alpha", "charlie"]);
    // 列表是 charlie, alpha; 删掉中间的 bravo 后, 焦点落在同一位置的 alpha 上.
    await expect(options.getByRole("button", { name: "删除 alpha" })).toBeFocused();
    await options.close();
  });

  test("词很多时每种语言先只列最近 200 个, 点显示全部才列出其余", async ({ context, extensionId, serviceWorker }) => {
    const many = Array.from({ length: 250 }, (_, i) => String.fromCharCode(97 + Math.floor(i / 26), 97 + (i % 26)));
    await seedAnnotateKnown(serviceWorker, { en: many });
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateCount")).toHaveText("已存 250 个词（中文 0 · 英文 250 · 西班牙文 0）");
    await options.locator("#annotateDetails summary").click();
    await expect(options.locator("#annotateList li")).toHaveCount(200);
    await expect(options.locator("#annotateList li span").first()).toHaveText(many[249]);
    await options.getByRole("button", { name: "显示全部 250 个" }).click();
    await expect(options.locator("#annotateList li")).toHaveCount(250);
    await expect(options.locator(".known-more")).toHaveCount(0);
    await options.close();
  });

  test("清空要两步: 点一次只变成确认按钮, 不清空; 再点一次才清空并刷新", async ({ context, extensionId, serviceWorker }) => {
    await seedAnnotateKnown(serviceWorker, WORDS);
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);
    const clear = options.locator("#annotateClear");
    await expect(clear).toHaveText("清空已存的词");

    await clear.click();
    await expect(clear).toHaveText("确认清空");
    await expect(options.locator("#status")).toContainText("确认清空");
    expect((await readAnnotateKnown(serviceWorker)).en).toEqual(["ephemeral", "cacophony"]);
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);

    await options.waitForTimeout(600); // 太快的第二次点击算作同一次双击
    await clear.click();
    await expect(options.locator("#status")).toHaveText("已清空已存的词。");
    await expect(options.locator("#annotateCount")).toHaveText("还没有标记过的词");
    await expect(clear).toHaveText("清空已存的词");
    await expect(clear).toBeDisabled();
    expect(await readAnnotateKnown(serviceWorker)).toEqual({ v: 1, zh: [], en: [], es: [] });
    await options.close();
  });

  test("清空: 双击不会清空, 只停在确认状态", async ({ context, extensionId, serviceWorker }) => {
    await seedAnnotateKnown(serviceWorker, WORDS);
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);
    const clear = options.locator("#annotateClear");
    await clear.dblclick();
    await expect(clear).toHaveText("确认清空");
    await options.waitForTimeout(300);
    expect((await readAnnotateKnown(serviceWorker)).en).toEqual(["ephemeral", "cacophony"]);
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);
    await options.close();
  });

  test("恢复默认: 关掉开关并恢复水平线, 但不清词表, 状态里说明没有清除", async ({ context, extensionId, serviceWorker }) => {
    await seedAnnotateKnown(serviceWorker, WORDS, { annotateEnabled: true, annotateLevels: { zh: 2, en: 1000, es: 20000 } });
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateEnabled")).toBeChecked();
    await expect(options.locator("#annotateLevelZh")).toHaveValue("2");
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);

    await options.locator("#reset").click();
    // 恢复默认后界面语言回到"自动", 随运行测试的机器而不同, 所以两种语言的说明都认.
    await expect(options.locator("#status")).toContainText(/已存的词没有清除|saved words were not cleared/);
    await expect(options.locator("#annotateEnabled")).not.toBeChecked();
    await expect(options.locator("#annotateLevelZh")).toHaveValue("4");
    await expect(options.locator("#annotateLevelEn")).toHaveValue("5000");
    await expect(options.locator("#annotateLevelEs")).toHaveValue("5000");
    await expect(options.locator("#annotateCount")).toBeVisible();
    expect(await readAnnotateSettings(serviceWorker)).toEqual({
      annotateEnabled: false,
      annotateLevels: { zh: 4, en: 5000, es: 5000 }
    });
    expect(await readAnnotateKnown(serviceWorker)).toEqual({ v: 1, ...WORDS });
    await options.close();
  });

  test("无障碍: 新控件都有可访问名字, 键盘可以勾选和删除", async ({ context, extensionId, serviceWorker }) => {
    await seedAnnotateKnown(serviceWorker, WORDS);
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);
    await options.locator("#annotateDetails summary").click();

    await expect(options.getByRole("checkbox", { name: "开启“只注释生词”（用于 Alt+A 页面）" })).toBeVisible();
    await expect(options.getByRole("combobox", { name: "中文水平" })).toBeVisible();
    await expect(options.getByRole("combobox", { name: "英文水平" })).toBeVisible();
    await expect(options.getByRole("combobox", { name: "西班牙文水平" })).toBeVisible();
    await expect(options.getByRole("button", { name: "清空已存的词" })).toBeVisible();
    await expect(options.getByRole("heading", { name: "只注释生词" })).toBeVisible();
    await expect(options.getByRole("list", { name: "中文（2）" })).toBeVisible();
    await expect(options.getByRole("button", { name: "删除 关键" })).toBeVisible();

    // 这一节里每个表单控件都有非空名字 (aria-label, 关联的 label 或按钮文字).
    const nameless = await options.evaluate(() => {
      const bad = [];
      for (const el of document.querySelectorAll(".annotate input, .annotate select, .annotate button")) {
        const label = el.labels && el.labels.length ? el.labels[0].textContent.trim() : "";
        const name = (el.getAttribute("aria-label") || "").trim() || label || (el.textContent || "").trim();
        if (!name) bad.push(el.id || el.className);
      }
      return bad;
    });
    expect(nameless).toEqual([]);

    await options.locator("#annotateEnabled").focus();
    await options.keyboard.press("Space");
    await expect(options.locator("#annotateEnabled")).toBeChecked();
    await options.getByRole("button", { name: "删除 efímero" }).focus();
    await options.keyboard.press("Space");
    await expect(options.getByRole("button", { name: "删除 efímero" })).toHaveCount(0);
    expect((await readAnnotateKnown(serviceWorker)).es).toEqual([]);
    await options.close();
  });
});

test.describe("选项页: 只注释生词 (英文界面)", () => {
  test.use({ uiLang: "en" });

  test("新文案是英文; 改选简体中文后立即换成中文, 选中的水平线和列表不变, 改回英文时还原", async ({ context, extensionId, serviceWorker }) => {
    await seedAnnotateKnown(serviceWorker, WORDS, { annotateLevels: { zh: 7, en: 10000, es: 3000 } });
    const options = await openOptions(context, extensionId);
    await expect(options.locator("#annotateTitle")).toHaveText("Annotate hard words only");
    await expect(options.getByRole("checkbox", { name: /Annotate hard words only/ })).toBeVisible();
    await expect(options.locator("#annotateCount")).toHaveText("5 saved words (Chinese 2 · English 2 · Spanish 1)");
    await expect(options.locator('label[for="annotateLevelZh"]')).toHaveText("Chinese level");
    await expect(options.locator("#annotateLevelZh option:checked")).toHaveText("HSK 7-9");
    await expect(options.locator("#annotateLevelEn option:checked")).toHaveText("The 10,000 most common words count as known");
    await expect(options.locator("#annotateClear")).toHaveText("Clear all saved words");
    await expect(options.locator('[data-i18n="annotate.privacy"]')).toContainText("never synced or uploaded");
    await options.locator("#annotateDetails summary").click();
    await expect(options.getByRole("button", { name: "Delete ephemeral" })).toBeVisible();
    await options.locator("#annotateClear").click();
    await expect(options.locator("#annotateClear")).toHaveText("Confirm clearing");

    await options.locator("#uiLang").selectOption("zh-CN");
    await expect(options.locator("#annotateTitle")).toHaveText("只注释生词");
    await expect(options.locator("#annotateCount")).toHaveText(COUNT_TEXT);
    await expect(options.locator('label[for="annotateLevelZh"]')).toHaveText("中文水平");
    await expect(options.locator("#annotateLevelZh option:checked")).toHaveText("HSK 7-9 级");
    await expect(options.locator("#annotateLevelEn option:checked")).toHaveText("最常用的前 10,000 个词算认识");
    await expect(options.locator("#annotateLevelEs")).toHaveValue("3000");
    await expect(options.locator('[data-i18n="annotate.privacy"]')).toContainText("不同步、不上传");
    await expect(options.locator('[data-i18n="annotate.listSummary"]')).toHaveText("查看已存的词");
    await expect(options.getByRole("button", { name: "删除 ephemeral" })).toBeVisible();
    // 切换语言会撤销"确认清空"这一步, 词表原样保留.
    await expect(options.locator("#annotateClear")).toHaveText("清空已存的词");
    expect((await readAnnotateKnown(serviceWorker)).en).toEqual(["ephemeral", "cacophony"]);

    await options.locator("#uiLang").selectOption("en");
    await expect(options.locator("#annotateTitle")).toHaveText("Annotate hard words only");
    await expect(options.locator("#annotateLevelZh option:checked")).toHaveText("HSK 7-9");
    await expect(options.locator("#annotateCount")).toHaveText("5 saved words (Chinese 2 · English 2 · Spanish 1)");
    await options.close();
  });
});
