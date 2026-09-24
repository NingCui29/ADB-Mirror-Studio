# ADB Mirror Studio

<p align="center">
  <img src="commercial/src/AdbMirrorStudio.App/Assets/Square150x150Logo.scale-200.png" width="128" alt="ADB Mirror Studio 应用图标" />
</p>

面向 Windows 的 Android 设备连接、低延迟屏幕镜像、录屏、文件传输与诊断工具。全部功能免费，无账户、无订阅、无激活、无广告和功能分级。

## 下载

当前版本：`V1.8.0`

- [GitHub Release](https://github.com/NingCui29/ADB-Mirror-Studio/releases/tag/V1.8.0)
- [下载安装版（推荐）](https://github.com/NingCui29/ADB-Mirror-Studio/releases/download/V1.8.0/ADB-Mirror-Studio-Setup-V1.8.0-win-x64.exe)
- [下载便携版](https://github.com/NingCui29/ADB-Mirror-Studio/releases/download/V1.8.0/AdbMirrorStudio-V1.8.0-win-x64.zip)

SHA256：

```text
安装版  B444D9B71A723B7D22A842F7ED338A4651575C64C9BF065FAA17D334A2AF1D70
便携版  311589A74AC08506247C758938729731A70EF3FD60D3B174A1FB9C676420C15B
```

## 主要功能

- USB、IPv4、IPv6 和主机名 ADB 连接
- 固定设备栏与单一当前设备上下文；设备功能统一归入概览、屏幕、文件、应用和终端
- 任务中心集中展示镜像会话、文件任务、录屏文件和多窗口排列
- 设置中心集中管理外观、设备刷新、系统健康、更新与本地数据
- 同一分发形态内单实例运行；重复启动会唤醒已有窗口
- Android 11+ mDNS 发现与六位配对码无线配对
- 记忆最近 10 个成功连接的无线地址，仅供手动选择，绝不自动重连
- 流畅、均衡、高清、演示四档 scrcpy 镜像预设
- 自动选择 H.264、H.265 等设备支持的视频编码器
- MP4/MKV 录屏、录制中心和镜像会话管理
- 在运行中的镜像会话直接开始或停止录制，并保留镜像窗口位置
- 多设备镜像窗口宫格、横向或纵向排列
- APK 覆盖安装
- 多选、拖放、逐项状态和可取消的文件上传队列
- 从设备下载文件或目录
- 设备详情、电池、存储、截图和 Logcat 导出
- 当前设备整机 CPU/GPU/内存/DDR 带宽实时占用、最近 30 秒平均占用、CPU/GPU 温度及 DDR 频率
- 返回、主页、最近任务、电源和音量快捷控制
- 用户应用读取、启动、强制停止和确认卸载
- 无需读取应用列表，直接按 Android 包名确认卸载
- 指定目标设备的内置 ADB Shell 控制台，不调用 Windows cmd.exe
- ADB、scrcpy、mDNS 与 PATH 冲突诊断
- WinUI 3、Desktop Acrylic、浅色/深色主题和 Per-Monitor V2 DPI 感知
- 1500 px 以上使用宽屏双列，980–1499 px 使用单列，小于 980 px 时设备栏移动到内容上方
- 检测 GitHub 新版本，在校验文件名、大小和 SHA256 后下载并启动安装程序

## 系统要求

- Windows 10 1809（Build 17763）或更高版本
- x64 处理器
- Android 5.0 或更高版本
- USB 调试或无线调试已启用

安装版和便携版均为自包含版本，已包括 .NET、Windows App SDK、ADB 和 scrcpy。

## 安装与运行

### 安装版

1. 下载 `ADB-Mirror-Studio-Setup-V1.8.0-win-x64.exe`。
2. 核对上方 SHA256 后运行安装程序。
3. 从开始菜单打开 ADB Mirror Studio。

默认安装在当前用户目录，无需管理员权限。安装器支持覆盖升级和卸载，并默认保留本机设置。

### 便携版

1. 下载 `AdbMirrorStudio-V1.8.0-win-x64.zip`。
2. 完整解压 ZIP，不要直接在压缩包中运行。
3. 双击 `AdbMirrorStudio.App.exe`。

当前安装程序尚未配置 Authenticode 代码签名，Windows 可能显示“未知发布者”。请只从本仓库 Release 下载并核对 SHA256。

安装版与便携版共享 `%LOCALAPPDATA%\AdbMirrorStudio` 设置目录。Windows App SDK 的应用身份边界可能允许两者同时运行，请勿并行启动两种分发版本。

## 快速连接

### USB

1. 在手机中开启“开发者选项”和“USB 调试”。
2. 使用支持数据传输的 USB 线连接电脑。
3. 在手机端允许 USB 调试授权。
4. 在左侧设备栏刷新并选择设备，再进入“屏幕”工作区启动镜像。

### Android 11+ 无线配对

1. 打开手机“开发者选项 → 无线调试”。
2. 选择“使用配对码配对设备”。
3. 在设备栏的“添加设备 → 无线配对”中输入配对地址和六位配对码。
4. 配对后使用无线调试主页面显示的连接地址手动连接。

配对端口和连接端口通常不同。不要将 ADB 端口暴露到公网。

## 应用内更新

检查到后续新版本时，“设置中心 → 更新”会提供“下载并安装”：

1. 只接受固定 GitHub 仓库的 HTTPS Windows x64 安装包。
2. 安装包名称必须与目标版本一致。
3. 下载到 `.download` 临时文件并显示进度，可随时取消。
4. 文件大小和 GitHub SHA256 均一致后才更名并启动。
5. 启动安装器后正常关闭当前应用，继续覆盖升级。

校验信息缺失或不一致时不会启动文件，界面会显示错误信息。

## 隐私与安全

- 不收集或上传设备信息、连接地址、文件路径、镜像内容和日志
- 不包含遥测、广告 SDK、在线激活或商业授权服务
- 设置、无线连接历史和崩溃日志仅保存在 `%LOCALAPPDATA%\AdbMirrorStudio`
- 无线地址只记忆、不自动重连
- Git 通过忽略规则和 CI 审计阻止设置、日志、令牌、私钥及本机路径进入仓库
- 正式发布包不包含 PDB、转储、日志、环境变量文件、密钥材料或本机用户路径

## 构建

需要 .NET 10 SDK、Windows 10/11 SDK；生成安装器还需要 NSIS 3。

运行隐私审计：

```powershell
.\commercial\scripts\test-privacy.ps1
```

运行测试：

```powershell
.\.tools\dotnet\dotnet.exe test `
  .\commercial\tests\AdbMirrorStudio.UnitTests\AdbMirrorStudio.UnitTests.csproj `
  --configuration Release
```

生成便携包：

```powershell
.\commercial\scripts\build-release.ps1
```

生成安装程序：

```powershell
.\commercial\scripts\build-installer.ps1
```

## 工程结构

- `AdbMirrorStudio.Domain`：设备、镜像和设置模型
- `AdbMirrorStudio.Application`：业务接口与协调逻辑
- `AdbMirrorStudio.Infrastructure`：ADB、scrcpy、进程、诊断、持久化和更新实现
- `AdbMirrorStudio.App`：WinUI 3 页面与视图模型
- `AdbMirrorStudio.UnitTests`：解析、命令、安全更新、设置和并发回归测试

外部命令使用独立参数列表执行，不通过 shell 拼接；支持超时、取消和进程树清理。

## 文档

- [完整使用、开发和发布手册](commercial/README.md)
- [V1.8.0 发行说明](commercial/RELEASE-NOTES-V1.8.0.md)
- [V1.8.0 真机验收](commercial/DEVICE-QA-V1.8.0.md)
- [V1.7.0 发行说明](commercial/RELEASE-NOTES-V1.7.0.md)
- [V1.7.0 真机验收](commercial/DEVICE-QA-V1.7.0.md)
- [V1.6.0 发行说明](commercial/RELEASE-NOTES-V1.6.0.md)
- [V1.6.0 真机验收](commercial/DEVICE-QA-V1.6.0.md)
- [设备性能监测说明](docs/device-performance-monitoring.md)
- [V1.5.0 发行说明](commercial/RELEASE-NOTES-V1.5.0.md)
- [V1.5.0 真机验收](commercial/DEVICE-QA-V1.5.0.md)
- [V1.5 UI 信息架构](docs/ui-information-architecture-v1.5.md)
- [V1.4.0 发行说明](commercial/RELEASE-NOTES-V1.4.0.md)
- [V1.3.2 发行说明](commercial/RELEASE-NOTES-V1.3.2.md)
- [V1.3.1 发行说明](commercial/RELEASE-NOTES-V1.3.1.md)
- [V1.3.0 发行说明](commercial/RELEASE-NOTES-V1.3.0.md)
- [V1.2.0 发行说明](commercial/RELEASE-NOTES-V1.2.0.md)
- [V1.1.0 发行说明](commercial/RELEASE-NOTES-V1.1.0.md)
- [V1.0.0 发行说明](commercial/RELEASE-NOTES-V1.0.0.md)
- [版本管理规则](commercial/VERSIONING.md)
- [隐私说明](commercial/PRIVACY.md)
- [免费使用许可](commercial/FREE-USE-LICENSE.md)
- [第三方组件归属](commercial/THIRD-PARTY-NOTICES.md)

本项目仅应用于用户拥有或已获明确授权的 Android 设备。不得用于未经授权的访问、监控或数据复制。
