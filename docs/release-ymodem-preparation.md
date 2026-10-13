# v0.3.2 Ymodem 发布准备

本次在 v0.3.1 基础上新增通用固件烧录及可配置设备规则，并包含波特率选择、命令组编辑和命令区布局修正。用户已授权提交并发布 v0.3.2；应用版本为 0.3.2，文件及程序集版本为 0.3.2.0，协作协议仍为 3，工作区配置格式为 3。

## 发布范围

- 新增 Ymodem 固件烧录，支持标准模式和自定义模式。复用左侧目标窗口勾选，并行向当前工作区的多个本地串口发送同一个 `.bin` 文件，各目标独立显示进度、速度、阶段和结果，可分别取消。
- 自定义模式支持多个命名规则方案、可缺省的前置和后置步骤、帧模板、响应变量以及 JSON 导入和导出。内置可编辑的 `UCCHIP-小网关` 示例；标准模式不执行规则，并隐藏规则方案和规则设置入口。
- 烧录复用已经打开的串口，通过独占二进制通道隔离日志和普通发送。烧录期间拦截普通命令及协作远程命令，暂停目标自动重连；其他窗口继续运行。隐藏、浮动命令区或切换工作区不会中断任务。
- 修复通过下拉选择波特率时焦点被日志窗口抢走、选择未生效的问题，保留手动输入。
- 命令组内已有命令支持右键编辑、双击编辑、右键复制和 `Ctrl+C` 复制完整内容。重复命令按选中位置编辑或删除，复制在后台 STA 线程执行并报告失败。
- 命令区左右比例调整为 1/3 与 2/3，停靠及浮动命令区增加隐藏入口。统一规则编辑器删除按钮尺寸，步骤操作按钮保持一排。

## 配置兼容性与使用限制

- 配置仍保存在工具目录的 `data\workspace.json`。工作区配置格式由 2 升级为 3，迁移先备份，再原子写入；旧 `Gateway` 模式迁为自定义模式及示例规则，原串口窗口和命令设置保留。
- 旧版本不能直接读取新配置格式。手动降级前需备份完整 `data`，并恢复迁移前的配置备份，不能把格式 3 的文件直接交给 v0.3.1。
- 规则、文件路径、选中方案和目标勾选按工作区保存。重启不自动开始烧录；规则变量不保存，正在运行的任务使用开始时的配置快照。
- 仅支持本地串口、同一批次同一个二进制文件，不支持远程窗口烧录或为不同窗口分配不同文件。标准模式需用户先让设备进入接收状态。
- 文件传输完成、后置规则匹配成功不等于固件版本校验成功。小网关示例通过启动标志确认重启，不额外发送重启命令。
- 便携升级和预览覆盖必须保留完整 `data`。正式包不能混入配置、日志或测试产物，必须包含单文件更新助手。

## 提交范围

以下清单为本次提交范围，包含版本与发布说明、功能实现、配置迁移及回归测试。不能只提交 `git diff` 中的文件而遗漏未跟踪实现。

已跟踪修改：

- `.github/release-notes.md`
- `.github/workflows/release.yml`
- `README.md`
- `docs/test-workspaces.md`
- `src/SerialLog.App/FloatingCommandWindow.xaml`
- `src/SerialLog.App/MainWindow.xaml`
- `src/SerialLog.App/MainWindow.xaml.cs`
- `src/SerialLog.App/SerialLog.App.csproj`
- `src/SerialLog.App/ViewModels/CommandPanelViewModel.cs`
- `src/SerialLog.App/ViewModels/MainViewModel.cs`
- `src/SerialLog.App/ViewModels/SerialWindowViewModel.cs`
- `src/SerialLog.App/ViewModels/TestWorkspaceManager.cs`
- `src/SerialLog.App/Views/CommandPanelView.xaml`
- `src/SerialLog.App/Views/CommandPanelView.xaml.cs`
- `src/SerialLog.Core/Configuration/WorkspaceCatalog.cs`
- `src/SerialLog.Core/Configuration/WorkspaceConfig.cs`
- `src/SerialLog.Core/Configuration/WorkspaceConfigStore.cs`
- `src/SerialLog.Core/Serial/SerialPortSession.cs`
- `tests/SerialLog.Tests/WorkspaceCollaborationTests.cs`

相关新增文件：

- `docs/release-ymodem-preparation.md`
- `docs/ymodem-firmware.md`
- `src/SerialLog.App/ViewModels/YmodemViewModel.cs`
- `src/SerialLog.App/Views/YmodemRulesWindow.cs`
- `src/SerialLog.Core/Firmware/SerialFrameCodec.cs`
- `src/SerialLog.Core/Firmware/YmodemRuleExecutor.cs`
- `src/SerialLog.Core/Firmware/YmodemRules.cs`
- `src/SerialLog.Core/Firmware/YmodemSender.cs`
- `src/SerialLog.Core/Serial/IBinarySerialTransport.cs`
- `src/SerialLog.Core/Serial/SerialBinaryLease.cs`
- `tests/SerialLog.Tests/ComboBoxFocusTests.cs`
- `tests/SerialLog.Tests/CommandGroupEditingTests.cs`
- `tests/SerialLog.Tests/CommandGroupMenuTests.cs`
- `tests/SerialLog.Tests/SerialBinaryLeaseTests.cs`
- `tests/SerialLog.Tests/YmodemMigrationTests.cs`
- `tests/SerialLog.Tests/YmodemPanelTests.cs`
- `tests/SerialLog.Tests/YmodemParallelTests.cs`
- `tests/SerialLog.Tests/YmodemRulesTests.cs`
- `tests/SerialLog.Tests/YmodemSenderTests.cs`
- `tests/SerialLog.Tests/YmodemWorkspaceTests.cs`

既有独立内容不纳入本次提交，保留原样：

- `promo/`
- `tests/SerialLog.Cli.Tests/`
- `docs/ecolink-auto-iteration.md`
- `docs/ecolink-loop-config.example.json`
- `docs/ecolink-ota-auto-test.md`
- `docs/ecolink-ota-config.example.json`
- 预览 `data`、`artifacts/` 和本地 `.learnings/`

## 验证结果

2026-10-13 Release 全量构建成功，0 警告、0 错误。全量测试共 359 项，本次新增测试 71 项；全量回归连续 3 轮全部通过，协作测试连续 20 轮全部通过。正式版本结果位于 `artifacts/test-results/v0.3.2`，全量结果为 `release-1.trx` 至 `release-3.trx`。

首次发布前全量测试为 357 项通过、1 项失败：`WorkspaceCollaborationTests.Same_computer_multiple_workspaces_receive_only_subscribed_live_logs` 在订阅撤销并迅速恢复的场景收到了名为 `unsubscribed` 的日志。失败证据保存在 `artifacts/test-results/release-preparation-20261013/release-preparation.trx`。

该测试单独连续复测 5 次均通过，第二次全量复测 358 项全部通过，结果为同目录下的 `collaboration-repeat-1.trx` 至 `collaboration-repeat-5.trx` 和 `release-preparation-repeat.trx`。涉及的协作服务和该测试文件相对 HEAD 无本地修改。

测试已补上源端同连接处理确认：在开始订阅和恢复订阅前，等待源端之前的日志及共享快照被主机处理；同时新增确认屏障回归，验证 ACK 返回前日志及快照确已处理。保留原有隔离断言，并补充不接收首次订阅前日志的断言，不使用固定延时，也不放宽断言。协作服务和协议不变，按主机处理时刻的共享／订阅状态分发，不引入按来源时间回溯过滤的语义。

干净候选目录为 `artifacts/v0.3.2-candidate`，包含应用与单文件更新助手；应用 EXE、DLL 和更新助手均已核验文件版本为 0.3.2.0，不包含 `data`、配置或日志。当前实机预览和正式安装目录不在此次发布覆盖范围内。

当前预览已包含全部功能修改，用户反馈暂未发现其他问题。既有小网关单目标烧录有实机成功记录；不同设备的标准模式、规则方案、并行取消和异常恢复不能仅由该记录推定已完成全部实机覆盖。

## 提交与发布安排

本次按 v0.3.2 集中提交上述实现、文档和测试，既有独立文件不纳入。正式发布使用仓库原有 Release 工作流。

1. 应用版本及工作流默认版本更新为 0.3.2，正式更新说明保存在 `.github/release-notes.md`。
2. 已完成 Release 全量测试、构建及配置迁移回归，干净候选已核验程序版本、更新助手和用户数据隔离。
3. 按用户授权，仅提交本次清单中的文件，创建 v0.3.2 标签并推送，不强制覆盖已有标签或远端提交。
4. 使用既有 GitHub Release 工作流构建便携 ZIP、MSIX、证书及校验文件，核验工作流和正式下载资产。此次准备不覆盖现有预览或正式安装目录。
