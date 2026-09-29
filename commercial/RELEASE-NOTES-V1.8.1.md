# ADB Mirror Studio V1.8.1

`V1.8.1` 为设备性能监测补充 Qualcomm Adreno KGSL 兼容，并明确区分“设备未提供数据”与“系统未向 ADB shell 开放 DDR 计数器”。已有 Mali GPU、Rockchip DMC/DDR、CPU、内存和温度监测保持兼容。

## 修复与改进

- 在通用 devfreq/Mali GPU 节点不可用时，读取 `/sys/class/kgsl/kgsl-3d0/gpubusy`。
- 按 KGSL 返回的 busy/total 时间计算整机 GPU 占用；GPU 空闲或关闭时的 `0 0` 显示为 0%。
- 保留通用 GPU `load` 节点优先级，避免改变已支持设备的数据源。
- Qualcomm 设备没有可读 DMC/DDR 节点时，DDR 当前值、30 秒平均值和频率显示“系统未开放”。
- 不使用内存容量、GPU 频率、帧率或其他总线指标伪造 GPU/DDR 占用。
- 更新设备性能监测说明、版本信息和下载链接。

## 验证与边界

- Release x64 构建、242 项单元测试、26 项发行脚本检查和隐私审计通过。
- Android 16 真机 `925c23bb`（Qualcomm SM8650）连续采样得到 KGSL GPU 当前值与 30 秒平均值；空闲时稳定显示 0%。
- 同一设备的 DDR bwmon/devfreq 与跟踪事件受 SELinux 限制，ADB shell 无法读取有效带宽计数，因此界面显示“系统未开放”。
- Android 12 Rockchip/Mali 设备原有 devfreq GPU 与 DMC/DDR 路径及异常样本过滤逻辑保持不变。
- 当前版本没有适合发布到 NuGet、npm、Maven、容器或其他 GitHub Packages 注册表的公共包；Windows 安装版与便携版继续通过 GitHub Release 分发。

## 发行产物

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `ADB-Mirror-Studio-Setup-V1.8.1-win-x64.exe` | 80,177,823 字节 | `697B9EDA0E5C96322D7535507A8F9A08CF04CE8DF3064680C71A2B36363A60E4` |
| `AdbMirrorStudio-V1.8.1-win-x64.zip` | 117,384,475 字节 | `3D7F68A2D7ED6ABB6D85559851E2D6F4B03F7CDEEAA5158F2E3B47B5E26AAC88` |

安装程序尚未配置 Authenticode 代码签名，Windows 可能显示“未知发布者”。请从本仓库 Release 下载并核对 SHA256。
