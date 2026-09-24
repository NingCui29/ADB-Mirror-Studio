# ADB Mirror Studio

ADB Mirror Studio 是面向 Windows 的 Android 设备连接、屏幕镜像、录屏、文件传输和环境诊断工作台。界面采用 WinUI 3 与 Desktop Acrylic，ADB、scrcpy 和应用运行时均可随便携包分发。所有功能永久免费，无账户、无试用、无订阅、无激活和功能分级。

> 当前版本：`V1.8.0`。个人、组织和企业均可在合法授权的设备上免费使用全部功能。

## 系统要求

- Windows 10 1809（Build 17763）或更高版本
- x64 处理器
- Android 5.0 或更高版本；音频转发通常要求 Android 11 或更高版本
- USB 连接需要可用的数据线和正确的设备驱动
- 无线连接要求电脑与手机网络互通

安装版和便携版均为自包含版本，无需另外安装 Python、.NET、ADB 或 scrcpy。

正常退出会停止本程序启动的镜像会话，并清理发布包 `Tools` 目录中的 ADB/scrcpy 进程，因此可以直接移动、覆盖或删除完整解压目录。系统中来自其他目录的 ADB 不会被终止。

## 功能概览

### 设备连接

- 异步刷新 USB 与无线 ADB 设备
- 同一分发形态内单实例运行；重复启动会唤醒已有窗口，不会打开第二个客户端
- 支持 IPv4、IPv6 和主机名连接
- 支持 Android 11+ mDNS 发现和六位配对码安全无线配对
- USB 设备切换至指定 ADB TCP/IP 端口
- 记忆最近 10 个通过应用成功连接的无线地址，可手动选择和清除；启动时不会自动重连
- 无线设备统一显示为“设备名称 IP:端口”，便于在多设备环境中确认目标
- 连接类型和在线、离线、未授权状态紧跟设备名称显示
- TCP/IP、重启和断开操作集中在当前设备的“概览 → 连接管理”中
- 设备重启与无线连接断开
- 首次只有一台在线设备时自动选中；目标断开后保持未选择，需手动确认新的操作设备
- “概览”约每 2 秒显示当前设备的整机 CPU/GPU/内存/DDR 带宽占用、最近 30 秒平均占用、CPU/GPU 温度及 DDR 频率；设备未提供可读 GPU、DDR 或温度节点时显示“不可用”

### 镜像与录屏

- 基于 scrcpy 4.1 的低延迟镜像和控制
- 流畅、均衡、高清、演示四档预设
- MP4 或 MKV 镜像录制
- 单设备会话去重、会话列表、停止操作和退出回收
- 应用退出时清理由本应用启动的镜像进程
- 每个 scrcpy 输出流仅保留最新 65,536 个字符的诊断信息，长时间镜像不会持续累积日志内存
- 自动检测设备支持的视频编码器；高清预设优先 H.265，其他预设优先 H.264，并提供兼容回退
- 屏幕工作区展示镜像预设和设备控制；任务中心集中展示运行中的会话与录制状态
- 多个镜像窗口可按宫格、横向或纵向自动排列
- 录制中心展示本次运行期间的录制文件、设备和完成状态
- 可在运行中的镜像会话直接开始或停止录制；软件会保存窗口位置并自动快速重启该设备的 scrcpy 会话
- 录制切换失败时自动恢复原普通镜像，避免因无效路径或启动错误丢失当前会话
- 录制完成后显示输出文件大小，并可从录制中心直接打开所在目录
- 电脑没有默认音频输出设备时自动关闭本地音频播放，录屏仍可保留设备音轨
- 录屏不覆盖已有非空文件；正常退出会等待会话结束，强制终止或异常退出会提示录制文件可能不完整

| 预设 | 最大尺寸 | 帧率 | 视频码率 | 适用场景 |
|---|---:|---:|---:|---|
| 流畅 | 1280 | 60 FPS | 4 Mbps | 网络或设备性能有限 |
| 均衡 | 1920 | 60 FPS | 8 Mbps | 日常操作，默认选项 |
| 高清 | 2560 | 60 FPS | 16 Mbps | 演示、截图和高画质需求 |
| 演示 | 1920 | 30 FPS | 8 Mbps | 稳定帧率的会议演示与录屏 |

### 文件传输与应用管理

- APK 覆盖安装，使用 `adb install -r` 保留现有应用数据
- 单文件、多文件选择和资源管理器拖放
- 文件队列、大小及逐项状态显示
- 将文件推送到设备 `/sdcard/Download/`
- 任务取消、失败隔离和完成统计
- 中文、空格及特殊字符路径作为独立进程参数安全传递
- 从设备绝对路径下载单个文件或目录到本地
- 中文目录逐层下载并保留空目录；单文件先写临时文件，成功后替换同名目标，失败保留原文件
- 目录下载最多 10,000 个条目，路径列表每次最多 1,000,000 个字符；不完整列表、Windows 非法文件名和大小写重名会明确报错
- APK 安装路径与普通文件上传队列独立，手动修改上传路径会同步更新队列

同名文件推送时由 ADB 覆盖目标文件。安装来源不明的 APK 前，应先验证发布者和文件哈希。

### 设备操作

- 查看 Android 版本、API 级别、屏幕分辨率、电池及数据分区存储状态
- Android、API、分辨率、电池和存储信息通过独立 ADB 命令并行采集
- 将当前设备画面保存为 PNG 文件
- 导出最近 2000 行 Logcat 到 UTF-8 文本文件
- 所有操作只对用户主动选择且已授权的设备执行
- 快捷发送返回、主页、最近任务、电源及音量按键
- 读取第三方用户应用，并执行启动、强制停止或经确认后的卸载
- 支持不读取应用列表，直接输入 Android 包名并确认卸载
- 提供绑定所选设备的 ADB Shell 高级控制台；不调用 Windows `cmd.exe`，命令和输出不持久化

### 诊断、设置与隐私

- 检查随包 ADB、scrcpy、mDNS 服务和 PATH 版本冲突
- 跟随系统、浅色和深色主题
- 每 5 秒自动刷新；无线连接历史仅保存在本机并始终由用户手动连接
- 设置使用原子 JSON 写入，降低异常退出导致配置损坏的风险
- 未处理异常写入本机崩溃日志
- 默认不包含遥测、广告 SDK 或自动日志上传
- 首次运行展示设备权限和数据使用说明
- 通过公开 GitHub Release 检查免费更新；可在应用内下载 Windows x64 安装包，校验 GitHub 资产大小与 SHA256 后启动安装

## 安装或便携运行

推荐安装方式：

1. 下载与当前公开版本对应的 `ADB-Mirror-Studio-Setup-V主版本.次版本.修订版本-win-x64.exe`。
2. 双击安装程序，阅读隐私说明和免费使用许可。
3. 选择安装目录；默认安装到当前用户的 `%LocalAppData%\Programs\ADB Mirror Studio`，无需管理员权限。
4. 从开始菜单启动。安装程序支持覆盖升级，升级不会删除 `%LocalAppData%\AdbMirrorStudio` 中的设置。
5. 可从 Windows“已安装的应用”或开始菜单卸载；交互式卸载时可选择是否一并删除本机设置和崩溃日志。

便携方式：

1. 完整解压与当前公开版本对应的 `AdbMirrorStudio-V主版本.次版本.修订版本-win-x64.zip`，不要直接在压缩包中运行。
2. 进入解压后的目录。
3. 双击 `AdbMirrorStudio.App.exe`。
4. 阅读首次运行说明并选择“同意并开始”。

若 Windows SmartScreen 显示“未知发布者”，请先核对发布渠道提供的 SHA256。当前便携包尚未配置正式代码签名；后续签名不会成为任何功能的付费条件。

安装版与便携版共享 `%LOCALAPPDATA%\AdbMirrorStudio` 设置目录。Windows App SDK 的应用身份边界可能允许两者同时运行，请勿并行启动两种分发版本。

## 连接设备

### USB 连接

1. 在手机中启用“开发者选项”和“USB 调试”。
2. 使用支持数据传输的 USB 线连接电脑。
3. 在手机弹窗中允许此电脑进行 USB 调试。
4. 在左侧设备栏点击刷新并选择目标设备。

显示“未授权”时，请解锁手机并处理授权弹窗。必要时在手机开发者选项中撤销 USB 调试授权，然后重新连接。

### Android 11+ 无线配对

1. 手机进入“开发者选项 → 无线调试”。
2. 选择“使用配对码配对设备”。
3. 在设备栏的“添加设备 → 无线配对”中输入手机显示的配对地址和六位配对码。
4. 配对成功后，使用无线调试主页面显示的连接地址建立连接。

配对端口与连接端口通常不同。

### USB 切换无线调试

1. 先确保 USB 设备在线且已授权。
2. 进入当前设备的“概览 → 连接管理”，点击“启用 TCP/IP”。
3. 输入监听端口，默认 `5555`。
4. 获取手机当前局域网 IP，在无线地址中输入 `手机IP:端口` 后连接。
5. 无线连接成功后再拔出 USB 线。

不要把 ADB TCP/IP 端口暴露到公网。建议只在可信局域网中使用。

## 镜像与录屏

1. 在左侧设备栏连接并选择在线设备。
2. 打开当前设备的“屏幕”工作区并选择质量预设。
3. 如需启动镜像时直接录屏，选择 `.mkv` 或 `.mp4` 保存路径。
4. 点击“开始镜像”；运行后同一位置只保留可用的停止操作。
5. 在“任务中心”查看会话和录屏文件，并在两个及以上会话时排列镜像窗口。

录屏由 scrcpy 进程完成。磁盘空间不足、输出文件被占用或设备断开会导致录制停止；异常详情会显示在应用状态区域。

## 文件传输与应用安装

1. 在左侧设备栏选择目标设备并打开“文件”工作区。
2. 在“发送到设备”中选择或拖入本地文件，再推送到设备 Download 目录。
3. 在“从设备获取”中输入以 `/` 开头的设备绝对路径并选择本地目录。
4. 需要终止上传任务时点击“取消”。
5. 安装 APK 或管理包名时切换到“应用”工作区。

取消会终止当前 ADB 子进程，已经完成的文件不会自动删除。

## 设备操作、应用与终端

1. “概览”提供设备详情、截图、连接管理，以及当前设备的 CPU/GPU/内存/DDR 带宽占用、CPU/GPU 温度和 DDR 频率。CPU 温度取最高的明确 CPU 热区；采样规则见 [设备性能监测说明](https://github.com/NingCui29/ADB-Mirror-Studio/blob/master/docs/device-performance-monitoring.md)。
2. “屏幕”提供返回、主页、最近任务、电源、音量和截图快捷控制。
3. “应用”读取、启动、强制停止或卸载用户应用，也支持直接按包名管理。
4. “终端”提供绑定当前设备的 ADB Shell 和 Logcat 导出。

设备日志可能包含应用名称、系统事件或其他敏感信息；分享前请自行审阅和脱敏。

## 数据与日志

应用数据默认保存在：

```text
%LOCALAPPDATA%\AdbMirrorStudio\
  settings.json
  Crash\
    crash-yyyyMMdd-HHmmssfff.log
```

删除该目录会重置主题、自动刷新、无线连接历史、镜像预设及首次运行状态。删除前请先退出应用。

相关文档：

- [隐私说明](PRIVACY.md)
- [免费使用许可](FREE-USE-LICENSE.md)
- [第三方组件归属](THIRD-PARTY-NOTICES.md)

## 常见问题

### 应用无法启动

- 必须先完整解压，不能直接在 ZIP 内运行。
- 确认系统为 x64 Windows 10 1809 或更高版本。
- 检查 `%LOCALAPPDATA%\AdbMirrorStudio\Crash` 是否产生新日志。
- 杀毒软件可能隔离 ADB、scrcpy 或相关 DLL；应从可信渠道重新下载并核验哈希，不建议盲目添加全盘白名单。

### 未发现 USB 设备

- 更换支持数据传输的线缆或 USB 接口。
- 将手机 USB 用途切换为“文件传输”。
- 检查手机端 USB 调试授权。
- 安装设备厂商 USB 驱动。
- 在“设置中心 → 系统健康”检查 ADB 是否正常以及 PATH 中是否存在冲突版本。

### 无线连接失败

- 确认电脑与手机之间可以互相访问，访客 Wi-Fi 可能隔离设备。
- 确认使用的是连接端口，不是配对端口。
- Android 11+ 设备重启或关闭无线调试后，端口可能变化。
- VPN、防火墙和企业网络策略可能阻止连接。
- IPv6 地址应使用标准端点格式，例如 `[2001:db8::10]:5555`。

### 镜像窗口启动后立即退出

- 若日志包含 `Could not open audio device: No default audio device available`，说明 Windows 没有默认声音输出设备，末尾的 `Demuxer error` 是后续错误。程序会在启动或切换录制时重新检测，自动关闭本机声音播放；启用音频的录屏仍保留设备音轨。接入并设置默认扬声器或耳机后，重新启动镜像即可恢复本机播放。
- 确认设备状态为“在线”。
- 在“设置中心 → 系统健康”检查 scrcpy 及其 DLL。
- 尝试“流畅”预设并关闭录屏。
- 某些厂商设备需要额外启用“USB 调试（安全设置）”才能注入触摸和键盘事件。

### APK 安装失败

- 检查设备剩余空间和 Android 版本要求。
- 签名不同的同包名应用不能直接覆盖安装。
- 企业设备策略可能禁止未知来源或 ADB 安装。
- `.apks`、`.xapk` 等分包容器不是单个 APK，当前安装入口不处理这类格式。

## 技术架构

```text
AdbMirrorStudio.Domain
  设备、镜像、设置领域模型
          ↓
AdbMirrorStudio.Application
  ADB、镜像、诊断、设置接口与业务协调
          ↓
AdbMirrorStudio.Infrastructure
  安全进程执行、ADB、scrcpy、JSON 持久化与诊断实现
          ↓
AdbMirrorStudio.App
  WinUI 3、Desktop Acrylic、页面和视图模型
```

关键设计原则：

- 不通过 shell 拼接 ADB 或 scrcpy 命令，全部使用独立 `ArgumentList`。
- 所有外部进程支持超时、取消和进程树清理。
- 设备刷新使用序列协调器丢弃过期结果。
- 单设备只保留一个由本应用管理的镜像会话。
- 设置先写临时文件，再以替换方式提交。
- 应用使用 Per-Monitor V2 DPI 感知，并依据当前显示器工作区限制默认窗口大小。

项目结构：

```text
commercial/
  src/
    AdbMirrorStudio.Domain/
    AdbMirrorStudio.Application/
    AdbMirrorStudio.Infrastructure/
    AdbMirrorStudio.App/
  tests/AdbMirrorStudio.UnitTests/
  installer/AdbMirrorStudio.nsi
  scripts/build-release.ps1
  scripts/build-installer.ps1
  artifacts/release/
  artifacts/installer/
```

## 开发环境

- .NET 10 SDK
- 支持 .NET 10 和 WinUI 3 的 Visual Studio
- Windows 10/11 SDK
- PowerShell 7 或 Windows PowerShell 5.1
- 构建安装程序时需要 NSIS 3（可通过 `NSIS_COMPILER` 指定 `makensis.exe`）

仓库根目录已有项目专用 SDK 时，可运行：

```powershell
.\.tools\dotnet\dotnet.exe --info
```

## 构建与测试

在仓库根目录运行测试：

```powershell
.\.tools\dotnet\dotnet.exe test `
  .\commercial\tests\AdbMirrorStudio.UnitTests\AdbMirrorStudio.UnitTests.csproj `
  --configuration Release
```

构建未打包 WinUI 应用：

```powershell
.\.tools\dotnet\dotnet.exe build `
  .\commercial\src\AdbMirrorStudio.App\AdbMirrorStudio.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  /p:Unpackaged=true
```

在 `commercial` 目录生成完整便携包：

```powershell
.\scripts\build-release.ps1
```

便携发布脚本会：

1. 在独立临时目录运行 Release 单元测试并生成 Windows x64 自包含发布目录。
2. 复制 README、隐私说明、免费使用许可和第三方归属文件。
3. 运行 Git 跟踪文件及未忽略新增文件的隐私审计，并从发布目录移除 PDB、转储、日志、本机设置、环境变量文件和密钥材料。
4. 根据 `Directory.Build.props` 自动创建带有当前版本号的便携压缩包。
5. 输出 ZIP 的 SHA256 和字节大小，并自动清理临时目录。

发布脚本会核对版本清单、运行时、WinUI 资源和随包工具；构建失败时保留上次成功产物。安装器卸载仅清理发行文件清单中的文件，保留用户额外放入安装目录的文件。

发布产物：

```text
commercial\artifacts\release\
  AdbMirrorStudio-V1.8.0-win-x64.zip
```

生成安装版：

```powershell
.\scripts\build-installer.ps1
```

安装器脚本会先生成最新便携包，再使用 NSIS 创建当前用户级 x64 安装程序。可用 `-SkipPortableBuild` 复用现有便携包；可通过 `ADB_MIRROR_SIGN_COMMAND` 提供包含 `%1` 文件占位符的 Authenticode 签名命令，同时签署安装器与卸载器。

安装产物：

```text
commercial\artifacts\installer\
  ADB-Mirror-Studio-Setup-V1.8.0-win-x64.exe
```

## 第三方组件

- Android Debug Bridge 1.0.41 / Platform Tools 37.0.0
- scrcpy 4.1
- SDL 3.4.12
- FFmpeg 8.1.2 对应的 62.x 动态库
- libusb 1.0.30
- NSIS 3.12（仅用于生成安装程序）

正式发行必须保留第三方版权声明、适用许可文本和源码获取方式。不得删除或隐藏开源组件归属。

## 版本与问题修复策略

项目采用三段式版本号：

- 小问题发布修订版本：`1.0.0 → 1.0.1`
- 大问题或保持兼容的重要升级发布次版本：`1.0.0 → 1.1.0`
- 不兼容的架构、界面或平台升级发布主版本：`1.0.0 → 2.0.0`

正式发布后的版本和标签不覆盖。修复已经发布版本中的问题时必须递增版本号、重新构建并创建新的 Release。详细定级、路线和发布门槛见[版本管理与问题修复计划](VERSIONING.md)。

## 发行质量检查表

- [ ] 确认正式产品名、公司法定名称、联系信息和品牌资产
- [ ] 将 MSIX Publisher 与代码签名证书主体保持一致
- [ ] 使用可信代码签名证书签署 EXE、MSIX 或安装程序
- [x] 确认所有功能永久免费且不加入付费授权、试用或订阅限制
- [x] 通过 HTTPS 读取公开 GitHub Release，安全下载、校验并启动 Windows x64 安装包
- [ ] 完成中文、英文资源及语言切换验收
- [ ] 补充适用地区、支持渠道、责任限制和最终免费使用许可文本
- [ ] 补齐 SDL、FFmpeg、libusb 的许可原文和源码获取义务
- [ ] 运行 Windows App Certification Kit
- [ ] 在 Windows 10/11、100%–250% DPI、多显示器环境测试
- [ ] 覆盖 Android 5–16、USB、Android 11+ 无线调试和主流厂商设备
- [ ] 执行弱网、拔线、设备重启、磁盘不足、权限拒绝及长时间运行测试
- [x] 完成当前用户级静默安装、覆盖升级和卸载验证
- [x] 发布 SHA256、版本说明和隐私政策
- [ ] 公布正式支持渠道

未完成以上质量项目不会限制软件功能；发布页面应如实说明代码签名和法律材料状态。
