// content.js — Editorial Minimalist Dual-Popup Companion (SSE Streaming + Zero Trace)
(function () {
  "use strict";

  if (window.__biliIndexTranslateLoaded) return;
  window.__biliIndexTranslateLoaded = true;

  const PANEL_ID = "bili-index-translate-panel";
  const STUDY_PANEL_ID = "bili-index-study-panel";
  const IMM_BAR_ID = "bili-index-imm-bar";
  const SNIP_MASK_ID = "bili-index-snip-mask";
  const MIN_LEN = 2;
  const MAX_LEN = 3000;
  const VIEWPORT_GAP = 12;
  const COMPANION_GAP = 10;
  const MAX_SNIP_EDGE_PX = 1440;

  let panel = null;
  let bodyEl = null;
  let footEl = null;
  let studyTriggerBtn = null;
  let mainCopyBtn = null;
  let mainSpeakBtn = null;

  let studyPanel = null;
  let studyBodyEl = null;
  let studyFootLeftEl = null;
  let studyFootRightEl = null;
  let studyLangBadge = null;
  let studyCompactBtn = null;
  let studySpeedBtn = null;
  let studyQuizBtn = null;
  let studyCopyBtn = null;

  let reqSeq = 0;
  let translatePort = null;
  let studyReqSeq = 0;
  let studyPort = null;
  let dragged = false;
  let studyDragged = false;
  let textScale = 1;

  // Session-only UI preferences (volatile RAM only, never written to storage)
  let preferFlagshipStudyModel = false;
  let compactStudyMode = false;

  // Ephemeral active selection & study state — wiped on close
  let currentSourceText = "";
  let currentTranslatedText = "";
  let currentAnchor = null;
  let studyState = null;
  let footFlashTimer = null;
  let snipMask = null;
  let lastContextTarget = null;
  let lastContextPoint = null;

  function makeIcon(kind) {
    const NS = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(NS, "svg");
    svg.setAttribute("class", "bit-icon");
    svg.setAttribute("viewBox", "0 0 16 16");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "1.5");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");

    const addPath = (d) => {
      const p = document.createElementNS(NS, "path");
      p.setAttribute("d", d);
      svg.appendChild(p);
    };

    if (kind === "book") {
      addPath("M2.2 3.2A2.2 2.2 0 0 1 4.4 1h8.4v11.5H4.4a2.2 2.2 0 0 0-2.2 2.2V3.2Z");
      addPath("M2.2 12.8A2.2 2.2 0 0 1 4.4 11h8.4");
      addPath("M5.5 4.5h4.5");
    } else if (kind === "speaker") {
      addPath("M2.5 6v4h2.8L9 13V3L5.3 6H2.5Z");
      addPath("M11.3 5.5a3.5 3.5 0 0 1 0 5");
      addPath("M13.2 3.6a6 6 0 0 1 0 8.8");
    } else if (kind === "copy") {
      addPath("M5.5 5.5h7a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1Z");
      addPath("M11 5.5V3.5a1 1 0 0 0-1-1H3.5a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1H4.5");
    } else if (kind === "check") {
      addPath("M3 8.5l3.2 3.2L13 4.5");
    } else if (kind === "eye") {
      addPath("M1.5 8s2.4-4.5 6.5-4.5S14.5 8 14.5 8s-2.4 4.5-6.5 4.5S1.5 8 1.5 8Z");
      const c = document.createElementNS(NS, "circle");
      c.setAttribute("cx", "8");
      c.setAttribute("cy", "8");
      c.setAttribute("r", "1.8");
      svg.appendChild(c);
    }
    return svg;
  }

  function inferQuickLang(text) {
    const s = String(text || "");
    if (/[\u3040-\u30ff]/.test(s)) return "ja-JP";
    if (/[\uac00-\ud7af\u1100-\u11ff]/.test(s)) return "ko-KR";
    if (/[\u0400-\u04ff]/.test(s)) return "ru-RU";
    if (/[\u4e00-\u9fff]/.test(s)) return "zh-CN";
    return "en-US";
  }

  function extractForeignPhrase(collocationText) {
    const s = String(collocationText || "").trim();
    if (!s) return "";
    const stripped = s.replace(/\s*[（(][^)）]*[）)]\s*$/g, "").trim();
    return stripped || s;
  }

  async function copyToClipboard(text, btnEl, feedbackMsg) {
    if (!text) return;
    let ok = false;
    try {
      await navigator.clipboard.writeText(text);
      ok = true;
    } catch (_) {
      try {
        const ta = document.createElement("textarea");
        ta.value = text;
        ta.style.position = "fixed";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.select();
        ok = document.execCommand("copy");
        ta.remove();
      } catch (_2) {}
    }
    if (!ok) return;

    if (btnEl) {
      btnEl.setAttribute("data-copied", "");
      btnEl.textContent = "";
      btnEl.appendChild(makeIcon("check"));
      setTimeout(() => {
        if (!btnEl.isConnected) return;
        btnEl.removeAttribute("data-copied");
        btnEl.textContent = "";
        btnEl.appendChild(makeIcon("copy"));
      }, 1200);
    }
    if (feedbackMsg && isStudyOpen()) {
      flashStudyFoot(feedbackMsg);
    }
  }

  function formatStudyCardMarkdown(card, grammarNote) {
    if (!card) return "";
    const meta = [card.pos, card.register].filter(Boolean).join(" · ");
    const lines = [];
    lines.push(
      card.term +
        (card.reading ? " [" + card.reading + "]" : "") +
        (meta ? " (" + meta + ")" : "")
    );
    if (card.meaning) lines.push("释义：" + card.meaning);
    if (card.nuance) lines.push("辨析：" + card.nuance);
    if (card.memoryAid) lines.push("助记：" + card.memoryAid);
    if (card.collocations && card.collocations.length > 0) {
      lines.push("搭配：" + card.collocations.join(" / "));
    }
    if (card.example && card.example.source) {
      lines.push(
        "例句：" +
          card.example.source +
          (card.example.target ? " — " + card.example.target : "")
      );
    }
    if (grammarNote) lines.push("句法：" + grammarNote);
    return lines.join("\n");
  }

  // Safely highlights surface/term occurrences inside an example sentence using pure DOM nodes
  function appendHighlightedSentence(container, sentence, term, surface) {
    const src = String(sentence || "");
    if (!src) return;

    const candidates = [surface, term]
      .map((x) => String(x || "").trim())
      .filter((x) => x.length >= 2);

    // For English/European lemmas >= 5 chars, also allow stem prefix match (e.g. linger -> lingered)
    let regex = null;
    for (const cand of candidates) {
      const escaped = cand.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
      if (/^[A-Za-z]{4,}$/.test(cand)) {
        const stem = cand.length >= 6 ? cand.slice(0, -1).replace(/[.*+?^${}()|[\]\\]/g, "\\$&") : escaped;
        const r = new RegExp("(\\b" + escaped + "\\w*|\\b" + stem + "[A-Za-z]{1,4}\\b)", "gi");
        if (r.test(src)) {
          regex = new RegExp("(\\b" + escaped + "\\w*|\\b" + stem + "[A-Za-z]{1,4}\\b)", "gi");
          break;
        }
      } else {
        const r = new RegExp("(" + escaped + ")", "gi");
        if (r.test(src)) {
          regex = new RegExp("(" + escaped + ")", "gi");
          break;
        }
      }
    }

    if (!regex) {
      container.appendChild(document.createTextNode(src));
      return;
    }

    let lastIdx = 0;
    let m;
    while ((m = regex.exec(src)) !== null) {
      if (m.index > lastIdx) {
        container.appendChild(document.createTextNode(src.slice(lastIdx, m.index)));
      }
      const mark = document.createElement("mark");
      mark.className = "bit-hl";
      mark.textContent = m[0];
      container.appendChild(mark);
      lastIdx = m.index + m[0].length;
      if (m[0].length === 0) break;
    }
    if (lastIdx < src.length) {
      container.appendChild(document.createTextNode(src.slice(lastIdx)));
    }
  }

  function applyScale(nextScale) {
    textScale = Math.max(0.85, Math.min(1.35, Math.round(nextScale * 20) / 20));
    document.documentElement.style.setProperty("--bit-scale", String(textScale));
    if (isStudyOpen() && !studyDragged) placeStudyPanel();
  }

  function handleScaleWheel(e) {
    if (!(e.ctrlKey || e.metaKey)) return;
    e.preventDefault();
    e.stopPropagation();
    applyScale(textScale + (e.deltaY < 0 ? 0.05 : -0.05));
  }

  // ---- Primary Translation Panel -----------------------------------------------------------

  function ensurePanel() {
    if (panel && document.documentElement.contains(panel)) return panel;

    panel = document.createElement("div");
    panel.id = PANEL_ID;
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "Bili Index Translate");
    panel.addEventListener("wheel", handleScaleWheel, { passive: false });

    const header = document.createElement("div");
    header.className = "bit-header";

    const title = document.createElement("span");
    title.className = "bit-title";
    const dot = document.createElement("span");
    dot.className = "bit-title-dot";
    const titleTxt = document.createElement("span");
    titleTxt.textContent = "Bili Translate";
    title.appendChild(dot);
    title.appendChild(titleTxt);

    const actions = document.createElement("div");
    actions.className = "bit-header-actions";

    mainSpeakBtn = document.createElement("button");
    mainSpeakBtn.className = "bit-icon-btn";
    mainSpeakBtn.type = "button";
    mainSpeakBtn.title = "朗读选中原文";
    mainSpeakBtn.appendChild(makeIcon("speaker"));
    mainSpeakBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      if (currentSourceText) speakText(currentSourceText, inferQuickLang(currentSourceText), false);
    });

    mainCopyBtn = document.createElement("button");
    mainCopyBtn.className = "bit-icon-btn";
    mainCopyBtn.type = "button";
    mainCopyBtn.title = "复制译文（快捷键 C）";
    mainCopyBtn.appendChild(makeIcon("copy"));
    mainCopyBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      if (currentTranslatedText) copyToClipboard(currentTranslatedText, mainCopyBtn, "");
    });

    studyTriggerBtn = document.createElement("button");
    studyTriggerBtn.className = "bit-study-trigger";
    studyTriggerBtn.type = "button";
    studyTriggerBtn.title = "展开/收起伴随语言学习卡片（快捷键 S）";
    studyTriggerBtn.setAttribute("aria-label", "打开语言学习卡片");
    studyTriggerBtn.appendChild(makeIcon("book"));
    const studyLabel = document.createElement("span");
    studyLabel.textContent = "\u5b66\u4e60\u5361\u7247";
    studyTriggerBtn.appendChild(studyLabel);

    studyTriggerBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      toggleStudyCard();
    });

    const closeBtn = document.createElement("button");
    closeBtn.className = "bit-close";
    closeBtn.type = "button";
    closeBtn.setAttribute("aria-label", "Close");
    closeBtn.textContent = "\u00d7";
    closeBtn.addEventListener("click", hideAll);

    actions.appendChild(mainSpeakBtn);
    actions.appendChild(mainCopyBtn);
    actions.appendChild(studyTriggerBtn);
    actions.appendChild(closeBtn);

    header.appendChild(title);
    header.appendChild(actions);

    bodyEl = document.createElement("div");
    bodyEl.className = "bit-body";

    footEl = document.createElement("div");
    footEl.className = "bit-foot";

    panel.appendChild(header);
    panel.appendChild(bodyEl);
    panel.appendChild(footEl);

    ["mousedown", "mouseup", "click", "dblclick"].forEach((ev) => {
      panel.addEventListener(ev, (e) => e.stopPropagation());
    });

    enableDrag(header, panel, () => {
      dragged = true;
      if (isStudyOpen() && !studyDragged) placeStudyPanel();
    });

    document.documentElement.appendChild(panel);
    return panel;
  }

  // ---- Companion Language Study Card Panel -------------------------------------------------

  function ensureStudyPanel() {
    if (studyPanel && document.documentElement.contains(studyPanel)) return studyPanel;

    studyPanel = document.createElement("div");
    studyPanel.id = STUDY_PANEL_ID;
    studyPanel.setAttribute("role", "dialog");
    studyPanel.setAttribute("aria-label", "Bili Language Study Card");
    if (compactStudyMode) studyPanel.setAttribute("data-compact", "");
    studyPanel.addEventListener("wheel", handleScaleWheel, { passive: false });

    const header = document.createElement("div");
    header.className = "bit-study-header";

    const titleWrap = document.createElement("div");
    titleWrap.className = "bit-study-title";
    titleWrap.appendChild(makeIcon("book"));
    const titleText = document.createElement("span");
    titleText.textContent = "\u8bed\u8a00\u5b66\u4e60\u5361\u7247";
    studyLangBadge = document.createElement("span");
    studyLangBadge.className = "bit-lang-badge";
    studyLangBadge.style.display = "none";
    titleWrap.appendChild(titleText);
    titleWrap.appendChild(studyLangBadge);

    const actions = document.createElement("div");
    actions.className = "bit-study-actions";

    studyCompactBtn = document.createElement("button");
    studyCompactBtn.className = "bit-pill-ctrl";
    studyCompactBtn.type = "button";
    syncCompactBtn();
    studyCompactBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      toggleCompactMode();
    });

    studySpeedBtn = document.createElement("button");
    studySpeedBtn.className = "bit-pill-ctrl";
    studySpeedBtn.type = "button";
    syncSpeedBtn();
    studySpeedBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      toggleStudyModel();
    });

    studyQuizBtn = document.createElement("button");
    studyQuizBtn.className = "bit-pill-ctrl";
    studyQuizBtn.type = "button";
    studyQuizBtn.title = "主动回忆自测翻转（快捷键 F）";
    studyQuizBtn.textContent = "\u81ea\u6d4b";
    studyQuizBtn.style.display = "none";
    studyQuizBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      toggleQuizMode();
    });

    studyCopyBtn = document.createElement("button");
    studyCopyBtn.className = "bit-icon-btn";
    studyCopyBtn.type = "button";
    studyCopyBtn.title = "复制当前词卡笔记（快捷键 C）";
    studyCopyBtn.appendChild(makeIcon("copy"));
    studyCopyBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      copyActiveStudyCard();
    });

    const closeBtn = document.createElement("button");
    closeBtn.className = "bit-close";
    closeBtn.type = "button";
    closeBtn.setAttribute("aria-label", "关闭学习卡片");
    closeBtn.textContent = "\u00d7";
    closeBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      hideStudyCard();
    });

    actions.appendChild(studyCompactBtn);
    actions.appendChild(studySpeedBtn);
    actions.appendChild(studyQuizBtn);
    actions.appendChild(studyCopyBtn);
    actions.appendChild(closeBtn);

    header.appendChild(titleWrap);
    header.appendChild(actions);

    studyBodyEl = document.createElement("div");
    studyBodyEl.className = "bit-study-body";

    const foot = document.createElement("div");
    foot.className = "bit-study-foot";
    studyFootLeftEl = document.createElement("span");
    studyFootRightEl = document.createElement("span");
    studyFootRightEl.className = "bit-kbd-hint";
    foot.appendChild(studyFootLeftEl);
    foot.appendChild(studyFootRightEl);

    studyPanel.appendChild(header);
    studyPanel.appendChild(studyBodyEl);
    studyPanel.appendChild(foot);

    ["mousedown", "mouseup", "click", "dblclick"].forEach((ev) => {
      studyPanel.addEventListener(ev, (e) => e.stopPropagation());
    });

    enableDrag(header, studyPanel, () => {
      studyDragged = true;
    });

    document.documentElement.appendChild(studyPanel);
    return studyPanel;
  }

  function syncCompactBtn() {
    if (!studyCompactBtn) return;
    if (compactStudyMode) {
      studyCompactBtn.setAttribute("data-active", "");
      studyCompactBtn.textContent = "\u7cbe\u7b80";
      studyCompactBtn.title = "当前精简视图：仅显示释义与例句（点击或按 D 展开详尽辨析与助记）";
    } else {
      studyCompactBtn.removeAttribute("data-active");
      studyCompactBtn.textContent = "\u8be6\u5c3d";
      studyCompactBtn.title = "当前详尽视图：显示辨析、助记与搭配（点击或按 D 切换精简视图）";
    }
  }

  function toggleCompactMode() {
    compactStudyMode = !compactStudyMode;
    syncCompactBtn();
    if (studyPanel) {
      if (compactStudyMode) studyPanel.setAttribute("data-compact", "");
      else studyPanel.removeAttribute("data-compact");
      if (!studyDragged) placeStudyPanel();
    }
  }

  function syncSpeedBtn() {
    if (!studySpeedBtn) return;
    if (preferFlagshipStudyModel) {
      studySpeedBtn.setAttribute("data-active", "");
      studySpeedBtn.textContent = "35B";
      studySpeedBtn.title = "当前 35B-A3B 旗舰模式（点击或按 M 切回 9B 均衡模式）";
    } else {
      studySpeedBtn.removeAttribute("data-active");
      studySpeedBtn.textContent = "9B";
      studySpeedBtn.title = "当前 9B 均衡模式（点击或按 M 切换 35B-A3B 旗舰精析）";
    }
  }

  function toggleStudyModel() {
    preferFlagshipStudyModel = !preferFlagshipStudyModel;
    syncSpeedBtn();
    if (studyState && studyState.sourceText) {
      openStudyCard(studyState.sourceText, studyState.anchor, studyDragged);
    }
  }

  function toggleQuizMode() {
    if (!studyState || !studyState.deck) return;
    studyState.quizMode = !studyState.quizMode;
    studyState.revealedIndices = {};
    if (studyState.quizMode) {
      studyQuizBtn.setAttribute("data-active", "");
      studyQuizBtn.textContent = "\u9000\u51fa\u81ea\u6d4b";
    } else {
      studyQuizBtn.removeAttribute("data-active");
      studyQuizBtn.textContent = "\u81ea\u6d4b";
    }
    renderStudyDeck(true);
  }

  function copyActiveStudyCard() {
    if (!studyState || !studyState.deck || !studyState.deck.cards.length) return;
    const idx = Math.max(0, Math.min(studyState.cardIndex || 0, studyState.deck.cards.length - 1));
    const md = formatStudyCardMarkdown(studyState.deck.cards[idx], studyState.deck.grammarNote);
    copyToClipboard(md, studyCopyBtn, "\u5df2\u590d\u5236\u8bcd\u5361");
  }

  function enableDrag(handle, targetEl, onStartDrag) {
    handle.addEventListener("mousedown", (e) => {
      if (e.button !== 0 || e.target.closest("button")) return;
      e.preventDefault();
      if (onStartDrag) onStartDrag();
      const startRect = targetEl.getBoundingClientRect();
      const dx = e.clientX - startRect.left;
      const dy = e.clientY - startRect.top;

      function onMove(ev) {
        const w = targetEl.offsetWidth;
        const h = targetEl.offsetHeight;
        const left = clamp(ev.clientX - dx, VIEWPORT_GAP, window.innerWidth - w - VIEWPORT_GAP);
        const top = clamp(ev.clientY - dy, VIEWPORT_GAP, window.innerHeight - h - VIEWPORT_GAP);
        targetEl.style.left = left + "px";
        targetEl.style.top = top + "px";
      }
      function onUp() {
        document.removeEventListener("mousemove", onMove, true);
        document.removeEventListener("mouseup", onUp, true);
      }
      document.addEventListener("mousemove", onMove, true);
      document.addEventListener("mouseup", onUp, true);
    });
  }

  function clamp(v, lo, hi) {
    return Math.max(lo, Math.min(Math.max(lo, hi), v));
  }

  function isOpen() {
    return Boolean(panel && panel.hasAttribute("data-open"));
  }

  function isStudyOpen() {
    return Boolean(studyPanel && studyPanel.hasAttribute("data-open"));
  }

  function stopSpeech() {
    try {
      if ("speechSynthesis" in window) {
        window.speechSynthesis.cancel();
      }
    } catch (_) {}
  }

  function speakText(text, langTag, slow) {
    if (!text) return;
    stopSpeech();
    if (!("speechSynthesis" in window) || typeof SpeechSynthesisUtterance === "undefined") {
      flashStudyFoot("当前浏览器不支持语音合成");
      return;
    }
    try {
      const utter = new SpeechSynthesisUtterance(text);
      if (langTag) {
        utter.lang = langTag;
        const voices = window.speechSynthesis.getVoices ? window.speechSynthesis.getVoices() : [];
        const prefix = langTag.split("-")[0].toLowerCase();
        const match =
          voices.find((v) => v.lang && v.lang.toLowerCase() === langTag.toLowerCase()) ||
          voices.find((v) => v.lang && v.lang.toLowerCase().startsWith(prefix));
        if (match) utter.voice = match;
      }
      utter.rate = slow ? 0.68 : 0.96;
      window.speechSynthesis.speak(utter);
      if (slow && isStudyOpen()) flashStudyFoot("0.7x 慢读跟读中…");
    } catch (_) {
      flashStudyFoot("语音播放受限");
    }
  }

  function setStudyFoot(left, right) {
    if (!studyFootLeftEl || !studyFootRightEl) return;
    studyFootLeftEl.textContent = left || "";
    studyFootLeftEl.setAttribute("data-base-left", left || "");
    if (right !== undefined) studyFootRightEl.textContent = right;
  }

  function flashStudyFoot(tempMsg) {
    if (!studyFootLeftEl) return;
    const prev = studyFootLeftEl.getAttribute("data-base-left") || studyFootLeftEl.textContent;
    studyFootLeftEl.textContent = tempMsg;
    clearTimeout(footFlashTimer);
    footFlashTimer = setTimeout(() => {
      if (studyFootLeftEl) studyFootLeftEl.textContent = prev;
    }, 1500);
  }

  function disconnectTranslateStream() {
    if (translatePort) {
      try { translatePort.disconnect(); } catch (_) {}
      translatePort = null;
    }
  }

  function disconnectStudyStream() {
    if (studyPort) {
      try { studyPort.disconnect(); } catch (_) {}
      studyPort = null;
    }
  }

  function hideStudyCard() {
    studyReqSeq++;
    disconnectStudyStream();
    stopSpeech();
    studyState = null;
    studyDragged = false;
    if (studyTriggerBtn) studyTriggerBtn.removeAttribute("data-active");
    if (studyPanel) {
      studyPanel.removeAttribute("data-open");
      if (studyBodyEl) studyBodyEl.textContent = "";
      if (studyFootLeftEl) {
        studyFootLeftEl.textContent = "";
        studyFootLeftEl.removeAttribute("data-base-left");
      }
      if (studyFootRightEl) studyFootRightEl.textContent = "";
    }
  }

  function hideAll() {
    reqSeq++;
    disconnectTranslateStream();
    hideStudyCard();
    currentSourceText = "";
    currentTranslatedText = "";
    currentAnchor = null;
    dragged = false;
    if (panel) {
      panel.removeAttribute("data-open");
      if (bodyEl) bodyEl.textContent = "";
      if (footEl) footEl.textContent = "";
    }
  }

  function placePanel(anchor) {
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    panel.style.maxWidth = Math.min(390, vw - VIEWPORT_GAP * 2) + "px";

    const w = panel.offsetWidth;
    const h = panel.offsetHeight;

    let top = anchor.bottom + 8;
    if (top + h > vh - VIEWPORT_GAP && anchor.top - h - 8 >= VIEWPORT_GAP) {
      top = anchor.top - h - 8;
    }
    top = clamp(top, VIEWPORT_GAP, vh - h - VIEWPORT_GAP);
    const left = clamp(anchor.left, VIEWPORT_GAP, vw - w - VIEWPORT_GAP);

    panel.style.left = Math.round(left) + "px";
    panel.style.top = Math.round(top) + "px";

    if (isStudyOpen() && !studyDragged) {
      placeStudyPanel();
    }
  }

  function placeStudyPanel() {
    if (!studyPanel || !isStudyOpen()) return;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    studyPanel.style.maxWidth = Math.min(404, vw - VIEWPORT_GAP * 2) + "px";

    const sw = studyPanel.offsetWidth;
    const sh = studyPanel.offsetHeight;

    if (isOpen() && panel) {
      const mainRect = panel.getBoundingClientRect();
      let left;
      let top = mainRect.top;

      if (mainRect.right + COMPANION_GAP + sw <= vw - VIEWPORT_GAP) {
        left = mainRect.right + COMPANION_GAP;
      } else if (mainRect.left - COMPANION_GAP - sw >= VIEWPORT_GAP) {
        left = mainRect.left - COMPANION_GAP - sw;
      } else {
        left = clamp(mainRect.left, VIEWPORT_GAP, vw - sw - VIEWPORT_GAP);
        if (mainRect.bottom + COMPANION_GAP + sh <= vh - VIEWPORT_GAP) {
          top = mainRect.bottom + COMPANION_GAP;
        } else {
          top = mainRect.top - COMPANION_GAP - sh;
        }
      }

      left = clamp(left, VIEWPORT_GAP, vw - sw - VIEWPORT_GAP);
      top = clamp(top, VIEWPORT_GAP, vh - sh - VIEWPORT_GAP);
      studyPanel.style.left = Math.round(left) + "px";
      studyPanel.style.top = Math.round(top) + "px";
      return;
    }

    const anchor = (studyState && studyState.anchor) || {
      left: Math.round((vw - sw) / 2),
      top: 90,
      bottom: 90
    };
    const left = clamp(anchor.left, VIEWPORT_GAP, vw - sw - VIEWPORT_GAP);
    const top = clamp(anchor.bottom + 8, VIEWPORT_GAP, vh - sh - VIEWPORT_GAP);
    studyPanel.style.left = Math.round(left) + "px";
    studyPanel.style.top = Math.round(top) + "px";
  }

  function syncSourceButtons() {
    const hasSrc = Boolean(currentSourceText && currentSourceText.trim());
    if (mainSpeakBtn) {
      if (hasSrc) mainSpeakBtn.removeAttribute("data-disabled");
      else mainSpeakBtn.setAttribute("data-disabled", "");
    }
    if (studyTriggerBtn) {
      if (hasSrc) studyTriggerBtn.removeAttribute("data-disabled");
      else studyTriggerBtn.setAttribute("data-disabled", "");
    }
  }

  function showLoading(anchor, isVision) {
    ensurePanel();
    dragged = false;
    currentTranslatedText = "";
    syncSourceButtons();
    bodyEl.innerHTML = "";
    const loading = document.createElement("div");
    loading.className = "bit-loading";
    loading.textContent = isVision ? "\u8bc6\u56fe\u76f4\u8bd1\u4e2d\u2026" : "Translating\u2026";
    bodyEl.appendChild(loading);
    footEl.textContent = "";
    panel.setAttribute("data-open", "");
    placePanel(anchor);
  }

  function formatError(res) {
    if (!res) return "No response from background worker.";
    const head = res.status ? "HTTP " + res.status : "Request failed";
    let hint = "";
    if (res.status === 412) {
      hint = "\n(Upstream blocked the request; check the Relay prefix in extension options.)";
    }
    return head + (res.body ? "\n" + res.body : "") + hint;
  }

  function renderMainStreamingDelta(deltaRes, anchor) {
    currentTranslatedText = deltaRes.text || "";
    if (deltaRes.sourceText && !currentSourceText) {
      currentSourceText = deltaRes.sourceText;
      syncSourceButtons();
    }
    bodyEl.textContent = currentTranslatedText;
    const streamLabel = deltaRes.vision ? " \u00b7 \u8bc6\u56fe\u6d41\u5f0f\u4e2d\u2026" : " \u00b7 \u6d41\u5f0f\u8f93\u51fa\u4e2d\u2026";
    footEl.textContent = (deltaRes.model || "") + streamLabel;
    if (!dragged) placePanel(anchor);
  }

  function showResult(res, anchor) {
    if (res && res.ok) {
      currentTranslatedText = res.text || "";
      if (res.sourceText) {
        currentSourceText = res.sourceText;
      }
      syncSourceButtons();

      bodyEl.innerHTML = "";
      const mainTxt = document.createElement("div");
      mainTxt.textContent = currentTranslatedText;
      bodyEl.appendChild(mainTxt);

      if (res.vision && currentSourceText) {
        const srcRow = document.createElement("div");
        srcRow.className = "bit-vision-src";
        const tag = document.createElement("span");
        tag.className = "bit-vision-src-tag";
        tag.textContent = "\u539f\u6587";
        const span = document.createElement("span");
        span.textContent = currentSourceText;
        srcRow.appendChild(tag);
        srcRow.appendChild(span);
        bodyEl.appendChild(srcRow);
      }

      const visionTag = res.vision ? " \u00b7 \u8bc6\u56fe" : "";
      const glossaryTag =
        res.glossaryHits && res.glossaryHits > 0 ? " \u00b7 \u672f\u8bed\u00d7" + res.glossaryHits : "";
      const left =
        (res.model || "") +
        visionTag +
        glossaryTag +
        (res.cached ? " \u00b7 \u26a1\u77ac\u65f6" : res.via ? " \u00b7 " + res.via : "");
      footEl.textContent = "";
      const lSpan = document.createElement("span");
      lSpan.textContent = left;
      const rSpan = document.createElement("span");
      rSpan.textContent = currentSourceText ? "S \u8bcd\u5361" : "";
      footEl.appendChild(lSpan);
      footEl.appendChild(rSpan);
    } else {
      currentTranslatedText = "";
      syncSourceButtons();
      bodyEl.innerHTML = "";
      const err = document.createElement("div");
      err.className = "bit-error";
      err.textContent = formatError(res);
      bodyEl.appendChild(err);
      footEl.textContent = res && res.via ? "via " + res.via : "";
    }
    if (!dragged) placePanel(anchor);
  }

  // ---- Study Card Rendering & Surgical Streaming Updates -----------------------------------

  function toggleStudyCard() {
    if (isStudyOpen()) {
      hideStudyCard();
      return;
    }
    if (!currentSourceText) return;
    openStudyCard(currentSourceText, currentAnchor, false);
  }

  function buildSkeletonLoader() {
    const wrap = document.createElement("div");
    wrap.className = "bit-skeleton";
    ["title", "mid", "full"].forEach((w) => {
      const b = document.createElement("div");
      b.className = "bit-skel-bar";
      b.setAttribute("data-w", w);
      wrap.appendChild(b);
    });
    return wrap;
  }

  function showStudyLoading() {
    ensureStudyPanel();
    syncCompactBtn();
    syncSpeedBtn();
    studyLangBadge.style.display = "none";
    studyQuizBtn.style.display = "none";
    studyQuizBtn.removeAttribute("data-active");
    studyQuizBtn.textContent = "\u81ea\u6d4b";

    studyBodyEl.textContent = "";
    studyBodyEl.appendChild(buildSkeletonLoader());

    setStudyFoot(
      preferFlagshipStudyModel ? "35B \u65d7\u8230\u62c6\u89e3\u4e2d\u2026" : "9B \u6d41\u5f0f\u62c6\u89e3\u4e2d\u2026",
      "\u96f6\u7559\u75d5"
    );
    studyPanel.setAttribute("data-open", "");
    if (studyTriggerBtn) studyTriggerBtn.setAttribute("data-active", "");
    placeStudyPanel();
  }

  function showStudyError(res) {
    ensureStudyPanel();
    studyBodyEl.textContent = "";

    const err = document.createElement("div");
    err.className = "bit-error";
    err.textContent = formatError(res);

    const retry = document.createElement("button");
    retry.className = "bit-retry-btn";
    retry.type = "button";
    retry.textContent = "\u91cd\u8bd5";
    retry.addEventListener("click", () => {
      if (studyState && studyState.sourceText) {
        openStudyCard(studyState.sourceText, studyState.anchor, studyDragged);
      }
    });

    studyBodyEl.appendChild(err);
    studyBodyEl.appendChild(retry);
    setStudyFoot(res && res.via ? "via " + res.via : "", "");
    placeStudyPanel();
  }

  function stepCard(delta) {
    if (!studyState || !studyState.deck || !studyState.deck.cards) return;
    const n = studyState.deck.cards.length;
    if (n <= 1) return;
    stopSpeech();
    studyState.cardIndex = ((studyState.cardIndex || 0) + delta + n) % n;
    renderStudyDeck(true);
  }

  function selectCardIndex(idx) {
    if (!studyState || !studyState.deck || !studyState.deck.cards) return;
    if (idx < 0 || idx >= studyState.deck.cards.length || idx === studyState.cardIndex) return;
    stopSpeech();
    studyState.cardIndex = idx;
    renderStudyDeck(true);
  }

  function buildNavBar(cards, activeIdx) {
    const nav = document.createElement("div");
    nav.className = "bit-nav-bar";

    const chipList = document.createElement("div");
    chipList.className = "bit-chip-list";
    cards.forEach((c, i) => {
      const chip = document.createElement("button");
      chip.className = "bit-word-chip";
      chip.type = "button";
      if (i === activeIdx) chip.setAttribute("data-active", "");
      chip.textContent = i + 1 + ". " + c.term;
      chip.addEventListener("click", () => selectCardIndex(i));
      chipList.appendChild(chip);
    });

    const stepGroup = document.createElement("div");
    stepGroup.className = "bit-step-group";

    const prevBtn = document.createElement("button");
    prevBtn.className = "bit-step-btn";
    prevBtn.type = "button";
    prevBtn.title = "上一个词 (←)";
    prevBtn.textContent = "\u2039";
    prevBtn.addEventListener("click", () => stepCard(-1));

    const nextBtn = document.createElement("button");
    nextBtn.className = "bit-step-btn";
    nextBtn.type = "button";
    nextBtn.title = "下一个词 (→)";
    nextBtn.textContent = "\u203a";
    nextBtn.addEventListener("click", () => stepCard(1));

    stepGroup.appendChild(prevBtn);
    stepGroup.appendChild(nextBtn);
    nav.appendChild(chipList);
    nav.appendChild(stepGroup);
    return nav;
  }

  function makeEditorialRow(kind, labelText, valueNodeOrString) {
    const row = document.createElement("div");
    row.className = "bit-row";
    const tag = document.createElement("span");
    tag.className = "bit-row-tag";
    tag.setAttribute("data-kind", kind);
    tag.textContent = labelText;

    const val = document.createElement("div");
    val.className = "bit-row-val";
    if (typeof valueNodeOrString === "string") {
      val.textContent = valueNodeOrString;
    } else if (valueNodeOrString) {
      val.appendChild(valueNodeOrString);
    }

    row.appendChild(tag);
    row.appendChild(val);
    return row;
  }

  function updateStudyFooterMeta() {
    if (!studyState || !studyState.deck) return;
    const deck = studyState.deck;
    const cards = deck.cards || [];
    const idx = Math.max(0, Math.min(studyState.cardIndex || 0, Math.max(0, cards.length - 1)));

    const leftParts = [];
    if (cards.length > 1) leftParts.push(idx + 1 + "/" + cards.length);
    if (studyState.model) {
      leftParts.push(studyState.model.replace("Index-Translate-", ""));
    }
    if (deck.streaming) leftParts.push("\u6b63\u5728\u52a0\u8f7d\u5176\u4f59\u8bcd\u5361\u2026");
    else if (studyState.cached) leftParts.push("\u26a1\u77ac\u65f6");
    else if (studyState.via) leftParts.push(studyState.via);

    setStudyFoot(leftParts.join(" \u00b7 "), "Space\u6717\u8bfb \u00b7 F\u81ea\u6d4b \u00b7 D\u7cbe\u7b80");
  }

  // Surgical update when background SSE cards arrive while user is reading active card!
  function renderStudyDeck(forceFullRebuild) {
    if (!studyState || !studyState.deck) return;
    ensureStudyPanel();

    const deck = studyState.deck;
    const cards = deck.cards || [];
    const idx = Math.max(0, Math.min(studyState.cardIndex || 0, Math.max(0, cards.length - 1)));
    studyState.cardIndex = idx;

    studyLangBadge.textContent = (deck.sourceLang || "AUTO").toUpperCase();
    studyLangBadge.style.display = "inline-flex";
    studyQuizBtn.style.display = cards.length > 0 ? "inline-flex" : "none";

    // Surgical path: if active card (idx + term) is ALREADY rendered in DOM and only additional
    // sibling cards streamed in, patch ONLY the navigation bar & footer — zero DOM teardown!
    const activeCard = cards[idx];
    const renderedKey = studyBodyEl.getAttribute("data-rendered-card");
    const nextKey = activeCard
      ? idx + ":" + activeCard.term + ":" + (activeCard.example ? activeCard.example.source : "")
      : "";

    if (!forceFullRebuild && activeCard && renderedKey === nextKey) {
      const existingNav = studyBodyEl.querySelector(".bit-nav-bar");
      if (cards.length > 1) {
        const freshNav = buildNavBar(cards, idx);
        if (existingNav) existingNav.replaceWith(freshNav);
        else {
          const anchorEl = studyBodyEl.querySelector(".bit-head-row");
          if (anchorEl) studyBodyEl.insertBefore(freshNav, anchorEl);
        }
      }
      updateStudyFooterMeta();
      if (!studyDragged) placeStudyPanel();
      return;
    }

    const prevScroll = studyBodyEl.scrollTop;
    studyBodyEl.textContent = "";
    studyBodyEl.setAttribute("data-rendered-card", nextKey);

    // 1. Sentence Grammar Note
    if (deck.grammarNote) {
      const gBox = document.createElement("div");
      gBox.className = "bit-grammar-box";
      const gLabel = document.createElement("span");
      gLabel.className = "bit-grammar-label";
      gLabel.textContent = "\u53e5\u6cd5";
      const gText = document.createElement("span");
      gText.textContent = deck.grammarNote;
      gBox.appendChild(gLabel);
      gBox.appendChild(gText);
      studyBodyEl.appendChild(gBox);
    }

    if (cards.length === 0) {
      studyBodyEl.appendChild(buildSkeletonLoader());
      updateStudyFooterMeta();
      if (!studyDragged) placeStudyPanel();
      return;
    }

    const card = activeCard;
    const masked = !!(studyState.quizMode && !(studyState.revealedIndices && studyState.revealedIndices[idx]));

    // 2. Multi-card Flashcard Navigation
    if (cards.length > 1) {
      studyBodyEl.appendChild(buildNavBar(cards, idx));
    }

    // 3. Editorial Hero Row (Headword + Phonetic + Segmented Audio)
    const headRow = document.createElement("div");
    headRow.className = "bit-head-row";

    const headMain = document.createElement("div");
    headMain.className = "bit-head-main";

    const termLine = document.createElement("div");
    termLine.className = "bit-term-line";

    const termEl = document.createElement("span");
    termEl.className = "bit-term";
    termEl.textContent = card.term;
    termLine.appendChild(termEl);

    if (card.surface) {
      const surfEl = document.createElement("span");
      surfEl.className = "bit-surface-tag";
      surfEl.textContent = "\u6587\u4e2d: " + card.surface;
      termLine.appendChild(surfEl);
    }
    headMain.appendChild(termLine);

    if (card.pos || card.register || card.reading) {
      const metaLine = document.createElement("div");
      metaLine.className = "bit-meta-line";
      if (card.pos) {
        const posEl = document.createElement("span");
        posEl.className = "bit-pos-tag";
        posEl.textContent = card.pos;
        metaLine.appendChild(posEl);
      }
      if (card.register) {
        const regEl = document.createElement("span");
        regEl.className = "bit-register-tag";
        regEl.textContent = card.register;
        metaLine.appendChild(regEl);
      }
      if (card.reading) {
        const readEl = document.createElement("span");
        readEl.className = "bit-reading";
        readEl.textContent = card.reading;
        metaLine.appendChild(readEl);
      }
      headMain.appendChild(metaLine);
    }

    const ttsSeg = document.createElement("div");
    ttsSeg.className = "bit-tts-seg";

    const ttsBtn = document.createElement("button");
    ttsBtn.className = "bit-tts-btn";
    ttsBtn.type = "button";
    ttsBtn.title = "常速朗读发音（快捷键 Space）";
    ttsBtn.appendChild(makeIcon("speaker"));
    const ttsLabel = document.createElement("span");
    ttsLabel.textContent = "\u53d1\u97f3";
    ttsBtn.appendChild(ttsLabel);
    ttsBtn.addEventListener("click", () => speakText(card.term, deck.sourceLang, false));

    const slowBtn = document.createElement("button");
    slowBtn.className = "bit-tts-slow";
    slowBtn.type = "button";
    slowBtn.title = "0.7x 慢速清晰跟读（快捷键 Shift+Space）";
    slowBtn.textContent = "0.7x";
    slowBtn.addEventListener("click", () => speakText(card.term, deck.sourceLang, true));

    ttsSeg.appendChild(ttsBtn);
    ttsSeg.appendChild(slowBtn);

    headRow.appendChild(headMain);
    headRow.appendChild(ttsSeg);
    studyBodyEl.appendChild(headRow);

    // 4. Core Definition (or Self-Test Mask)
    if (card.meaning) {
      const meanBox = document.createElement("div");
      meanBox.className = "bit-meaning-box";
      if (masked) {
        meanBox.setAttribute("data-masked", "");
        meanBox.appendChild(makeIcon("eye"));
        const maskText = document.createElement("span");
        maskText.textContent = "\u70b9\u51fb\u6216\u6309 F \u63ed\u6653\u91ca\u4e49\u4e0e\u89e3\u6790";
        meanBox.appendChild(maskText);
        meanBox.addEventListener("click", () => {
          if (!studyState.revealedIndices) studyState.revealedIndices = {};
          studyState.revealedIndices[idx] = true;
          renderStudyDeck(true);
        });
      } else {
        meanBox.textContent = card.meaning;
      }
      studyBodyEl.appendChild(meanBox);
    }

    // 5. Editorial Hairline Insights (Nuance / Mnemonic / Click-to-Speak Collocations)
    if (!masked && (card.nuance || card.memoryAid || (card.collocations && card.collocations.length > 0))) {
      const insights = document.createElement("div");
      insights.className = "bit-insights";

      if (card.nuance) {
        insights.appendChild(makeEditorialRow("nuance", "\u8fa8\u6790", card.nuance));
      }
      if (card.memoryAid) {
        insights.appendChild(makeEditorialRow("memory", "\u52a9\u8bb0", card.memoryAid));
      }
      if (card.collocations && card.collocations.length > 0) {
        const colList = document.createElement("div");
        colList.className = "bit-colloc-list";
        card.collocations.forEach((item) => {
          const pill = document.createElement("button");
          pill.className = "bit-colloc-pill";
          pill.type = "button";
          pill.title = "点击朗读该搭配短语";
          pill.textContent = item;
          pill.addEventListener("click", () => {
            speakText(extractForeignPhrase(item), deck.sourceLang, false);
          });
          colList.appendChild(pill);
        });
        insights.appendChild(makeEditorialRow("colloc", "\u642d\u914d", colList));
      }
      studyBodyEl.appendChild(insights);
    }

    // 6. Context-Highlighted Bilingual Example
    if (card.example && (card.example.source || card.example.target)) {
      const exWrap = document.createElement("div");
      exWrap.className = "bit-example-wrap";

      const exBox = document.createElement("div");
      exBox.className = "bit-example-box";

      if (card.example.source) {
        const srcRow = document.createElement("div");
        srcRow.className = "bit-example-src";
        const srcSpan = document.createElement("span");
        appendHighlightedSentence(srcSpan, card.example.source, card.term, card.surface);

        const exAudio = document.createElement("div");
        exAudio.className = "bit-ex-audio";

        const exTts = document.createElement("button");
        exTts.className = "bit-tts-mini";
        exTts.type = "button";
        exTts.title = "常速朗读例句（快捷键 E）";
        exTts.appendChild(makeIcon("speaker"));
        exTts.addEventListener("click", () => speakText(card.example.source, deck.sourceLang, false));

        const exSlow = document.createElement("button");
        exSlow.className = "bit-tts-mini";
        exSlow.type = "button";
        exSlow.title = "0.7x 慢读例句（快捷键 Shift+E）";
        exSlow.textContent = "0.7x";
        exSlow.addEventListener("click", () => speakText(card.example.source, deck.sourceLang, true));

        exAudio.appendChild(exTts);
        exAudio.appendChild(exSlow);

        srcRow.appendChild(srcSpan);
        srcRow.appendChild(exAudio);
        exBox.appendChild(srcRow);
      }

      if (card.example.target && !masked) {
        const tgtDiv = document.createElement("div");
        tgtDiv.className = "bit-example-tgt";
        tgtDiv.textContent = card.example.target;
        exBox.appendChild(tgtDiv);
      }

      exWrap.appendChild(exBox);
      studyBodyEl.appendChild(exWrap);
    }

    updateStudyFooterMeta();
    studyBodyEl.scrollTop = prevScroll;
    if (!studyDragged) placeStudyPanel();
  }

  function applyStudyMessage(msg, text, anchor) {
    if (!msg) return;
    if (msg.type === "PARTIAL" && msg.deck) {
      const prevIdx = studyState ? studyState.cardIndex || 0 : 0;
      const prevQuiz = studyState ? !!studyState.quizMode : false;
      const prevRev = studyState ? studyState.revealedIndices || {} : {};
      studyState = {
        sourceText: text,
        anchor: anchor,
        deck: msg.deck,
        model: msg.model,
        via: msg.via,
        cardIndex: prevIdx,
        quizMode: prevQuiz,
        revealedIndices: prevRev
      };
      renderStudyDeck(false);
    } else if (msg.type === "DONE") {
      disconnectStudyStream();
      if (msg.ok && msg.deck) {
        const prevIdx = studyState ? studyState.cardIndex || 0 : 0;
        const prevQuiz = studyState ? !!studyState.quizMode : false;
        const prevRev = studyState ? studyState.revealedIndices || {} : {};
        studyState = {
          sourceText: text,
          anchor: anchor,
          deck: msg.deck,
          model: msg.model,
          via: msg.via,
          cached: !!msg.cached,
          cardIndex: prevIdx,
          quizMode: prevQuiz,
          revealedIndices: prevRev
        };
        renderStudyDeck(false);
      } else {
        showStudyError(msg);
      }
    }
  }

  function openStudyCard(text, anchor, keepDraggedPosition) {
    const seq = ++studyReqSeq;
    disconnectStudyStream();
    studyDragged = Boolean(keepDraggedPosition);
    studyState = {
      sourceText: text,
      anchor: anchor,
      deck: null,
      cardIndex: 0,
      quizMode: false,
      revealedIndices: {}
    };
    showStudyLoading();

    const modelOverride = preferFlagshipStudyModel ? "Index-Translate-35B-A3B" : "";

    // 1. Progressive SSE port
    if (chrome && chrome.runtime && typeof chrome.runtime.connect === "function") {
      try {
        const port = chrome.runtime.connect({ name: "bili-study-stream" });
        studyPort = port;
        port.onMessage.addListener((msg) => {
          if (seq !== studyReqSeq) return;
          applyStudyMessage(msg, text, anchor);
        });
        port.postMessage({ type: "START_STUDY_STREAM", text: text, modelOverride: modelOverride });
        return;
      } catch (_) {
        disconnectStudyStream();
      }
    }

    // 2. Fallback single-shot message
    try {
      chrome.runtime.sendMessage(
        { type: "BILI_STUDY_CARD", text: text, modelOverride: modelOverride },
        (res) => {
          if (seq !== studyReqSeq) return;
          if (chrome.runtime.lastError) {
            showStudyError({ status: 0, body: chrome.runtime.lastError.message });
            return;
          }
          applyStudyMessage({ type: "DONE", ...(res || {}) }, text, anchor);
        }
      );
    } catch (err) {
      if (seq !== studyReqSeq) return;
      showStudyError({ status: 0, body: String((err && err.message) || err) });
    }
  }

  // ---- Selection Reading & Primary Translation Trigger -------------------------------------

  function isTextControl(el) {
    if (!el || el.nodeType !== 1) return false;
    if (el.tagName === "TEXTAREA") return true;
    if (el.tagName === "INPUT") {
      const t = (el.type || "text").toLowerCase();
      return t === "text" || t === "search" || t === "url" || t === "tel" || t === "email";
    }
    return false;
  }

  function isEditableFocus() {
    const a = document.activeElement;
    if (!a) return false;
    if (a.tagName === "INPUT" || a.tagName === "TEXTAREA" || a.tagName === "SELECT") return true;
    if (a.isContentEditable) return true;
    return false;
  }

  function readSelection(mouseEvent) {
    const active = document.activeElement;
    if (isTextControl(active)) {
      const start = active.selectionStart;
      const end = active.selectionEnd;
      if (typeof start === "number" && typeof end === "number" && end > start) {
        const text = active.value.slice(start, end).trim();
        const r = active.getBoundingClientRect();
        const anchor = mouseEvent
          ? { left: mouseEvent.clientX, top: mouseEvent.clientY - 16, bottom: mouseEvent.clientY + 12 }
          : { left: r.left, top: r.top, bottom: r.bottom };
        return { text: text, anchor: anchor };
      }
    }

    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return null;

    const range = sel.getRangeAt(0);
    // Strip injected immersive bilingual spans if the user triple-clicked a bilingual paragraph
    let text = "";
    try {
      const frag = range.cloneContents();
      frag.querySelectorAll("[data-bit-imm]").forEach((el) => el.remove());
      text = (frag.textContent || "").trim();
    } catch (_) {
      text = sel.toString().trim();
    }
    if (!text) return null;

    let rect = range.getBoundingClientRect();
    if (!rect || (rect.width === 0 && rect.height === 0)) {
      const list = range.getClientRects();
      if (list.length) rect = list[0];
    }
    if (!rect || (rect.width === 0 && rect.height === 0)) {
      if (!mouseEvent) return null;
      rect = { left: mouseEvent.clientX, top: mouseEvent.clientY, bottom: mouseEvent.clientY };
    }
    return { text: text, anchor: { left: rect.left, top: rect.top, bottom: rect.bottom } };
  }

  function triggerTranslate(text, anchor, autoOpenStudy) {
    text = String(text || "").trim();
    if (!text) return;

    const seq = ++reqSeq;
    disconnectTranslateStream();
    if (isStudyOpen() && (!studyState || studyState.sourceText !== text)) {
      hideStudyCard();
    }

    currentSourceText = text;
    currentAnchor = anchor;
    showLoading(anchor);

    if (autoOpenStudy) {
      openStudyCard(text, anchor, false);
    }

    if (chrome && chrome.runtime && typeof chrome.runtime.connect === "function") {
      try {
        const port = chrome.runtime.connect({ name: "bili-translate-stream" });
        translatePort = port;
        port.onMessage.addListener((msg) => {
          if (seq !== reqSeq || !msg) return;
          if (msg.type === "DELTA") {
            renderMainStreamingDelta(msg, anchor);
          } else if (msg.type === "DONE") {
            disconnectTranslateStream();
            showResult(msg, anchor);
          }
        });
        port.postMessage({ type: "START_TRANSLATE_STREAM", text: text });
        return;
      } catch (_) {
        disconnectTranslateStream();
      }
    }

    try {
      chrome.runtime.sendMessage({ type: "BILI_TRANSLATE", text: text }, (res) => {
        if (seq !== reqSeq) return;
        if (chrome.runtime.lastError) {
          showResult({ ok: false, status: 0, body: chrome.runtime.lastError.message }, anchor);
          return;
        }
        showResult(res, anchor);
      });
    } catch (err) {
      if (seq !== reqSeq) return;
      showResult({ ok: false, status: 0, body: String((err && err.message) || err) }, anchor);
    }
  }

  // ==========================================================================================
  // Zero-OCR Multimodal Vision Snip (Alt+S) & Right-Click Image Translation
  // ==========================================================================================

  function downscaleCanvasDims(w, h) {
    const maxEdge = Math.max(w, h);
    if (maxEdge <= MAX_SNIP_EDGE_PX) return { width: Math.max(1, Math.round(w)), height: Math.max(1, Math.round(h)) };
    const ratio = MAX_SNIP_EDGE_PX / maxEdge;
    return {
      width: Math.max(1, Math.round(w * ratio)),
      height: Math.max(1, Math.round(h * ratio))
    };
  }

  function cropDataUrlToRect(fullDataUrl, cssRect) {
    return new Promise((resolve, reject) => {
      const img = new Image();
      img.onload = () => {
        try {
          const scaleX = img.naturalWidth / Math.max(1, window.innerWidth);
          const scaleY = img.naturalHeight / Math.max(1, window.innerHeight);
          const sx = Math.max(0, Math.round(cssRect.left * scaleX));
          const sy = Math.max(0, Math.round(cssRect.top * scaleY));
          const sw = Math.min(img.naturalWidth - sx, Math.max(1, Math.round(cssRect.width * scaleX)));
          const sh = Math.min(img.naturalHeight - sy, Math.max(1, Math.round(cssRect.height * scaleY)));
          const outDims = downscaleCanvasDims(sw, sh);

          const canvas = document.createElement("canvas");
          canvas.width = outDims.width;
          canvas.height = outDims.height;
          const ctx = canvas.getContext("2d");
          ctx.drawImage(img, sx, sy, sw, sh, 0, 0, outDims.width, outDims.height);
          resolve(canvas.toDataURL("image/png"));
        } catch (err) {
          reject(err);
        }
      };
      img.onerror = () => reject(new Error("Failed to decode captured viewport image."));
      img.src = fullDataUrl;
    });
  }

  // Fallback for sandbox / environments without chrome.tabs.captureVisibleTab:
  // crops overlapping <canvas> or <img> elements directly in RAM.
  function cropDomVisualFallback(cssRect) {
    const candidates = document.querySelectorAll("canvas, img");
    for (let i = 0; i < candidates.length; i++) {
      const el = candidates[i];
      const r = el.getBoundingClientRect();
      if (r.width < 12 || r.height < 12) continue;
      const ix0 = Math.max(cssRect.left, r.left);
      const iy0 = Math.max(cssRect.top, r.top);
      const ix1 = Math.min(cssRect.left + cssRect.width, r.right);
      const iy1 = Math.min(cssRect.top + cssRect.height, r.bottom);
      if (ix1 - ix0 < 12 || iy1 - iy0 < 12) continue;

      try {
        const srcW = el.tagName === "CANVAS" ? el.width : el.naturalWidth;
        const srcH = el.tagName === "CANVAS" ? el.height : el.naturalHeight;
        if (!srcW || !srcH) continue;
        const scaleX = srcW / r.width;
        const scaleY = srcH / r.height;
        const sx = Math.max(0, Math.round((ix0 - r.left) * scaleX));
        const sy = Math.max(0, Math.round((iy0 - r.top) * scaleY));
        const sw = Math.max(1, Math.round((ix1 - ix0) * scaleX));
        const sh = Math.max(1, Math.round((iy1 - iy0) * scaleY));
        const outDims = downscaleCanvasDims(sw, sh);
        const canvas = document.createElement("canvas");
        canvas.width = outDims.width;
        canvas.height = outDims.height;
        const ctx = canvas.getContext("2d");
        ctx.drawImage(el, sx, sy, sw, sh, 0, 0, outDims.width, outDims.height);
        return canvas.toDataURL("image/png");
      } catch (_) {}
    }
    return "";
  }

  function triggerImageTranslate(dataUrl, anchor) {
    if (!dataUrl || !dataUrl.startsWith("data:image/")) return;

    const seq = ++reqSeq;
    disconnectTranslateStream();
    if (isStudyOpen()) {
      hideStudyCard();
    }

    currentSourceText = "";
    currentAnchor = anchor;
    showLoading(anchor, true);

    if (chrome && chrome.runtime && typeof chrome.runtime.connect === "function") {
      try {
        const port = chrome.runtime.connect({ name: "bili-image-stream" });
        translatePort = port;
        port.onMessage.addListener((msg) => {
          if (seq !== reqSeq || !msg) return;
          if (msg.type === "DELTA") {
            renderMainStreamingDelta(msg, anchor);
          } else if (msg.type === "DONE") {
            disconnectTranslateStream();
            showResult(msg, anchor);
          }
        });
        port.postMessage({ type: "START_IMAGE_STREAM", dataUrl: dataUrl });
        return;
      } catch (_) {
        disconnectTranslateStream();
      }
    }

    showResult({ ok: false, status: 0, body: "Vision streaming port unavailable." }, anchor);
  }

  function cancelRegionSnip() {
    if (snipMask) {
      snipMask.remove();
      snipMask = null;
    }
  }

  function startRegionSnip() {
    if (window !== window.top) return;
    if (snipMask) {
      cancelRegionSnip();
      return;
    }
    hideAll();

    const mask = document.createElement("div");
    mask.id = SNIP_MASK_ID;

    const hint = document.createElement("div");
    hint.className = "bit-snip-hint";
    hint.textContent = "\u62d6\u62fd\u6846\u9009\u56fe\u7247 / \u6f2b\u753b / \u89c6\u9891\u5b57\u5e55\u533a\u57df\u76f4\u8bd1 \u00b7 Esc \u53d6\u6d88";

    const box = document.createElement("div");
    box.className = "bit-snip-box";

    mask.appendChild(box);
    mask.appendChild(hint);
    document.documentElement.appendChild(mask);
    snipMask = mask;

    let startX = 0;
    let startY = 0;
    let dragging = false;

    const cleanupListeners = () => {
      window.removeEventListener("keydown", onEsc, true);
    };

    const onEsc = (ev) => {
      if (ev.key === "Escape") {
        ev.preventDefault();
        ev.stopPropagation();
        cleanupListeners();
        cancelRegionSnip();
      }
    };
    window.addEventListener("keydown", onEsc, true);

    mask.addEventListener("contextmenu", (ev) => {
      ev.preventDefault();
      cleanupListeners();
      cancelRegionSnip();
    });

    mask.addEventListener("mousedown", (ev) => {
      if (ev.button !== 0) {
        cleanupListeners();
        cancelRegionSnip();
        return;
      }
      ev.preventDefault();
      ev.stopPropagation();
      dragging = true;
      startX = ev.clientX;
      startY = ev.clientY;
      mask.setAttribute("data-dragging", "1");
      box.style.display = "block";
      box.style.left = startX + "px";
      box.style.top = startY + "px";
      box.style.width = "0px";
      box.style.height = "0px";
    });

    mask.addEventListener("mousemove", (ev) => {
      if (!dragging) return;
      const x0 = Math.min(startX, ev.clientX);
      const y0 = Math.min(startY, ev.clientY);
      const w = Math.abs(ev.clientX - startX);
      const h = Math.abs(ev.clientY - startY);
      box.style.left = x0 + "px";
      box.style.top = y0 + "px";
      box.style.width = w + "px";
      box.style.height = h + "px";
    });

    mask.addEventListener("mouseup", (ev) => {
      if (!dragging) return;
      dragging = false;
      cleanupListeners();

      const left = Math.min(startX, ev.clientX);
      const top = Math.min(startY, ev.clientY);
      const width = Math.abs(ev.clientX - startX);
      const height = Math.abs(ev.clientY - startY);

      cancelRegionSnip();
      if (width < 14 || height < 14) return;

      const cssRect = { left: left, top: top, width: width, height: height };
      const anchor = {
        left: left,
        top: top,
        bottom: top + height
      };

      // Wait two animation frames so the snip overlay disappears completely from compositor before capture
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          try {
            chrome.runtime.sendMessage({ type: "BILI_CAPTURE_TAB" }, (res) => {
              void (chrome.runtime && chrome.runtime.lastError);
              if (res && res.ok && res.dataUrl) {
                cropDataUrlToRect(res.dataUrl, cssRect)
                  .then((croppedUrl) => triggerImageTranslate(croppedUrl, anchor))
                  .catch((err) =>
                    showResult({ ok: false, status: 0, body: String((err && err.message) || err) }, anchor)
                  );
                return;
              }
              const fallbackUrl = cropDomVisualFallback(cssRect);
              if (fallbackUrl) {
                triggerImageTranslate(fallbackUrl, anchor);
              } else {
                showResult(
                  { ok: false, status: 0, body: (res && res.body) || "Unable to capture tab region." },
                  anchor
                );
              }
            });
          } catch (err) {
            const fallbackUrl = cropDomVisualFallback(cssRect);
            if (fallbackUrl) {
              triggerImageTranslate(fallbackUrl, anchor);
            } else {
              showResult({ ok: false, status: 0, body: String((err && err.message) || err) }, anchor);
            }
          }
        });
      });
    });
  }

  // ==========================================================================================
  // Viewport-First Streaming Immersive Bilingual Webpage Engine (Alt+A)
  // ==========================================================================================

  const IMM_BLOCK_SELECTOR = "p, li, h1, h2, h3, h4, h5, h6, blockquote, figcaption, dd, td";
  const IMM_SKIP_ANCESTOR =
    "pre, code, script, style, noscript, svg, math, canvas, textarea, input, select, button, nav, footer, [contenteditable='true'], [translate='no'], .notranslate, #" +
    PANEL_ID +
    ", #" +
    STUDY_PANEL_ID +
    ", #" +
    IMM_BAR_ID +
    ", #" +
    SNIP_MASK_ID;
  const IMM_MAX_CONCURRENCY = 2;
  const IMM_MAX_BATCH_ITEMS = 4;
  const IMM_MAX_BATCH_CHARS = 960;

  let immActive = false;
  let immViewMode = "bilingual"; // "bilingual" | "target-only"
  let immModelOverride = ""; // "" uses options immersiveModel (default 2B); toggles 2B <-> 9B
  let immResolvedModel = "Index-Translate-2B";
  let immTargetLang = "中文";
  let immBar = null;
  let immStatusEl = null;
  let immModeBtn = null;
  let immModelBtn = null;

  let immIntersectionObs = null;
  let immMutationObs = null;
  let immMutationTimer = null;
  let immTotalCount = 0;
  let immDoneCount = 0;
  let immInFlight = 0;
  const immQueue = [];
  const immActivePorts = new Set();
  const immRecords = new Map(); // hostEl -> { source, span, state: "observed"|"queued"|"running"|"done" }

  function ensureImmBar() {
    if (immBar && document.documentElement.contains(immBar)) return immBar;

    immBar = document.createElement("div");
    immBar.id = IMM_BAR_ID;
    immBar.setAttribute("data-bit-imm", "1");

    const dot = document.createElement("span");
    dot.className = "bit-imm-dot";

    immStatusEl = document.createElement("span");
    immStatusEl.className = "bit-imm-status";
    immStatusEl.textContent = "\u53cc\u8bed\u5bf9\u7167";

    immModeBtn = document.createElement("button");
    immModeBtn.className = "bit-imm-btn";
    immModeBtn.type = "button";
    immModeBtn.title = "切换「双语对照」与「仅显译文」排版";
    immModeBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      immViewMode = immViewMode === "bilingual" ? "target-only" : "bilingual";
      syncImmModeUi();
    });

    immModelBtn = document.createElement("button");
    immModelBtn.className = "bit-imm-btn";
    immModelBtn.type = "button";
    immModelBtn.title = "切换网页双语流式模型（2B 极速 / 9B 精译）";
    immModelBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      const curIs9B = /9b/i.test(immModelOverride || immResolvedModel);
      immModelOverride = curIs9B ? "Index-Translate-2B" : "Index-Translate-9B";
      immResolvedModel = immModelOverride;
      syncImmBar();
      // Re-translate current immersive blocks cleanly with the newly chosen model
      if (immActive) {
        restartImmersiveWithNewModel();
      }
    });

    const closeBtn = document.createElement("button");
    closeBtn.className = "bit-imm-close";
    closeBtn.type = "button";
    closeBtn.title = "退出双语并无损还原原网页（快捷键 Alt+A）";
    closeBtn.textContent = "\u00d7";
    closeBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      stopImmersivePage();
    });

    ["mousedown", "mouseup", "click", "dblclick"].forEach((ev) => {
      immBar.addEventListener(ev, (e) => e.stopPropagation());
    });

    immBar.appendChild(dot);
    immBar.appendChild(immStatusEl);
    immBar.appendChild(immModeBtn);
    immBar.appendChild(immModelBtn);
    immBar.appendChild(closeBtn);
    document.documentElement.appendChild(immBar);
    syncImmModeUi();
    syncImmBar();
    return immBar;
  }

  function syncImmModeUi() {
    if (immViewMode === "target-only") {
      document.documentElement.setAttribute("data-bit-imm-mode", "target-only");
      if (immModeBtn) {
        immModeBtn.setAttribute("data-active", "");
        immModeBtn.textContent = "\u4ec5\u8bd1";
      }
    } else {
      document.documentElement.removeAttribute("data-bit-imm-mode");
      if (immModeBtn) {
        immModeBtn.removeAttribute("data-active");
        immModeBtn.textContent = "\u53cc\u8bed";
      }
    }
  }

  function syncImmBar() {
    if (!immBar) return;
    const busy = immInFlight > 0 || immQueue.length > 0;
    immBar.setAttribute("data-busy", busy ? "1" : "0");
    if (immStatusEl) {
      if (immTotalCount === 0) {
        immStatusEl.textContent = "\u672a\u68c0\u6d4b\u5230\u5916\u6587\u6bb5\u843d";
      } else if (busy) {
        immStatusEl.textContent = "\u6d41\u5f0f\u8bd1\u914d " + immDoneCount + "/" + immTotalCount;
      } else {
        immStatusEl.textContent = "\u53cc\u8bed\u5c31\u7eea " + immDoneCount + "/" + immTotalCount;
      }
    }
    if (immModelBtn) {
      const m = immModelOverride || immResolvedModel || "Index-Translate-2B";
      const shortLabel = m.replace(/^Index-Translate-/i, "").replace(/-A3B$/i, "");
      immModelBtn.textContent = shortLabel;
      if (/9b|35b/i.test(shortLabel)) immModelBtn.setAttribute("data-active", "");
      else immModelBtn.removeAttribute("data-active");
    }
  }

  // Extract clean text while wrapping inline <code>foo</code> in backticks so instTrans preserves identifiers
  function extractBlockSourceText(el) {
    let out = "";
    const walk = (node) => {
      if (!node) return;
      if (node.nodeType === 3) {
        out += node.nodeValue || "";
        return;
      }
      if (node.nodeType !== 1) return;
      if (node.hasAttribute("data-bit-imm")) return;
      const tag = node.tagName.toLowerCase();
      if (tag === "script" || tag === "style" || tag === "noscript" || tag === "svg" || tag === "math" || tag === "button") {
        return;
      }
      if (tag === "br") {
        out += "\n";
        return;
      }
      if (tag === "code" && !node.querySelector("pre")) {
        const codeTxt = (node.textContent || "").trim();
        if (codeTxt && !codeTxt.includes("`") && codeTxt.length <= 100) {
          out += " `" + codeTxt + "` ";
          return;
        }
      }
      for (let c = node.firstChild; c; c = c.nextSibling) {
        walk(c);
      }
    };
    walk(el);
    return out.replace(/[ \t\f\v]+/g, " ").replace(/\n{3,}/g, "\n\n").trim();
  }

  function isAlreadyTargetLanguage(text) {
    // Only skip Chinese-dominant paragraphs when target is Chinese AND there is zero Japanese Kana
    const t = String(immTargetLang || "中文");
    const tgtIsZh = t === "中文" || t === "简体中文" || /^zh/i.test(t) || /chinese/i.test(t);
    if (!tgtIsZh) return false;
    if (/[\u3040-\u30ff]/.test(text)) return false; // Japanese Kana present -> definitely translate!
    const letters = text.match(/[\p{L}\p{N}]/gu) || [];
    if (letters.length === 0) return true;
    const han = text.match(/[\u4e00-\u9fff]/g) || [];
    return han.length / letters.length > 0.65;
  }

  function isEligibleBlock(el) {
    if (!el || el.nodeType !== 1) return false;
    if (immRecords.has(el) || el.hasAttribute("data-bit-imm")) return false;
    if (el.closest(IMM_SKIP_ANCESTOR)) return false;

    // Skip container blocks that hold nested block candidates (only translate leaf blocks)
    const nested = el.querySelector(IMM_BLOCK_SELECTOR);
    if (nested) return false;

    const rects = el.getClientRects();
    if (!rects || rects.length === 0) return false;

    const text = extractBlockSourceText(el);
    if (text.length < 4 || text.length > MAX_LEN) return false;
    // Skip pure numbers, punctuation, or raw URLs
    if (!/\p{L}/u.test(text)) return false;
    if (/^https?:\/\/\S+$/i.test(text)) return false;
    if (isAlreadyTargetLanguage(text)) return false;

    return text;
  }

  function scanAndObserveBlocks(root) {
    if (!immActive || !immIntersectionObs) return;
    const scope = root && root.querySelectorAll ? root : document.body;
    if (!scope) return;

    const candidates = scope.matches && scope.matches(IMM_BLOCK_SELECTOR)
      ? [scope, ...scope.querySelectorAll(IMM_BLOCK_SELECTOR)]
      : Array.from(scope.querySelectorAll(IMM_BLOCK_SELECTOR));

    const vh = window.innerHeight || 800;
    const immediate = [];

    for (let i = 0; i < candidates.length; i++) {
      const el = candidates[i];
      const source = isEligibleBlock(el);
      if (!source) continue;

      const rec = { source: source, el: el, span: null, state: "observed" };
      immRecords.set(el, rec);
      immTotalCount++;

      const r = el.getBoundingClientRect();
      if (r.bottom >= -120 && r.top <= vh + 260) {
        immediate.push(rec);
      } else {
        immIntersectionObs.observe(el);
      }
    }

    // Enqueue immediately visible viewport blocks in reading order
    for (const rec of immediate) {
      enqueueImmRecord(rec);
    }
    syncImmBar();
    pumpImmQueue();
  }

  function ensureImmSpan(rec) {
    if (rec.span && rec.el.contains(rec.span)) return rec.span;
    const cs = window.getComputedStyle(rec.el);
    const span = document.createElement("span");
    span.className = "bit-imm-block";
    span.setAttribute("data-bit-imm", "1");
    span.setAttribute("data-loading", "1");
    span.style.setProperty("--bit-host-fs", cs.fontSize || "15px");
    span.style.setProperty("--bit-host-fw", cs.fontWeight || "400");
    span.style.setProperty("--bit-host-color", cs.color || "inherit");
    span.textContent = "\u00b7\u00b7\u00b7";
    rec.el.setAttribute("data-bit-imm-host", "1");
    rec.el.appendChild(span);
    rec.span = span;
    return span;
  }

  function enqueueImmRecord(rec) {
    if (!immActive || !rec || rec.state !== "observed") return;
    rec.state = "queued";
    if (immIntersectionObs) {
      try { immIntersectionObs.unobserve(rec.el); } catch (_) {}
    }
    immQueue.push(rec);
  }

  function pumpImmQueue() {
    if (!immActive) return;

    while (immInFlight < IMM_MAX_CONCURRENCY && immQueue.length > 0) {
      const batch = [];
      let charSum = 0;

      while (immQueue.length > 0 && batch.length < IMM_MAX_BATCH_ITEMS) {
        const next = immQueue[0];
        if (!document.documentElement.contains(next.el)) {
          immQueue.shift();
          immTotalCount = Math.max(0, immTotalCount - 1);
          continue;
        }
        if (batch.length > 0 && charSum + next.source.length > IMM_MAX_BATCH_CHARS) {
          break;
        }
        immQueue.shift();
        next.state = "running";
        ensureImmSpan(next);
        batch.push(next);
        charSum += next.source.length;
      }

      if (batch.length > 0) {
        runImmBatch(batch);
      }
    }
    syncImmBar();
  }

  function applyImmSegmentText(rec, text, isFinal) {
    if (!rec || !rec.el || !document.documentElement.contains(rec.el)) return;
    const span = ensureImmSpan(rec);
    span.removeAttribute("data-loading");
    if (isFinal) {
      span.removeAttribute("data-streaming");
      rec.el.setAttribute("data-bit-imm-ready", "1");
    } else {
      span.setAttribute("data-streaming", "1");
    }
    span.textContent = text;
  }

  function runImmBatch(batch) {
    immInFlight++;
    syncImmBar();

    const items = batch.map((r) => r.source);
    let finished = false;

    const finalizeBatch = (res) => {
      if (finished) return;
      finished = true;
      immInFlight = Math.max(0, immInFlight - 1);

      if (!immActive) return;
      const segs = (res && res.segments) || {};
      if (res && res.model) immResolvedModel = res.model;

      for (let i = 0; i < batch.length; i++) {
        const rec = batch[i];
        const txt = segs[i];
        if (res && res.ok && txt) {
          applyImmSegmentText(rec, txt, true);
          if (rec.state !== "done") {
            rec.state = "done";
            immDoneCount++;
          }
        } else if (rec.span) {
          rec.span.removeAttribute("data-loading");
          rec.span.removeAttribute("data-streaming");
          rec.span.textContent = res && res.status ? "[HTTP " + res.status + "]" : "[\u8bd1\u914d\u5931\u8d25]";
          rec.state = "done";
          immDoneCount++;
        }
      }
      syncImmBar();
      pumpImmQueue();
    };

    if (chrome && chrome.runtime && typeof chrome.runtime.connect === "function") {
      try {
        const port = chrome.runtime.connect({ name: "bili-immersive-stream" });
        immActivePorts.add(port);
        port.onMessage.addListener((msg) => {
          if (!immActive || !msg) return;
          if (msg.type === "BATCH_DELTA" && msg.segments) {
            if (msg.model) immResolvedModel = msg.model;
            for (let i = 0; i < batch.length; i++) {
              if (msg.segments[i]) applyImmSegmentText(batch[i], msg.segments[i], false);
            }
          } else if (msg.type === "BATCH_DONE") {
            immActivePorts.delete(port);
            try { port.disconnect(); } catch (_) {}
            finalizeBatch(msg);
          }
        });
        port.onDisconnect.addListener(() => {
          immActivePorts.delete(port);
          if (!finished && immActive) {
            finalizeBatch({ ok: false, status: 0 });
          }
        });
        port.postMessage({
          type: "START_IMMERSIVE_BATCH",
          items: items,
          modelOverride: immModelOverride
        });
        return;
      } catch (_) {}
    }

    finalizeBatch({ ok: false, status: 0 });
  }

  function clearImmersiveDomOnly() {
    for (const port of immActivePorts) {
      try { port.disconnect(); } catch (_) {}
    }
    immActivePorts.clear();
    if (immIntersectionObs) {
      try { immIntersectionObs.disconnect(); } catch (_) {}
      immIntersectionObs = null;
    }
    if (immMutationObs) {
      try { immMutationObs.disconnect(); } catch (_) {}
      immMutationObs = null;
    }
    clearTimeout(immMutationTimer);
    immQueue.length = 0;
    immInFlight = 0;
    immTotalCount = 0;
    immDoneCount = 0;
    immRecords.clear();

    document.querySelectorAll(".bit-imm-block[data-bit-imm]").forEach((el) => el.remove());
    document.querySelectorAll("[data-bit-imm-host]").forEach((el) => {
      el.removeAttribute("data-bit-imm-host");
      el.removeAttribute("data-bit-imm-ready");
    });
  }

  function startImmersiveObservers() {
    immIntersectionObs = new IntersectionObserver(
      (entries) => {
        if (!immActive) return;
        let added = false;
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          const rec = immRecords.get(entry.target);
          if (rec && rec.state === "observed") {
            enqueueImmRecord(rec);
            added = true;
          }
        }
        if (added) pumpImmQueue();
      },
      { rootMargin: "320px 0px 560px 0px", threshold: 0.01 }
    );

    scanAndObserveBlocks(document.body);

    immMutationObs = new MutationObserver((mutations) => {
      if (!immActive) return;
      let hasCandidate = false;
      for (const m of mutations) {
        for (let i = 0; i < m.addedNodes.length; i++) {
          const n = m.addedNodes[i];
          if (n.nodeType === 1 && !n.hasAttribute("data-bit-imm") && !n.closest("#" + IMM_BAR_ID)) {
            hasCandidate = true;
            break;
          }
        }
        if (hasCandidate) break;
      }
      if (!hasCandidate) return;
      clearTimeout(immMutationTimer);
      immMutationTimer = setTimeout(() => {
        if (immActive) scanAndObserveBlocks(document.body);
      }, 260);
    });

    if (document.body) {
      immMutationObs.observe(document.body, { childList: true, subtree: true });
    }
  }

  function startImmersivePage() {
    if (immActive) return;
    immActive = true;
    ensureImmBar();
    immBar.setAttribute("data-open", "");
    syncImmModeUi();

    const boot = () => {
      if (!immActive) return;
      startImmersiveObservers();
      syncImmBar();
    };

    if (chrome && chrome.runtime && typeof chrome.runtime.sendMessage === "function") {
      try {
        chrome.runtime.sendMessage({ type: "BILI_GET_IMMERSIVE_PREFS" }, (res) => {
          void (chrome.runtime && chrome.runtime.lastError);
          if (res && res.ok) {
            if (!immModelOverride && res.immersiveModel) immResolvedModel = res.immersiveModel;
            if (res.target) immTargetLang = res.target;
          }
          boot();
        });
        return;
      } catch (_) {}
    }
    boot();
  }

  function restartImmersiveWithNewModel() {
    clearImmersiveDomOnly();
    startImmersiveObservers();
    syncImmBar();
  }

  function stopImmersivePage() {
    immActive = false;
    clearImmersiveDomOnly();
    document.documentElement.removeAttribute("data-bit-imm-mode");
    if (immBar) {
      immBar.removeAttribute("data-open");
    }
  }

  function toggleImmersivePage() {
    if (immActive) stopImmersivePage();
    else startImmersivePage();
  }

  document.addEventListener("mouseup", (e) => {
    if (e.button !== 0) return;
    if (
      (panel && panel.contains(e.target)) ||
      (studyPanel && studyPanel.contains(e.target)) ||
      (immBar && immBar.contains(e.target))
    ) {
      return;
    }

    setTimeout(() => {
      const picked = readSelection(e);
      if (!picked || picked.text.length < MIN_LEN || picked.text.length > MAX_LEN) return;
      if (isOpen() && picked.text === currentSourceText) return;
      triggerTranslate(picked.text, picked.anchor, false);
    }, 10);
  });

  document.addEventListener("mousedown", (e) => {
    const inMain = panel && panel.contains(e.target);
    const inStudy = studyPanel && studyPanel.contains(e.target);
    if (!inMain && !inStudy && (isOpen() || isStudyOpen())) {
      hideAll();
    }
  });

  document.addEventListener(
    "contextmenu",
    (e) => {
      lastContextTarget = e.target;
      lastContextPoint = { x: e.clientX, y: e.clientY };
    },
    true
  );

  // ---- Keyboard Shortcuts ------------------------------------------------------------------
  // Alt+A = toggle Immersive Bilingual Webpage | Alt+S = Zero-OCR Region Snip Image Translate
  // S = toggle Study Card | Space / Shift+Space = speak word (1x / 0.7x)
  // E / Shift+E = speak example (1x / 0.7x) | F = flip self-test | D = compact/detailed
  // M = 9B/35B model toggle | C = copy card/translation | 1-4 or ←/→ = switch cards | Esc = close
  document.addEventListener("keydown", (e) => {
    if (e.altKey && !e.ctrlKey && !e.metaKey && (e.code === "KeyA" || e.key === "a" || e.key === "A")) {
      e.preventDefault();
      toggleImmersivePage();
      return;
    }

    if (e.altKey && !e.ctrlKey && !e.metaKey && (e.code === "KeyS" || e.key === "s" || e.key === "S")) {
      if (window === window.top) {
        e.preventDefault();
        startRegionSnip();
      }
      return;
    }

    if (!isOpen() && !isStudyOpen()) return;

    if (e.key === "Escape") {
      hideAll();
      return;
    }

    if (e.ctrlKey || e.metaKey || e.altKey || isEditableFocus()) return;

    const k = e.key;
    if (k === "s" || k === "S") {
      if (currentSourceText) {
        e.preventDefault();
        toggleStudyCard();
      }
      return;
    }

    if (k === "c" || k === "C") {
      e.preventDefault();
      if (isStudyOpen() && studyState && studyState.deck) {
        copyActiveStudyCard();
      } else if (currentTranslatedText) {
        copyToClipboard(currentTranslatedText, mainCopyBtn, "");
      }
      return;
    }

    if (!isStudyOpen() || !studyState || !studyState.deck) return;
    const cards = studyState.deck.cards || [];
    if (cards.length === 0) return;
    const idx = Math.max(0, Math.min(studyState.cardIndex || 0, cards.length - 1));
    const card = cards[idx];

    if (k === " " || e.code === "Space") {
      e.preventDefault();
      speakText(card.term, studyState.deck.sourceLang, e.shiftKey);
    } else if (k === "e" || k === "E") {
      if (card.example && card.example.source) {
        e.preventDefault();
        speakText(card.example.source, studyState.deck.sourceLang, e.shiftKey);
      }
    } else if (k === "f" || k === "F") {
      e.preventDefault();
      if (studyState.quizMode && !(studyState.revealedIndices && studyState.revealedIndices[idx])) {
        if (!studyState.revealedIndices) studyState.revealedIndices = {};
        studyState.revealedIndices[idx] = true;
        renderStudyDeck(true);
      } else {
        toggleQuizMode();
      }
    } else if (k === "d" || k === "D") {
      e.preventDefault();
      toggleCompactMode();
    } else if (k === "m" || k === "M") {
      e.preventDefault();
      toggleStudyModel();
    } else if (k === "ArrowLeft") {
      if (cards.length > 1) {
        e.preventDefault();
        stepCard(-1);
      }
    } else if (k === "ArrowRight") {
      if (cards.length > 1) {
        e.preventDefault();
        stepCard(1);
      }
    } else if (k >= "1" && k <= "4") {
      const targetIdx = Number(k) - 1;
      if (targetIdx < cards.length) {
        e.preventDefault();
        selectCardIndex(targetIdx);
      }
    }
  });

  window.addEventListener("pagehide", () => {
    cancelRegionSnip();
    hideAll();
    if (immActive) stopImmersivePage();
    try { chrome.runtime.sendMessage({ type: "BILI_PURGE_VOLATILE" }); } catch (_) {}
  });

  function resolveContextImageAnchor() {
    if (lastContextTarget && lastContextTarget.getBoundingClientRect) {
      const r = lastContextTarget.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) {
        return {
          left: Math.max(VIEWPORT_GAP, Math.round(r.left)),
          top: Math.max(VIEWPORT_GAP, Math.round(r.top)),
          bottom: Math.min(window.innerHeight - VIEWPORT_GAP, Math.round(r.bottom))
        };
      }
    }
    if (lastContextPoint) {
      return { left: lastContextPoint.x, top: lastContextPoint.y, bottom: lastContextPoint.y + 8 };
    }
    return {
      left: Math.max(VIEWPORT_GAP, Math.round((window.innerWidth - 360) / 2)),
      top: 80,
      bottom: 80
    };
  }

  if (chrome && chrome.runtime && chrome.runtime.onMessage) {
    chrome.runtime.onMessage.addListener((msg) => {
      if (!msg) return;
      if (msg.type === "BILI_TOGGLE_IMMERSIVE") {
        toggleImmersivePage();
        return;
      }
      if (msg.type === "BILI_START_SNIP") {
        startRegionSnip();
        return;
      }
      if (msg.type === "BILI_CONTEXT_IMAGE_DATA" && msg.dataUrl) {
        triggerImageTranslate(msg.dataUrl, resolveContextImageAnchor());
        return;
      }
      if (msg.type === "BILI_CONTEXT_IMAGE_ERROR") {
        showResult({ ok: false, status: 0, body: msg.body || "Failed to load image." }, resolveContextImageAnchor());
        return;
      }
      if (msg.type !== "BILI_CONTEXT_TRANSLATE" && msg.type !== "BILI_CONTEXT_STUDY") return;

      const picked = readSelection(null);
      const text = (picked && picked.text) || String(msg.text || "").trim();
      if (!text) return;

      const anchor = (picked && picked.anchor) || {
        left: Math.max(VIEWPORT_GAP, Math.round((window.innerWidth - 360) / 2)),
        top: 80,
        bottom: 80
      };
      triggerTranslate(text, anchor, msg.type === "BILI_CONTEXT_STUDY");
    });
  }
})();
