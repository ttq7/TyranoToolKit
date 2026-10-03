<div align="center">

<img src="https://capsule-render.vercel.app/api?type=waving&height=200&text=TyranoToolKit&fontSize=58&fontColor=ffffff&animation=fadeIn&color=gradient&customColorList=6,11,20&desc=%E6%9A%B4%E9%BE%99%E7%BC%96%E8%BE%91%E5%99%A8%20HTML5%20%E6%B8%B8%E6%88%8F%20%E2%86%92%20Android%20APK&descSize=18&descY=28" width="100%"/>

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

**[⬇️ 下载最新版](https://github.com/ttq7/TyranoToolKit/releases/latest)** · [快速开始](#-快速开始) · [常见问题](#-常见问题)

<img src="https://count.getloli.com/get/@ttq7-TyranoToolKit?theme=moebooru" alt="访客计数" width="320"/>

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

---

<div align="center">

<img src="https://capsule-render.vercel.app/api?type=rect&height=6&color=gradient&customColorList=6,11,20" width="100%"/>

如果这个工具帮到了你，欢迎点一个 ⭐ Star～

<img src="https://count.getloli.com/get/@ttq7-TyranoToolKit-footer?theme=moebooru" alt="visit" width="240"/>

*Made with 🌸 by ttq7*

</div>
