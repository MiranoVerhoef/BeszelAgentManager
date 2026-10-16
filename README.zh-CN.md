# BeszelAgentManager 4

[English](README.md)

BeszelAgentManager 是用于安装、配置、更新和监控 [Beszel](https://beszel.dev) 代理的 Windows 桌面工具，基于 .NET 10 和 WinUI 3。

## 切换为中文

1. 打开 **Extra → Language → Switch language**。
2. 选择 **简体中文**。
3. 在弹出的确认框中选择 **Restart now**，重启后应用中文。

默认保留英文原版。切回英文时，在 **更多 → 语言 → 切换语言** 中选择 **English (original)**，并选择“立即重启”。选择“稍后”会保存语言偏好，在下次启动时生效；重复选择已保存的语言不会弹窗。

安装器也可以选择英文或简体中文；安装器和应用的语言选择相互独立。日志、上游发行说明和外部错误详情保留原文，便于排查问题。

## 主要功能

- 安装、更新、重装或卸载 Beszel 代理，并选择受支持的版本；后台服务阻止版本降级。
- 配置 Hub 地址、公钥、令牌、监听端口和受支持的环境变量。
- 从主界面或系统托盘启动、停止和重启代理服务。
- 配置自动更新、定期重启、日志轮转、DNS 备用地址和 WebSocket 离线退避。
- 查看管理器和代理日志，导出脱敏支持包，查看或重置代理指纹。
- 使用项目签名元数据验证管理器更新，提供可选的 Defender 排除项和第三方杀毒软件路径说明。

日常特权操作由受保护的 LocalSystem 后台服务执行，初次安装、升级、卸载及打开服务编辑器仍需管理员权限。

## 安装与使用

支持 Windows 10 1809 及以上、Windows 11，以及带桌面体验的 Windows Server 2019、2022、2025，均为 x64。Windows Server Core 不受支持。

在项目发行页面选择安装包：

- `BeszelAgentManagerSetup.exe`：完整版本，包含 .NET 10 运行时。
- `BeszelAgentManagerSetup-Lite.exe`：精简版本，需预先安装 Microsoft .NET 10 桌面运行时 x64。

按照发行页面的 `SHA256SUMS.txt` 核对安装包后运行安装程序。首次使用时，在“连接”页面填入 Hub 连接信息，选择“安装代理”；修改代理配置后选择“应用设置”。

主要目录：

| 内容 | 默认路径 |
| --- | --- |
| 管理器 | `C:\Program Files\BeszelAgentManager` |
| Beszel 代理 | `C:\Program Files\Beszel-Agent` |
| 管理器配置和界面日志 | `C:\ProgramData\BeszelAgentManager` |
| 后台服务数据和代理日志 | `C:\ProgramData\BeszelAgentManager.ServiceData` |

语言保存在配置文件的 `ui_language` 字段中：`en-US` 为英文，`zh-Hans` 为简体中文。语言选择不会修改代理环境变量、服务名称或更新设置。

卸载管理器和卸载代理是不同操作。代理卸载提供保留历史日志的选项。自动更新会验证项目签名元数据；Windows 发布者签名是另一回事，系统仍可能显示发布者或 SmartScreen 提示。

## 从源码构建

需要 Windows x64、.NET 10 SDK 和 Inno Setup 6，建议使用 PowerShell 7。

```powershell
dotnet restore BeszelAgentManager.sln
dotnet build src\BeszelAgentManager.WinUI -c Release -p:Platform=x64 -r win-x64 --self-contained true
```

完整安装包和 Lite 安装包的暂存目录、NSSM 准备及安装器编译命令见 [英文构建说明](README.md#build-from-source)。翻译维护方式见 [Localization](docs/localization.md)。

## 致谢

- BeszelAgentManager：Verhoef
- Beszel：[beszel.dev](https://beszel.dev)
