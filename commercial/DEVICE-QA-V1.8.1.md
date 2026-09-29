# ADB Mirror Studio V1.8.1 真机验收

测试设备：`925c23bb`，型号 `24031PN0DC`，Android 16，Qualcomm SM8650，Adreno GPU，Windows x64 桌面客户端。

| 项目 | 结果 |
|---|---|
| GPU 数据源 | `/sys/class/kgsl/kgsl-3d0/gpubusy` 可由 ADB shell 读取，返回 busy/total 时间 |
| 当前 GPU 占用 | 生产采样链路观测到 11.1%，随后设备空闲时显示 0.0% |
| GPU 30 秒平均值 | 连续三次采样从 11.1% 更新为 5.6%、3.7% |
| GPU 空闲值 | 驱动返回 `0 0` 时解析为 0%，不误报为不可用或除零错误 |
| GPU 温度 | `gpuss-*` 热区可读，连续采样约 35.3–35.7 °C |
| DDR 节点 | Qualcomm bwmon/devfreq 节点及 DCVS 跟踪事件受 SELinux 限制，ADB shell 无法取得有效带宽计数 |
| DDR 界面状态 | 当前值、30 秒平均值和频率均显示“系统未开放”，性能提示说明没有可读节点 |
| 设备识别 | 当前设备显示为 `24031PN0DC（925c23bb）`，未混用其他设备的采样结果 |
| 生产链路 | `AdbService → DevicePerformanceParser → MainViewModel` 连续采样和界面文本格式验证通过 |
| 自动化验证 | 242 项 Release 单元测试、26 项发行脚本检查、隐私审计和 `git diff --check` 通过 |
| 发行构建 | V1.8.1 Windows x64 自包含便携包与 NSIS 安装包生成成功，文件版本为 `1.8.1.0` |

本次验收覆盖 Qualcomm GPU/DDR 兼容状态、原有性能采样回归和发行构建。由于系统没有向非 root ADB shell 开放 DDR 带宽计数器，本版本不会显示无法证实的 DDR 百分比。
