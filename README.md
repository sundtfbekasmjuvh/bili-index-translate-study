# Bili Index Translate · 极简流式划词翻译 + 伴随语言学习卡片

基于 Bilibili OpenAI 兼容翻译接口（默认 `Index-Translate-9B` / `35B-A3B` / `2B`）构建的**双平台极简划词翻译与按需语言学习私教**：
平时划词仅弹出轻量译文气泡；遇到值得精学的生词、日语汉字词或复杂长难句时，点击 **`学习卡片`**（或网页端按 **`S`**）即可在译文旁并排展开**零留痕词典级学习卡片**。

> **开箱即用下载**：可直接前往右侧 [**Releases**](https://github.com/sundtfbekasmjuvh/bili-index-translate-study/releases) 下载打包好的 Chrome/Edge 扩展 ZIP 与 Windows 桌面版可执行程序。

---

## 目录结构与双端概览

| 目录 | 说明 | 状态 |
|---|---|---|
| [`bili-index-translate/`](bili-index-translate/) | **Chrome / Edge / Brave / Arc 浏览器扩展（Manifest V3）**：MV3 `declarativeNetRequest` 原生直连 B 站接口（无需第三方中转）+ 双通道 SSE 流式翻译 + 伴随语言学习卡片 | 可直接加载未打包扩展或下载 Release ZIP |
| [`BiliIndexTranslate.Desktop/`](BiliIndexTranslate.Desktop/) | **Windows 桌面全局划词端（C# / .NET 8, WPF + UI Automation）**：跨任意支持 UIA 文本的应用非抢焦点浮窗翻译 + 伴随学习卡片 | `0 warnings` 编译通过，`117/117` xUnit 核心单测通过（见 [`BiliIndexTranslate.Desktop/README.md`](BiliIndexTranslate.Desktop/README.md)） |
| [`bili-study-sandbox/`](bili-study-sandbox/) | **零依赖本地浏览器预览沙盒（Node.js 标准库）**：无需重载浏览器扩展即可在本地 `http://localhost:3001` 实时调试双浮窗样式与提示词 | 运行 `node bili-study-sandbox/server.js` 即可启动 |

---

## 核心特性（v1.2.1 官方协议对齐与直连增强版）

### 1. 词典级极简美学与无干扰交互
- **并排双浮窗自适应避让**：伴随学习卡片优先停靠在译文气泡右侧，靠近屏幕边缘时自动切换至左侧/下方/上方，支持标题栏独立拖拽。
- **磨砂微透与深浅色自适应**：消除杂乱嵌套色块，采用高透毛玻璃层级、无框高对比核心释义与极细分割线行内微标（`辨析` · `助记` · `搭配`）；自动跟随系统 `prefers-color-scheme: dark`。
- **`详尽` ↔ `精简`（快捷键 `D`）一键折叠 & `Ctrl+滚轮` 字号缩放**：`精简`模式一键收起辨析、助记与搭配（高度压缩约 40%），`Ctrl + 鼠标滚轮`支持 `0.85x – 1.35x` 动态字号调节。

### 2. 原生直连 B 站接口、官方参数对齐与零留痕隐私
- **MV3 `declarativeNetRequest` 原生直连（彻底告别 `HTTP 412` 与第三方中转泄露）**：自动剥离浏览器扩展 `Origin: chrome-extension://...` 请求头，直接以 `HTTP 200` 连接 `index-translate.bilibili.com`，划词文本不再经过任何第三方代理服务器。
- **对齐官方 `bilibili/Index-Translate` 协议与智能模型分级**：自动注入 `chat_template_kwargs: { enable_thinking: false }` 与 `<think>` 过滤（消除 Qwen3.5 思维链空输出隐患）；主翻译采用官方 `temperature: 0.0` 指令；当主译文设为 `2B` 极速模型时，学习卡片自动升级至 `9B`，并支持在词卡顶栏一键切换 **`9B` 均衡模式 ↔ `35B`（`Index-Translate-35B-A3B` 官方旗舰 MoE）**。
- **双通道 SSE 流式渐进渲染 + 会话纯内存瞬时 LRU**：主译文首字约 `~300 ms` 响应；长难句学习卡片约 `~1.3 s` 先行呈现句法结构，第 1 张重点词卡约 `~3 s` 即刻可读；同一会话内按 `S` 重开已查词卡实现 **`~0 ms` 内存瞬时秒开（`⚡瞬时`）**，不触发多余悬停请求，不落盘任何历史记录。

### 3. 多语种深度拆解与跟读记忆
- **多语种自动识别 + 纯正目标语解释强约束**：自动匹配原外语音标/读音（英语 IPA、日语平假名+罗马音、韩语、法语、德语、俄语等），同时严格约束释义、语域标签（如 `文学书面` / `日常高频`）、近义语感辨析、构词助记与例句译文 100% 使用纯正简体中文（或所选目标语），即使输入日语汉字词（如 `木漏れ日`、`一期一会`）也绝不混入日文释义或繁体字。
- **双速语音跟读 + 点击搭配即读**：词头与例句均提供 **`发音 (1x)`** 与 **`0.7x` 慢速清晰跟读**；点击任意「搭配」胶囊即可自动剥离中文括注并朗读外语原短语。
- **双语例句目标词高亮 + `自测`翻转 + 一键复制笔记**：自动高亮例句中的目标词或其屈折变形；支持主动回忆遮罩自测与一键复制干净的词卡笔记。

---

## 网页端快捷键一览（焦点不在输入框时生效）

| 快捷键 | 功能 |
|---|---|
| `S` | 展开 / 收起伴随「语言学习卡片」 |
| `Space` / `Shift + Space` | 常速（`1x`）/ 慢速（`0.7x`）朗读当前词头 |
| `E` / `Shift + E` | 常速（`1x`）/ 慢速（`0.7x`）朗读双语例句原文 |
| `D` | 切换 `详尽` / `精简` 视图密度 |
| `F` | 开启 / 揭晓 / 退出 `自测` 主动回忆遮罩 |
| `M` | 一键切换 `9B` 均衡模式与 `35B`（`35B-A3B` 旗舰精析） |
| `C` | 复制当前词卡格式化笔记（词卡未开时复制主译文） |
| `1` – `4` 或 `←` / `→` | 在长句提取的多张重点词卡之间快速切换 |
| `Ctrl + 鼠标滚轮` | 动态缩放浮窗字号（`0.85x – 1.35x`） |
| `Esc` | 关闭全部浮窗并停止语音朗读 |

---

## 快速安装与二次开发指南

### 方式一：安装 Chrome / Edge 浏览器扩展
1. 下载源码或 Release 中的 `bili-index-translate-chrome-v1.2.1.zip` 并解压。
2. 打开浏览器访问 `chrome://extensions`（Edge 访问 `edge://extensions`），开启右上角**开发者模式（Developer mode）**。
3. 点击**加载已解压的扩展程序（Load unpacked）**，选择 `bili-index-translate/` 目录即可。
4. 点击扩展图标可自定义接口 Base URL、默认模型（`2B` / `9B` / `35B-A3B` / `Nailong-9B`）与目标解释语言。

### 方式二：运行或编译 Windows 桌面端（.NET 8 WPF）
- **直接运行预编译版**：下载 Release 中的 `BiliIndexTranslate-Desktop-win-x64-selfcontained-v1.2.1.zip`（自带运行时单文件免安装版）或 `BiliIndexTranslate-Desktop-win-x64-v1.2.1.zip`（需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)），解压后运行 `BiliIndexTranslate.exe`。
- **从源码构建与测试**（需 .NET 8 SDK）：
  ```bat
  cd BiliIndexTranslate.Desktop
  tools\test.cmd
  tools\build.cmd
  ```

### 方式三：本地热调 UI 与提示词沙盒（推荐二次修改时使用）
无需频繁刷新浏览器扩展，直接用 Node.js（18+，零第三方依赖）启动交互预览页：
```bash
node bili-study-sandbox/server.js
# 浏览器打开 http://localhost:3001 即可实时修改 bili-index-translate/ 下的代码并刷新预览
```
