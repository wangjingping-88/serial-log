# Serial Log

Windows 串口日志工具，基于 .NET 8 WPF。

## 当前能力

- 一屏 3 x 2 显示多个串口日志窗口，超过 6 个使用分页。
- 每条完整日志行带 PC 接收时间的毫秒级时间戳。
- 支持串口窗口标题重命名、快速连接、连接全部、断开全部、自动保存和手动导出。
- 支持单条命令发送到一个或多个串口窗口。
- 支持命令组：每组可绑定目标串口、命令列表、命令间隔和换行方式。
- 支持 AT 命令导入：
  - 普通 AT 列表，一行一条命令。
  - 字面量 `\r\n` 分隔的 AT 列表。
  - `.c/.h` 文件中的 `AT_CMD_EXPORT(...)`。
  - 从串口日志中解析 `AT&L` 查询结果。
- 日志按连接会话目录保存，避免多次连接日志混在一起。

## 项目结构

```text
src/SerialLog.App   WPF 桌面应用
src/SerialLog.Core  串口、日志、命令组、AT 导入等核心逻辑
src/SerialLog.Cli   TDMA 自动化辅助命令行工具
tests/SerialLog.Tests 单元测试
docs/ 使用说明和辅助文档
```

## 环境要求

- Windows
- .NET 8 SDK

本机开发环境使用：

```powershell
$env:DOTNET_ROOT='D:\Program Files\dotnet'
$env:PATH='D:\Program Files\dotnet;' + $env:PATH
```

## 构建和测试

```powershell
dotnet restore SerialLog.sln
dotnet build SerialLog.sln -c Debug --no-restore
dotnet test SerialLog.sln --no-restore
dotnet publish src\SerialLog.App\SerialLog.App.csproj -c Release -o D:\serial-log-data\publish
```

注意：Windows 下不要并行执行 `dotnet test` 和 `dotnet build`，否则可能因为输出 DLL 被锁导致构建失败。

## 使用说明

详见 [docs/使用说明.md](docs/使用说明.md)。

## 后续计划

后续需要支持串口窗口拖动排序、命令窗口停靠/浮动、跨 PC 协作等较大的架构演进，详见 [docs/架构演进规划.md](docs/架构演进规划.md)。
