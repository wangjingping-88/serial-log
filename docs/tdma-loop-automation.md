# TDMA 单播回环自动迭代

`SerialLog.Cli` 用于把 TDMA 单播回环调试固定成可重复的一轮流程：

1. 生成本轮构建版本号。
2. 把版本号写入 WIoTaMesh 固件头文件。
3. 执行普通 `scons`。
4. 调用 YMODEM 工具烧录中心和四个路由节点。
5. 节点启动后发送 `AT+BUILD?`，确认五个节点都运行本轮版本。
6. 对五个节点发送 `AT+FREQ=490000000`。
7. 先对 R1、R2、R3、R4 发送链式拓扑 `AT+TDMABIZPLAN`。
8. 最后对中心发送同一个 `AT+TDMABIZPLAN`。
9. 观察纯同步窗口，遇到异常字段立即停止当前轮。
10. 中心发送一次 `AT+SEND=981,4,11223344`。
11. 判定目标是否达成，例如单播一帧回环成功。
12. 失败则保存日志和 `result.json`，进入下一轮。

## 发布

```powershell
& "D:\Program Files\dotnet\dotnet.exe" publish D:\code\serial-log\src\SerialLog.Cli\SerialLog.Cli.csproj -c Release -o D:\serial-log-data\publish-cli
```

## 配置

复制样例：

```powershell
Copy-Item D:\code\serial-log\docs\tdma-loop-config.example.json D:\serial-log-data\tdma-loop.json
notepad D:\serial-log-data\tdma-loop.json
```

关键字段：

- `BuildVersionPrefix`：版本号前缀，默认 `tdma`。
- `VersionHeaderPath`：构建前写入的固件头文件。
- `VersionQueryCommand`：烧录后确认版本的命令，默认 `AT+BUILD?`。
- `VersionResponsePrefix`：版本响应前缀，默认 `+BUILD:`。
- `FlashFirmware`：为 `true` 时每轮都会烧录五个节点。
- `AbnormalPatterns`：匹配到即停止当前轮，默认 `LOST`。
- `Goal`：当前目标，默认 `unicast_one_frame_loop`。
- `PlanCommands`：五个节点的计划命令。
- 当前命令顺序：全节点 `AT+FREQ=490000000`，R1~R4 `AT+TDMABIZPLAN=0,50,53936,52444,64654,64697,981`，最后中心同一条计划命令。
- `SendCommand`：中心节点单播发送命令。

## 运行

```powershell
D:\serial-log-data\publish-cli\SerialLog.Cli.exe tdma-loop --config D:\serial-log-data\tdma-loop.json
```

每轮目录：

```text
D:\serial-log-data\tdma-loop\<时间>_iter_<序号>\
```

每轮会生成：

- `build-version.txt`：本轮版本号。
- `scons.log`：构建日志。
- `<node>_ymodem.log`：YMODEM 烧录日志。
- `<node>.log`：本轮节点日志。
- `result.json`：判定结果和失败证据。

## 版本确认

版本号格式：

```text
tdma-YYYYMMDD-HHMMSS-iNNN
```

固件命令：

```text
AT+BUILD?
```

期望响应：

```text
+BUILD:<version>
OK
```

五个节点全部返回本轮版本后，才会进入 TDMA 测试。

## 成功目标

当前目标 `unicast_one_frame_loop` 要求同一个 `packet_num` 在同一 TDMA frame 内完成：

1. 中心 `TDMA_DATA_TX`。
2. R4 `TDMA_DATA_LOCAL`。
3. R4 `TDMA_ACK_TX`。
4. 中心 `TDMA_ACK_RX`。
5. 中心 `TDMA_ACK_MATCH matched=1 result=0`。

ACK 到下一帧才回来会判失败。

当前发送命令：

```text
AT+SEND=981,4,11223344
```

## 串口

运行前关闭 sscom、旧 SerialLog.App、case_runner 等占用 COM 口的程序。

如果 `AtPort` 和 `LogPort` 是同一个 COM，工具会复用已打开的日志串口发送 AT 命令，避免 Windows 串口独占冲突。
