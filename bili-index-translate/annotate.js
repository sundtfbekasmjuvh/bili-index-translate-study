// annotate.js - pure functions for "annotate hard words only" mode. No DOM, no chrome.*.
// The service worker loads it with importScripts; Node (unit tests) with require.
(function (root) {
  "use strict";

  const RE_HAN_ONLY = /^[㐀-䶿一-鿿]+$/;
  const RE_HAN = /[㐀-䶿一-鿿]/g;
  const RE_KANA = /[぀-ヿ]/;
  const RE_LATIN = /[A-Za-zÀ-ɏ]/g;
  // Same shape as the background accepts for a stored en/es key (validAnnotateKey).
  const RE_KEY_LATIN = /^\p{L}[\p{L}\p{M}']*$/u;
  const MAX_KEY = 32;

  const FUNC_EN = new Set(
    ("the and of to in is that it was for with as on are this be by at from or an have not but they you we he she his her " +
      "which were been has had their will would there what").split(" ")
  );
  const FUNC_ES = new Set(
    ("el la los las de que y en un una es por con para del se al lo como más pero su sus fue son está muy también este esta " +
      "entre sobre cuando").split(" ")
  );

  // ---- word lists ----

  // zh: Map word -> HSK level (1..7, lowest wins); en/es: Map word -> rank (line number, 1-based).
  function parseWordlist(lang, text) {
    const map = new Map();
    const lines = String(text || "").split(/\r?\n/);
    if (lang === "zh") {
      let level = 0;
      for (const raw of lines) {
        const line = raw.trim();
        if (!line) continue;
        const m = /^#(\d+)$/.exec(line);
        if (m) {
          level = Number(m[1]);
          continue;
        }
        if (!level) continue;
        const old = map.get(line);
        if (old === undefined || level < old) map.set(line, level);
      }
    } else if (lang === "en" || lang === "es") {
      let rank = 0;
      for (const raw of lines) {
        const line = raw.trim();
        if (!line) continue;
        rank++;
        if (!map.has(line)) map.set(line, rank);
      }
    }
    return map;
  }

  // ---- language detection ----

  function detectLang(text, langAttr) {
    const s = String(text || "");
    if (RE_KANA.test(s)) return "ja";
    const han = (s.match(RE_HAN) || []).length;
    const latin = (s.match(RE_LATIN) || []).length;
    if (han > 0 && han > latin) return "zh";
    if (latin === 0) return null;
    const attr = String(langAttr || "").toLowerCase().split(/[-_]/)[0];
    if (attr === "en" || attr === "es") return attr;
    let en = 0;
    let es = 0;
    for (const w of s.toLowerCase().match(/[a-záéíóúüñ]+/g) || []) {
      if (FUNC_EN.has(w)) en++;
      if (FUNC_ES.has(w)) es++;
    }
    if (en > es) return "en";
    if (es > en) return "es";
    return null;
  }

  // ---- lemmatization ----

  function lemmaEn(w) {
    const out = new Set();
    const add = (s) => {
      if (s.length >= 3 && s !== w) out.add(s);
    };
    const stemmed = (stem) => {
      add(stem);
      add(stem + "e");
      if (/([^aeiou])\1$/.test(stem)) add(stem.slice(0, -1));
    };
    if (/'s$/.test(w)) add(w.slice(0, -2));
    if (/ies$/.test(w)) add(w.slice(0, -3) + "y");
    if (/es$/.test(w)) add(w.slice(0, -2));
    if (/s$/.test(w) && !/ss$/.test(w)) add(w.slice(0, -1));
    if (/ing$/.test(w)) stemmed(w.slice(0, -3));
    if (/ied$/.test(w)) add(w.slice(0, -3) + "y");
    if (/ed$/.test(w)) stemmed(w.slice(0, -2));
    if (/ier$/.test(w)) add(w.slice(0, -3) + "y");
    if (/er$/.test(w)) stemmed(w.slice(0, -2));
    if (/iest$/.test(w)) add(w.slice(0, -4) + "y");
    if (/est$/.test(w)) stemmed(w.slice(0, -3));
    if (/ily$/.test(w)) add(w.slice(0, -3) + "y");
    if (/ly$/.test(w)) {
      add(w.slice(0, -2));
      if (/[^aeiou]ly$/.test(w)) add(w.slice(0, -1) + "e");
    }
    return [...out];
  }

  const ES_AR = ["ando", "ado", "ada", "ados", "adas", "aba", "abas", "ábamos", "aban", "aron", "aste", "aré", "arás",
    "ará", "aremos", "arán", "aría", "arías", "aríamos", "arían", "amos", "áis", "ó", "é", "o", "as", "a", "an"];
  const ES_ER_IR = ["iendo", "ido", "ida", "idos", "idas", "ía", "ías", "íamos", "ían", "ieron", "iste", "ió", "í", "eré",
    "erás", "erá", "eremos", "erán", "ería", "erías", "eríamos", "erían", "emos", "imos", "éis", "o", "es", "e", "en"];

  function lemmaEs(w) {
    const out = new Set();
    const add = (s) => {
      if (s.length >= 3 && s !== w) out.add(s);
    };
    if (/mente$/.test(w) && w.length > 7) {
      const base = w.slice(0, -5);
      add(base);
      if (/a$/.test(base)) add(base.slice(0, -1) + "o");
    }
    if (/(os|as)$/.test(w)) add(w.slice(0, -2) + "o");
    if (/a$/.test(w)) add(w.slice(0, -1) + "o");
    if (/oras?$/.test(w)) add(w.replace(/ras?$/, "r"));
    if (/iones$/.test(w)) add(w.slice(0, -5) + "ión");
    if (/ces$/.test(w)) add(w.slice(0, -3) + "z");
    if (/es$/.test(w)) add(w.slice(0, -2));
    if (/s$/.test(w)) add(w.slice(0, -1));
    for (const suf of ES_AR) if (w.endsWith(suf) && w.length - suf.length >= 2) add(w.slice(0, -suf.length) + "ar");
    for (const suf of ES_ER_IR) {
      if (w.endsWith(suf) && w.length - suf.length >= 2) {
        const stem = w.slice(0, -suf.length);
        add(stem + "er");
        add(stem + "ir");
      }
    }
    return [...out];
  }

  // Candidate base forms of a lower-case word (the word itself is not included).
  function lemmaCandidates(lang, word) {
    const w = String(word || "").toLowerCase().replace(/’/g, "'");
    if (lang === "en") return lemmaEn(w);
    if (lang === "es") return lemmaEs(w);
    return [];
  }

  // ---- picking ----

  function stripNoise(text) {
    return String(text || "")
      .replace(/```[\s\S]*?```/g, (m) => " ".repeat(m.length))
      .replace(/`[^`]*`/g, (m) => " ".repeat(m.length))
      .replace(/(?:https?:\/\/|www\.)\S+/gi, (m) => " ".repeat(m.length));
  }

  function segmentWords(locale, text) {
    const seg = new Intl.Segmenter(locale, { granularity: "word" });
    return Array.from(seg.segment(text), (s) => ({ s: s.segment, index: s.index, word: !!s.isWordLike }));
  }

  function pickZh(text, table, level) {
    const segs = segmentWords("zh", text);
    const found = [];
    const contiguous = (i, n) => {
      for (let j = 0; j < n; j++) {
        const g = segs[i + j];
        if (!g || !g.word || !RE_HAN_ONLY.test(g.s)) return false;
        if (j > 0 && segs[i + j - 1].index + segs[i + j - 1].s.length !== g.index) return false;
      }
      return true;
    };
    for (let i = 0; i < segs.length; ) {
      let step = 1;
      let hit = null;
      for (let n = 4; n >= 2 && !hit; n--) {
        if (!contiguous(i, n)) continue;
        const joined = segs.slice(i, i + n).map((g) => g.s).join("");
        if (table.has(joined)) {
          hit = { w: joined, diff: table.get(joined) };
          step = n;
        }
      }
      if (!hit && contiguous(i, 1) && segs[i].s.length >= 2) {
        const w = segs[i].s;
        if (table.has(w)) hit = { w, diff: table.get(w) };
        else {
          // split by longest match inside the segment; known if every piece is within the level
          let max = 0;
          for (let p = 0; p < w.length; ) {
            let len = Math.min(6, w.length - p);
            while (len > 1 && !table.has(w.substr(p, len))) len--;
            const lv = table.get(w.substr(p, len));
            max = Math.max(max, lv === undefined ? 99 : lv);
            p += len;
          }
          hit = { w, diff: max };
        }
      }
      if (hit && hit.w.length >= 2 && hit.w.length <= MAX_KEY && hit.diff > level) {
        found.push({ w: hit.w, key: hit.w, diff: hit.diff });
      }
      i += step;
    }
    return found;
  }

  function sentenceInitial(text, index) {
    let i = index - 1;
    while (i >= 0 && /[\s"'“‘(\[¿¡—-]/.test(text[i])) {
      if (text[i] === "\n") return true;
      i--;
    }
    return i < 0 || /[.!?…。！？]/.test(text[i]);
  }

  // A word that is itself in the frequency list keeps its own rank and key: the list holds inflected
  // forms too, and the suffix rules also produce false base forms (butter -> but, casa -> caso,
  // evening -> even) that would hide a hard word and store the wrong key. Only a word missing from
  // the list falls back to the best-ranked base form.
  function resolveEntry(lang, lower, table) {
    if (table.has(lower)) return { key: lower, rank: table.get(lower) };
    let entry = { key: lower, rank: Infinity };
    for (const c of lemmaCandidates(lang, lower)) {
      const r = table.get(c);
      if (r !== undefined && r < entry.rank) entry = { key: c, rank: r };
    }
    return entry;
  }

  function pickLatin(text, lang, table, level) {
    const segs = segmentWords(lang, text).filter((g) => g.word);
    const alpha = segs.filter((g) => /^\p{L}/u.test(g.s));
    const caps = alpha.filter((g) => /^\p{Lu}/u.test(g.s)).length;
    const isTitle = alpha.length > 0 && caps / alpha.length >= 0.6;
    const found = [];
    for (const g of segs) {
      const w = g.s.replace(/’/g, "'");
      if (/[\d_]/.test(w)) continue;
      if ((w.match(/\p{L}/gu) || []).length < 3) continue;
      if (/^\p{L}.*\p{Lu}/u.test(w)) continue; // capital inside the word
      const capital = /^\p{Lu}/u.test(w);
      const initial = capital && !isTitle && sentenceInitial(text, g.index);
      if (capital && !isTitle && !initial) continue; // proper noun
      const entry = resolveEntry(lang, w.toLowerCase(), table);
      // Only a plain word can be stored as known (node.js, example.org are segmented as one "word").
      if (!RE_KEY_LATIN.test(entry.key) || entry.key.length > MAX_KEY) continue;
      if (initial && entry.rank === Infinity) continue;
      if (entry.rank <= level) continue;
      found.push({ w: g.s, key: entry.key, diff: entry.rank });
    }
    return found;
  }

  // level: HSK level (1..7) for zh, frequency rank cut-off for en/es. Words at or inside it count as known.
  function pickCandidates({ text, lang, tables, level, known, exclude, maxPerPara = 8 }) {
    const table = tables && tables[lang];
    if (!table || (lang !== "zh" && lang !== "en" && lang !== "es")) return [];
    // NFC so that a decomposed "café" matches the word list and is stored in one form.
    const clean = stripNoise(String(text || "").normalize("NFC"));
    const lv = Number(level) || 0;
    const found = lang === "zh" ? pickZh(clean, table, lv) : pickLatin(clean, lang, table, lv);
    const seen = new Set();
    const kept = [];
    for (const c of found) {
      if (seen.has(c.key)) continue;
      if ((known && known.has(c.key)) || (exclude && exclude.has(c.key))) continue;
      seen.add(c.key);
      kept.push(c);
    }
    let chosen = kept;
    if (kept.length > maxPerPara) {
      chosen = kept
        .map((c, i) => ({ c, i }))
        .sort((a, b) => (a.c.diff === b.c.diff ? a.i - b.i : a.c.diff < b.c.diff ? 1 : -1))
        .slice(0, maxPerPara)
        .sort((a, b) => a.i - b.i)
        .map((x) => x.c);
    }
    return chosen.map((c) => ({ w: c.w, key: c.key }));
  }

  // ---- prompt ----

  function buildAnnotatePrompt(paras, glossLang) {
    const zh = glossLang === "zh";
    const lang = zh ? "简体中文" : "英语";
    const limit = zh ? "12个汉字" : "6个英文单词";
    const k = paras.reduce((n, p) => n + p.words.length, 0);
    const multi = paras.length > 1;
    const head = multi
      ? "请阅读下面的" + paras.length + "段文字，只为每段自己的【词表】中的每个词给出它在该段话中的语境释义，并且严格遵循所有约束要求。"
      : "请阅读下面的【段落】，只为【词表】中的每个词给出它在这段话中的语境释义，并且严格遵循所有约束要求。";
    let body = "";
    let n = 0;
    paras.forEach((p, i) => {
      const sfx = multi ? String(i + 1) : "";
      body += "【段落" + sfx + "】\n" + p.text + "\n\n【词表" + sfx + "】\n";
      body += p.words.map((w) => ++n + ". " + w).join("\n") + "\n\n";
    });
    const rule1 = multi
      ? "1. 【硬性要求】全部词表共 " + k + " 个词，恰好输出 " + k + " 行，每行格式为“编号. 词 => 释义”，编号从 1 到 " + k +
        "，与词表中的编号一致（跨段连续），按词表顺序输出，不要分段，不要写段落标记，例如：1. 词 => 释义"
      : "1. 【硬性要求】恰好输出 " + k + " 行，每行格式为“编号. 词 => 释义”，编号从 1 到 " + k + "，例如：1. 词 => 释义";
    const content =
      head + "\n\n" + body +
      "【约束要求】\n" + rule1 + "\n" +
      "2. 【硬性要求】“词”必须与词表里的字符完全一致：词表里是什么语言就原样抄什么语言，不能换成译文，不要加注音或其他文字\n" +
      "3. 【硬性要求】释义必须用" + lang + "书写，写成对应的词或短语（像词典的简明对译），不要写成定义、解释或描述；专有名词给通用译名，不超过" + limit + "\n" +
      "4. 只处理词表中的词，不要翻译整段，不要有前言、解释或代码块\n\n" +
      "只输出这 " + k + " 行，不要有任何额外说明。";
    return { messages: [{ role: "user", content }], k };
  }

  // ---- reply parsing ----

  const LINE_RE = /^(\d+)\s*[.)、．]\s*(.+?)\s*(?:=>|→|：|:)\s*(.+)$/;

  function glossOk(g, glossLang) {
    if (!g) return false;
    const han = (g.match(RE_HAN) || []).length;
    if (glossLang === "zh") return han > 0 && han <= 40;
    if (glossLang === "en") {
      const latin = (g.match(/[A-Za-z]/g) || []).length;
      return latin > han && latin > 0 && g.split(/\s+/).length <= 12;
    }
    return han <= 40 && g.split(/\s+/).length <= 12;
  }

  function parseAnnotateReply(content, words, opts) {
    const { finishReason, glossLang } = opts || {};
    const k = words.length;
    const fail = (reason) => ({ ok: false, glosses: words.map(() => null), reason });
    if (finishReason === "length") return fail("finish_reason is length");
    const byNum = new Map();
    let junk = 0;
    let count = 0;
    for (const raw of String(content || "").split(/\r?\n/)) {
      const line = raw.trim();
      if (!line || line.startsWith("```")) continue;
      const m = LINE_RE.exec(line);
      if (!m) {
        junk += line.length;
        continue;
      }
      count++;
      const num = Number(m[1]);
      if (byNum.has(num)) return fail("duplicate number " + num);
      byNum.set(num, m[3].trim());
    }
    if (junk > 10) return fail("unparsable text (" + junk + " chars)");
    if (count !== k) return fail("expected " + k + " lines, got " + count);
    for (const num of byNum.keys()) if (num < 1 || num > k) return fail("number out of range: " + num);
    const glosses = words.map((_, i) => {
      const g = byNum.get(i + 1);
      return glossOk(g, glossLang) ? g : null;
    });
    return { ok: true, glosses, reason: "" };
  }

  // Traditional Chinese targets return null: the prompt asks for Simplified Chinese only.
  function glossLangForTarget(target) {
    const t = String(target || "").trim();
    if (!t) return null;
    if (/繁|traditional|hant|zh[-_](tw|hk|mo)/i.test(t)) return null;
    if (/^zh/i.test(t) || /chinese|中文|汉语|漢語/i.test(t)) return "zh";
    if (/^english/i.test(t) || /^en(?![a-z])/i.test(t)) return "en";
    return null;
  }

  const api = {
    parseWordlist,
    detectLang,
    lemmaCandidates,
    pickCandidates,
    buildAnnotatePrompt,
    parseAnnotateReply,
    glossLangForTarget,
  };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.BiliAnnotate = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
