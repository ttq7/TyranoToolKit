<div align="center">

# 🐉 TyranoToolKit

**一键把暴龙编辑器（TyranoBuilder）HTML5 视觉小说打包成 Android APK**

不用手动装 Node.js、Cordova、Android SDK、JDK、Gradle——工具全部自动搞定。

[![Release](https://img.shields.io/github/v/release/ttq7/TyranoToolKit?style=flat-square)](https://github.com/ttq7/TyranoToolKit/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9%20WPF-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F%2011-lightgrey?style=flat-square)]()

**[⬇️ 下载最新版](https://github.com/ttq7/TyranoToolKit/releases/latest)** · [从源码构建](#-从源码构建) · [常见问题](#-常见问题)

</div>

---

## ✨ 特性

- **🚀 一键环境部署** — Node.js、Cordova CLI、Android SDK（cmdline-tools / platform-tools / platforms / build-tools）、JDK 17、Gradle 全自动下载安装，全程国内镜像（清华 / 腾讯云），失败自动切换源并重试
- **🔍 可视化依赖检测** — 逐项检查依赖的安装与版本状态，版本过低（如 Gradle）会明确提示升级，缺失项一键补装
- **📦 APK 导出** — 选择游戏目录 → 点击导出，支持 debug / release 两种模式
- **♻️ 离线复用** — 已下载的 Gradle 发行包通过本地路径直接引用，构建不再重复联网下载 130MB
- **🛡️ 可靠性保障** — 下载完整性校验、SDK 许可证预写（跳过交互确认）、安装结果真实验证（杜绝"假成功"）

## 🚀 快速开始

1. 从 [Releases](https://github.com/ttq7/TyranoToolKit/releases/latest) 下载 `TyranoToolKit.exe`
2. 双击运行（绿色单文件，无需安装）
3. 在依赖检测页点击 **一键安装**（首次约需 10 分钟，取决于网速）
4. 选择你的暴龙编辑器游戏项目目录
5. 点击 **导出 APK**，完成后 APK 位于项目输出目录

> 💡 建议把本工具目录加入杀毒软件白名单，避免运行时依赖被误删。

## 🔧 自动部署的组件

| 组件 | 版本 | 下载源 |
|---|---|---|
| Node.js | v22 LTS | npmmirror |
| Cordova CLI | 13.x | npm（npmmirror registry） |
| Android SDK | platform-tools / android-36 / build-tools 36.0.0 | 清华镜像 → Google 官方 |
| JDK | Temurin 17 LTS | 清华镜像 → Adoptium 官方 |
| Gradle | 8.11.1 | 清华镜像 → 腾讯云 → 官方 |

## 🛠️ 从源码构建

需要 .NET 9 SDK（Windows x64）：

```bash
git clone https://github.com/ttq7/TyranoToolKit.git
cd TyranoToolKit
dotnet publish TyranoToolKit.csproj -c Release -o publish
```

产物为 `publish/TyranoToolKit.exe` 单文件程序。

## ❓ 常见问题

**Q: 导出时报 Gradle 版本不匹配？**
在依赖检测页重新检测，工具会提示把 Gradle 升级到 8.11.1，点击"安装 Gradle"即可自动升级。

**Q: 下载失败 / 卡住？**
工具内置多个镜像源会自动切换；如果全部失败，检查网络或代理设置后重试，已下载的组件不会重复下载。

**Q: 在虚拟机里运行要注意什么？**
虚拟机需能正常联网。依赖检测页会如实报告缺失组件（如 build-tools），按提示补装即可。

## 📄 许可证

[MIT](LICENSE) © 2026 ttq7
