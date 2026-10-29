# Bili Index Translate · 极简流式划词 + Zero-OCR 多模态识图框选 + Alt+A 双语对照 + instTrans 术语表 + 伴随语言学习卡片

基于 Bilibili OpenAI 兼容多模态与文本翻译接口（`Index-Translate-35B-A3B` / `9B` / `2B`）构建的**双平台极简翻译与按需语言学习私教**：
- **网页端（Chrome / Edge MV3）**：无需本地 Python 代理、无需第三方 OCR 或中转，通过 MV3 `declarativeNetRequest` 原生直连 B 站接口；同时支持 **划词流式翻译**、**`Alt+S` 零 OCR 框选图片/漫画/视频字幕帧直译 + 生词卡闭环**、**图片右键一键直译**、**`Alt+A` 视口流式双语对照全页**、**官方 `instTrans` 动态术语表/免译锁定** 与 **按 `S` 并排展开零留痕词典级学习卡片**。
- **Windows 桌面端（.NET 8 WPF + UIA + Per-Monitor DPI 识图框选）**：跨任意桌面应用的非抢焦点全局划词气泡 + **`Alt+Shift+S` 全屏跨显示器框选识图直译（支持游戏 HUD、扫描版 PDF、视频硬字幕与剪贴板截图）** + `instTrans` 自定义术语表 + 伴随语言学习卡片。

> **开箱即用下载**：可直接前往右侧 [**Releases**](https://github.com/sundtfbekasmjuvh/bili-index-translate-study/releases) 下载打包好的 Chrome/Edge 扩展 ZIP 与 Windows 桌面版可执行程序。

---

## 目录结构与双端概览

| 目录 | 说明 | 状态 |
|---|---|---|
| [`bili-index-translate/`](bili-index-translate/) | **Chrome / Edge / Brave / Arc 浏览器扩展（Manifest V3）**：MV3 原生直连 + `Alt+S` 零 OCR 识图框选/右键图片直译 + `Alt+A` 视口流式双语对照 + `instTrans` 动态术语表 + 划词与伴随语言学习卡片 | 可直接加载未打包扩展或下载 Release ZIP |
| [`BiliIndexTranslate.Desktop/`](BiliIndexTranslate.Desktop/) | **Windows 桌面全局划词与屏幕识图端（C# / .NET 8, WPF + UI Automation）**：跨应用非抢焦点浮窗翻译 + `Alt+Shift+S` 跨屏冻结框选识图直译/剪贴板截图直译 + `instTrans` 术语表 + 伴随学习卡片 | `0 warnings` 编译通过，`122/122` xUnit 核心单测通过（见 [`BiliIndexTranslate.Desktop/README.md`](BiliIndexTranslate.Desktop/README.md)） |
| [`bili-study-sandbox/`](bili-study-sandbox/) | **零依赖本地浏览器预览沙盒（Node.js 标准库）**：无需重载扩展即可在本地 `http://localhost:3001` 实时体验划词、学习卡片、`Alt+S` 画布框选识图、术语表与 `Alt+A` 双语对照 | 运行 `node bili-study-sandbox/server.js` 即可启动 |

---

## 核心特性（v1.4.0 多模态全能旗舰版）

### 1. Zero-OCR 多模态识图框选直译 + 学习卡片全闭环（Chrome `Alt+S` / Windows `Alt+Shift+S`）
- **突破不可选中像素边界**：无需外接任何第三方 OCR 引擎，直接调用 `Index-Translate-35B-A3B` 旗舰 MoE 视觉多模态能力（实测比 2B/9B 视觉快 `3.2x`，首字流式响应 `~0.9–1.2s`），一气呵成读懂**生肉漫画气泡、论文扫描图、架构示意图、游戏外语 UI 以及 YouTube/B 站暂停视频硬字幕帧**。
- **`〖译文〗` → `〖原文〗` 双轨流式提取，无缝打通词典级学习卡片**：译文优先逐字流式上屏，紧接着自动回填图片中的原始外语文本——原本无法选中的图片像素即刻点亮 **发音朗读** 与 **`S` 键伴随语言学习卡片**（音标/假名、词性、辨析、助记与双语例句一键拆解），并同步遵守用户自定义**术语表**！
- **100% 纯内存裁剪，零临时文件落盘**：网页端基于 `<canvas>` 内存裁剪、Windows 端基于 `MemoryStream` 物理像素冻结截图裁剪（同时支持托盘菜单一键翻译 `Win+Shift+S` 剪贴板截图），绝不在磁盘写入任何截图缓存。

### 2. 免本地代理 `Alt+A` 视口流式双语对照（Chrome 扩展）
- **免 Python 网桥原生直连**：在浏览器网络层自动剥离扩展 `Origin` 头并注入 `enable_thinking: false`，装好扩展即刻直连，无本地回环代理冲突。
- **`IntersectionObserver` 视口优先 + `〖P0〗` 锚点合并 SSE 流式上屏**：按下 `Alt+A` 仅优先翻译当前屏幕可见及预读区段落，滚动加载自动续翻；右下角微透胶囊一键切换 **`双语` ↔ `仅译`**、**`2B` ↔ `9B`**，再次按 `Alt+A` 100% 无损还原原网页。

### 3. 官方 `instTrans` 动态命中术语表 & 伴随语言学习卡片（双端通用）
- **强化学习原生槽位对齐**：支持每行一条 `Source => Target` 指定译名，或单写一行 `Flash-Attention` 表示**免译原样锁定**；文本划词按需零冗余注入，多模态识图同步注入 `【术语对照】`。
- **双速语音跟读 + 自测遮罩 + 严格零留痕**：词头与例句支持 `1x` / `0.7x` 慢速跟读，支持 `M` 键一键切换 `9B` ↔ `35B-A3B` 旗舰精析，纯内存 LRU 秒开（`⚡瞬时`），零历史落盘。

---

## 快捷键一览

| 快捷键 | 平台 | 功能 |
|---|---|---|
| `Alt + S` | Chrome / Edge | **零 OCR 网页/视频帧框选识图直译**（亦可直接右键点击任意图片翻译） |
| `Alt + Shift + S` | Windows 桌面端 | **零 OCR 跨显示器屏幕冻结框选识图直译**（托盘亦支持剪贴板截图直译） |
| `Alt + A` | Chrome / Edge | 开启 / 退出全页视口流式「双语对照阅读」（100% 无损还原） |
| `Alt + Q` | Windows 桌面端 | 按需兜底划词翻译快捷键（UIA 不可读控件自动单次复制翻译） |
| `S` | Chrome 端浮窗内 | 展开 / 收起伴随「语言学习卡片」（划词或识图完成后均可一键唤起） |
| `Space` / `Shift + Space` | 学习卡片内 | 常速（`1x`）/ 慢速（`0.7x`）朗读当前词头 |
| `E` / `Shift + E` | 学习卡片内 | 常速（`1x`）/ 慢速（`0.7x`）朗读双语例句原文 |
| `D` / `F` / `M` / `C` | 学习卡片内 | 切换 `详尽/精简` / `自测翻转` / `9B↔35B` 模型 / 复制格式化词卡笔记 |
| `1` – `4` 或 `←` / `→` | 学习卡片内 | 在长句或识图提取的多张重点词卡之间快速切换 |
| `Ctrl + 鼠标滚轮` / `Esc` | 双端通用 | 动态缩放浮窗字号 / 关闭浮窗并停止语音朗读 |

---

## 快速安装指南

1. **Chrome / Edge 浏览器扩展**：下载 Release 中的 `bili-index-translate-chrome-v1.4.0.zip` 解压 → 打开 `chrome://extensions` 开启右上角**开发者模式** → **加载已解压的扩展程序** 选择 `bili-index-translate/` 目录。
2. **Windows 桌面端（.NET 8 WPF）**：下载 Release 中的 `BiliIndexTranslate-Desktop-win-x64-selfcontained-v1.4.0.zip`（自带运行时单文件免安装版）或 `BiliIndexTranslate-Desktop-win-x64-v1.4.0.zip`（轻量版），解压运行 `BiliIndexTranslate.exe`。
3. **本地预览沙盒**：运行 `node bili-study-sandbox/server.js` 并访问 `http://localhost:3001`。
