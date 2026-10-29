# Bili Index Translate · 极简流式划词 + Alt+A 双语对照 + instTrans 术语表 + 伴随语言学习卡片

基于 Bilibili OpenAI 兼容翻译接口（`Index-Translate-9B` / `35B-A3B` / `2B`）构建的**双平台极简翻译与按需语言学习私教**：
- **网页端（Chrome / Edge MV3）**：无需本地 Python 代理、无需第三方中转，通过 MV3 `declarativeNetRequest` 原生直连 B 站接口；同时支持 **划词流式翻译**、**`Alt+A` 视口懒加载流式双语对照全页**、**官方 `instTrans` 动态术语表/免译锁定** 与 **按 `S` 并排展开零留痕词典级学习卡片**。
- **Windows 桌面端（.NET 8 WPF + UIA）**：跨任意桌面应用的非抢焦点全局划词气泡 + `instTrans` 自定义术语表/免译锁定 + 伴随语言学习卡片。

> **开箱即用下载**：可直接前往右侧 [**Releases**](https://github.com/sundtfbekasmjuvh/bili-index-translate-study/releases) 下载打包好的 Chrome/Edge 扩展 ZIP 与 Windows 桌面版可执行程序。

---

## 目录结构与双端概览

| 目录 | 说明 | 状态 |
|---|---|---|
| [`bili-index-translate/`](bili-index-translate/) | **Chrome / Edge / Brave / Arc 浏览器扩展（Manifest V3）**：MV3 原生直连 + `Alt+A` 视口流式双语对照 + `instTrans` 动态术语表 + 划词与伴随语言学习卡片 | 可直接加载未打包扩展或下载 Release ZIP |
| [`BiliIndexTranslate.Desktop/`](BiliIndexTranslate.Desktop/) | **Windows 桌面全局划词端（C# / .NET 8, WPF + UI Automation）**：跨应用非抢焦点浮窗翻译 + `instTrans` 自定义术语表 + 伴随学习卡片 | `0 warnings` 编译通过，`118/118` xUnit 核心单测通过（见 [`BiliIndexTranslate.Desktop/README.md`](BiliIndexTranslate.Desktop/README.md)） |
| [`bili-study-sandbox/`](bili-study-sandbox/) | **零依赖本地浏览器预览沙盒（Node.js 标准库）**：无需重载扩展即可在本地 `http://localhost:3001` 实时体验划词、学习卡片、术语表与 `Alt+A` 双语对照 | 运行 `node bili-study-sandbox/server.js` 即可启动 |

---

## 核心特性（v1.3.0 双剑合璧版）

### 1. 免本地代理 `Alt+A` 视口流式双语对照（Chrome 扩展）
- **免 Python 网桥原生直连**：不同于需本地运行 `python call_api.py --serve` 的代理方案，本扩展在浏览器网络层自动剥离扩展 `Origin` 头并注入 `enable_thinking: false`，装好扩展即刻直连，无本地回环代理冲突。
- **`IntersectionObserver` 视口优先 + `〖P0〗` 锚点合并 SSE 流式上屏**：按下 `Alt+A`（或页面右键菜单）仅优先翻译当前屏幕可见及预读区段落，滚动或瀑布流加载时自动续翻；相邻短段合并为单条 SSE 流逐字实时流入对应段落下方（`2B` 模型 3 段并发流式实测仅 `~680 ms`）。
- **零侵入 DOM 与悬浮胶囊控制栏**：不移动、不包裹原网页子节点（完美兼容 React / Vue 动态水合页面）；右下角微透胶囊一键切换 **`双语` ↔ `仅译`**、**`2B` ↔ `9B`**，再次按 `Alt+A` 或点击 `×` 100% 无损还原原网页。
- **与划词「学习卡片」无缝协同**：在双语对照阅读时，三击或划选任意外文原句会自动剔除译文节点，按下 `S` 即可在旁弹出词典级学习卡片精读发音。

### 2. 官方 `instTrans` 动态命中术语表 & 免译锁定（Chrome + Windows 双端通用）
- **强化学习原生槽位对齐**：深度对接 `Index-Translate` 官方 `instTrans` 结构化约束模板（`【源文】` + `【约束要求】 1. 【硬性要求】专名/术语对照`）。
- **按需零冗余注入（Dynamic Hit-Only Injection）**：支持每行一条 `Source => Target` 指定译名，或单写一行 `Flash-Attention` 表示**免译原样锁定**；每次请求仅动态筛出当前文本实际命中的术语注入提示词，未命中时保持零额外 Token 开销，并自动保护内联 `` `code` `` 代码标识符不被误翻。

### 3. 词典级伴随语言学习卡片 & 严格零留痕隐私
- **并排双浮窗磨砂美学**：高透毛玻璃层级、深浅色自适应、极细分割线微标（`辨析` · `助记` · `搭配`）、`详尽/精简 (D)` 折叠与 `Ctrl+滚轮` 字号缩放。
- **智能模型分级路由**：主译文设为 `2B` 极速模型时，学习卡片自动升级至 `9B`，并支持 `M` 键一键切换 **`9B` 均衡模式 ↔ `35B`（`Index-Translate-35B-A3B` 官方旗舰 MoE）**。
- **双速语音跟读 + 自测遮罩 + 严格零留痕**：词头与例句支持 `1x` / `0.7x` 慢速跟读，点击搭配胶囊即读外语短语；会话纯内存 LRU 秒开（`⚡瞬时`），不写任何翻译历史或生词落盘记录。

---

## 网页端快捷键一览

| 快捷键 | 功能 |
|---|---|
| `Alt + A` | 开启 / 退出全页视口流式「双语对照阅读」（100% 无损还原） |
| `S` | 展开 / 收起伴随「语言学习卡片」（划词气泡打开且焦点不在输入框时） |
| `Space` / `Shift + Space` | 常速（`1x`）/ 慢速（`0.7x`）朗读当前词头 |
| `E` / `Shift + E` | 常速（`1x`）/ 慢速（`0.7x`）朗读双语例句原文 |
| `D` | 切换学习卡片 `详尽` / `精简` 视图密度 |
| `F` | 开启 / 揭晓 / 退出 `自测` 主动回忆遮罩 |
| `M` | 切换学习卡片 `9B` 均衡模式与 `35B`（`35B-A3B` 旗舰精析） |
| `C` | 复制当前词卡格式化笔记（词卡未开时复制主译文） |
| `1` – `4` 或 `←` / `→` | 在长句提取的多张重点词卡之间快速切换 |
| `Ctrl + 鼠标滚轮` | 动态缩放浮窗字号（`0.85x – 1.35x`） |
| `Esc` | 关闭划词与学习卡片浮窗并停止语音朗读 |

---

## 快速安装指南

1. **Chrome / Edge 浏览器扩展**：下载 Release 中的 `bili-index-translate-chrome-v1.3.0.zip` 解压 → 打开 `chrome://extensions` 开启右上角**开发者模式** → **加载已解压的扩展程序** 选择 `bili-index-translate/` 目录。
2. **Windows 桌面端（.NET 8 WPF）**：下载 Release 中的 `BiliIndexTranslate-Desktop-win-x64-selfcontained-v1.3.0.zip`（自带运行时单文件免安装版）或 `BiliIndexTranslate-Desktop-win-x64-v1.3.0.zip`（轻量版），解压运行 `BiliIndexTranslate.exe`。
3. **本地预览沙盒**：运行 `node bili-study-sandbox/server.js` 并访问 `http://localhost:3001`。
