<div align="center">

<img src="https://capsule-render.vercel.app/api?type=waving&height=180&text=TyranoToolKit&fontSize=56&fontColor=ffffff&animation=fadeIn&color=gradient&customColorList=6,11,20" width="100%"/>

### 一键把暴龙编辑器（TyranoBuilder）视觉小说打包成 Android APK

不用手动装 Node.js、Cordova、Android SDK、JDK、Gradle——工具全部自动搞定。

<a href="https://github.com/ttq7/TyranoToolKit/releases/latest">
  <img src="https://readme-typing-svg.demolab.com?font=Noto+Sans+SC:wght@600&size=22&duration=3000&pause=1000&color=FF6EC7&center=true&vCenter=true&width=560&lines=%E2%AC%87%EF%B8%8F+%E7%82%B9%E5%87%BB%E4%B8%8A%E9%9D%A2%E4%B8%8B%E8%BD%BD%E6%9C%80%E6%96%B0%E7%89%88+exe;%F0%9F%8C%B8+%E5%8F%AF%E7%88%B1%E7%9A%84%E5%B0%8F%E4%BC%99%E4%BC%B4%E5%B7%B2%E5%87%86%E5%A4%87%E5%A5%BD%E5%95%A6;%F0%9F%93%A6+%E7%BB%BF%E8%89%B2%E5%8D%95%E6%96%87%E4%BB%B6+%C2%B7+%E5%8F%8C%E5%87%BB%E5%8D%B3%E7%94%A8;%F0%9F%94%A7+%E4%BE%9D%E8%B5%96%E5%85%A8%E8%87%AA%E5%8A%A8+%C2%B7+%E5%9B%BD%E5%86%85%E9%95%9C%E5%83%8F%E5%8A%A0%E9%80%9F" alt="typing"/>
</a>

[![Release](https://img.shields.io/github/v/release/ttq7/TyranoToolKit?style=flat-square&color=ff69b4)](https://github.com/ttq7/TyranoToolKit/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9%20WPF-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F%2011-lightgrey?style=flat-square)]()
[![Stars](https://img.shields.io/github/stars/ttq7/TyranoToolKit?style=flat-square&color=yellow)](https://github.com/ttq7/TyranoToolKit/stargazers)

<br/>

[![下载](https://img.shields.io/badge/⬇️_下载最新版-ff69b4?style=for-the-badge)](https://github.com/ttq7/TyranoToolKit/releases/latest)
[![问题反馈](https://img.shields.io/badge/🐛_问题反馈-blue?style=for-the-badge)](https://github.com/ttq7/TyranoToolKit/issues)
[![讨论](https://img.shields.io/badge/💬_功能建议-2ea44f?style=for-the-badge)](https://github.com/ttq7/TyranoToolKit/issues)

<img src="https://count.getloli.com/get/@ttq7-TyranoToolKit?theme=moebooru" alt="访客计数" width="320"/>

</div>

---

## 📖 简介

**TyranoToolKit（暴龙工具箱）** 是一款面向 [暴龙编辑器 / TyranoBuilder](https://tyrano.jp/) 视觉小说作者的 Windows 桌面工具。

用 TyranoBuilder 做好的游戏想在手机上玩？官方流程需要自己装 Node.js、Cordova、Android SDK、JDK、Gradle 五件套，配置环境变量、接受许可协议、对版本号——随便一步出错就卡住。本工具把这些全部自动化。

| | |
|:---:|:---|
| 🇨🇳 **国内镜像优先** | 清华、腾讯云、npmmirror 多源自动切换 + 失败重试，国内网络无需科学上网 |
| 🤖 **全自动无人值守** | 连最劝退的 Android SDK 许可协议都自动搞定（预写许可证文件，跳过交互确认） |
| ✅ **所见即所装** | 每个组件安装后都做真实验证（文件存在性 + 完整性校验），绝不"假成功" |
| 📦 **绿色零残留** | 单文件运行，不写注册表；依赖全部装在工具自己的数据目录，删目录即彻底卸载 |
| 🌙 **深色主题 UI** | 手写 WPF 深色主题，夜间打包不刺眼 |

## ✨ 功能总览

| 功能页 | 说明 |
|:---:|:---|
| 📱 **APK 打包** | 扫描 TyranoBuilder 项目 → 配置应用信息 → 一键构建 APK |
| ⚡ **Electron 下载** | Electron 运行时版本管理，供暴龙引擎 Electron 桌面版使用 |
| 🔍 **依赖检测** | 集成在 APK 打包页内，逐项检查 + 一键安装/升级 |

### 📱 APK 打包

- **项目扫描** — 自动定位 TyranoBuilder 安装目录（默认 Steam 版），识别游戏项目
- **应用配置** — 自定义应用名称、应用 ID（包名）、应用图标（可选，PNG 1024×1024）
- **构建模式** — `debug`（测试用）/ `release`（发布用）
- **实时日志** — 构建过程全量输出，支持随时取消、一键打开输出目录

### 🔍 依赖检测与自动安装

<details open>
<summary><b>自动部署的组件一览</b></summary>

| 组件 | 版本 | 下载源 |
|:---|:---|:---|
| Node.js | v22 LTS | npmmirror |
| Cordova CLI | 13.x | npm（npmmirror registry） |
| Android SDK | platform-tools / android-36 / build-tools 36.0.0 | 清华镜像 → Google 官方 |
| JDK | Temurin 17 LTS | 清华镜像 → Adoptium 官方 |
| Gradle | 8.11.1 | 清华镜像 → 腾讯云 → 官方 |
| Electron | 任意版本可选 | npmmirror → GitHub 官方 |

</details>

- 逐项显示安装状态与版本号，版本过低会明确提示升级（如 Gradle → 8.11.1）
- 每个组件可单独安装，也可按需补装缺失项——**已装好的绝不重复下载**
- 下载完整性校验（Content-Length 比对），坏包自动删除重下
- 已下载的 Gradle 发行包保留在本地，构建时通过 `file:///` 直接引用，**零流量复用**

### ⚡ Electron 下载管理

为暴龙引擎（TyranoEngine）的 Electron 桌面版提供运行时版本管理：

- **版本列表** — 从 npm registry 拉取全部 Electron 版本，标注 latest 与稳定版
- **双源切换** — 国内镜像（npmmirror，推荐）与 GitHub 官方源一键切换
- **断点续传** — 下载中断后从断点继续，支持随时取消
- **多版本缓存** — 按 `@electron/get` 标准结构缓存（SHA256 目录 + SHASUMS256.txt 校验），可与暴龙引擎共享缓存
- **一键切换** — 下载后直接"设为暴龙引擎使用版本"

## 🚀 快速开始

```text
① 下载 exe ──► ② 双击运行 ──► ③ 依赖检测/安装 ──► ④ 扫描项目 ──► ⑤ 配置应用 ──► ⑥ 构建 APK
```

1. 从 [Releases](https://github.com/ttq7/TyranoToolKit/releases/latest) 下载 `TyranoToolKit.exe`
2. 双击运行（绿色单文件，无需安装）
3. 首次使用先做**依赖检测**：缺什么点什么（首次约 10 分钟，取决于网速）
4. 选择你的 TyranoBuilder 游戏项目目录，点击 `扫描项目`
5. 填写应用名称 / ID（可选上传图标），选择 `debug` 或 `release`
6. 点击 `构建`，完成后点击 `打开输出目录` 拿 APK

> 💡 建议把本工具目录加入杀毒软件白名单，避免运行时依赖被误删。
>
> 💡 已装过 Android 开发环境？工具会自动识别并复用，不会重复下载。

## 🛠️ 从源码构建

需要 .NET 9 SDK（Windows x64）：

```bash
git clone https://github.com/ttq7/TyranoToolKit.git
cd TyranoToolKit
dotnet publish TyranoToolKit.csproj -c Release -o publish
```

产物为 `publish/TyranoToolKit.exe` 单文件程序。

<details>
<summary><b>🔧 技术栈</b></summary>

- **UI**：WPF (.NET 9) + 手写深色主题
- **构建链**：Cordova CLI（cordova-android 15.x / AGP 8.10.1）
- **下载引擎**：HttpClient 多源切换 + 断点续传 + SHA256 校验
- **SDK 部署**：cmdline-tools + 预写许可证文件（CI 标准做法）

</details>

## ❓ 常见问题

<details open>
<summary><b>导出时报 Gradle 版本不匹配？</b></summary>

在依赖检测区重新检测，工具会提示把 Gradle 升级到 8.11.1，点击 `安装 Gradle` 即可自动升级，旧版本会自动清理。

</details>

<details open>
<summary><b>下载失败 / 卡住？</b></summary>

工具内置多个镜像源会自动切换重试；如果全部失败，检查网络或代理设置后再试，已下载的组件不会重复下载。

</details>

<details open>
<summary><b>在虚拟机里运行要注意什么？</b></summary>

虚拟机需能正常联网。依赖检测会如实报告缺失组件（如 build-tools），按提示补装即可。

</details>

<details open>
<summary><b>想彻底卸载？</b></summary>

删除 exe 和工具数据目录（`%LocalAppData%\TyranoToolKit`）即可——所有下载的依赖都在里面，不残留注册表。

</details>

## 🤝 贡献

欢迎提 [Issue](https://github.com/ttq7/TyranoToolKit/issues) 和 Pull Request！遇到 bug 请附上构建日志截图。

## 📄 许可证

[MIT](LICENSE) © 2026 ttq7

---

<div align="center">

<img src="https://capsule-render.vercel.app/api?type=rect&height=6&color=gradient&customColorList=6,11,20" width="100%"/>

如果这个工具帮到了你，欢迎点一个 ⭐ Star～

<img src="https://count.getloli.com/get/@ttq7-TyranoToolKit-footer?theme=moebooru" alt="visit" width="240"/>

*Made with 🌸 by ttq7*

</div>
