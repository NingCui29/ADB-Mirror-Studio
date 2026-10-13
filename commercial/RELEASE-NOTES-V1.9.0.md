# ADB Mirror Studio V1.9.0

`V1.9.0` 新增统一的“性能监测”开关，让 CPU、GPU、内存和 DDR 指标按需采样与显示。首次使用和旧版配置默认关闭，开关状态保存在本机。

## 功能与行为

- 在设备工作区“概览”中开启“性能监测”后，立即采样并显示 CPU/GPU/内存/DDR 占用、30 秒平均值、CPU/GPU 温度及 DDR 频率，后续约每 2 秒更新。
- 关闭开关后隐藏全部性能指标与提示，停止采样定时器并取消在途性能采样，不再执行性能采样 ADB 命令。
- 重新开启时清空旧读数、CPU 计算基线及平均值窗口，重新统计；迟到的成功或失败结果不能污染新一轮读数。
- 保存开关选择，下一次启动恢复上次状态；旧配置缺少开关字段时按关闭处理。
- 保留 Mali/devfreq、Qualcomm Adreno KGSL 和 Rockchip DMC/DDR 的现有采样规则与受限数据源提示。

## 升级说明

安装版支持覆盖升级。旧版用户升级后性能监测默认关闭，需要时在“概览”中开启；已保存的新开关状态会继续恢复。设备自动刷新和性能监测是两个独立设置。

## 验证与边界

- Release 单元测试 252 项、发行脚本检查 27 项及隐私审计通过。
- 完成独立目录的全新安装、V1.8.1 覆盖升级和两次卸载验收，原安装信息与设置恢复复核通过。
- ZIP 负载、程序版本与 SHA256 校验通过。
- Windows 界面自动化遇到窗口绑定错误，性能开关点击、显示变化、重启恢复和正常退出尚未完成 GUI 验收；目前开关行为由生产 ViewModel 单元测试覆盖。详细验证范围见 [发布验收](https://github.com/NingCui29/ADB-Mirror-Studio/blob/master/commercial/DEVICE-QA-V1.9.0.md)。

## 发行产物

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `ADB-Mirror-Studio-Setup-V1.9.0-win-x64.exe` | 80,134,108 字节 | `722DDF1367ECD74C3DD2306DC286B29752F1A016EB4E00BAFDC7CA0691673707` |
| `AdbMirrorStudio-V1.9.0-win-x64.zip` | 117,387,559 字节 | `07BC67749D8F8F67A1B54101BE3F85B2985D3BFBE83C8D545A0CAAE993DDC811` |

当前仓库没有 NuGet、npm、Maven 或容器等公共包发布定义；全部可分发程序通过 GitHub Release 提供。

安装程序尚未配置 Authenticode 代码签名。请从本仓库 Release 下载并核对 SHA256。
