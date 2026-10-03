# TyranoToolKit

Windows 桌面工具箱，帮助[暴龙编辑器 / TyranoBuilder](https://tyrano.jp/) 视觉小说作者把 HTML5 游戏一键导出为 Android APK——无需手动安装和配置 Node.js、Cordova、Android SDK、JDK、Gradle。

## 功能

- **一键环境部署**：自动下载并安装 Node.js、Cordova CLI、Android SDK（cmdline-tools / platform-tools / platforms / build-tools）、JDK 17、Gradle，全程使用国内镜像（清华 / 腾讯云），下载失败自动切换源并重试
- **依赖检测**：可视化检查各项依赖的安装与版本状态（Gradle 低于最低版本会提示升级），缺失项一键补装
- **APK 导出**：调用 Cordova 将 HTML5 项目打包为 Android APK（支持 debug / release）
- **离线复用**：已下载的 Gradle 发行包通过本地路径直接引用，构建不再重复联网下载

## 环境要求

- Windows 10/11（x64）
- .NET 9 Desktop Runtime（或直接使用 self-contained 发布版）

## 从源码构建

```bash
dotnet publish TyranoToolKit.csproj -c Release -o publish
```

产物为 `publish/TyranoToolKit.exe` 单文件程序。

## 技术栈

- WPF (.NET 9) + MVVM
- Cordova CLI（cordova-android 15.x）
- 依赖组件自动下载与完整性校验、许可证预写（CI 标准做法）

## 许可证

[MIT](LICENSE)
