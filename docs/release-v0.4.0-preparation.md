# v0.4.0 发布准备

基准提交：`5f68cd6`（v0.3.2）。2026-10-22 完成 **v0.4.0** 本地发布准备；2026-10-23 用户已明确授权提交、推送并发布正式 Release。以下候选及测试结果为提交前记录，正式发布结果以对应标签的 CI 和下载资产为准。

应用版本已设为 `0.4.0`，程序集和文件版本为 `0.4.0.0`；Release 工作流默认版本同步更新。协作协议仍为 3，工作区配置格式为 5。

## 改动分组

| 范围 | 内容 |
|---|---|
| 命令组重构 | 二级设置、复制与跨工作区导入、命名分类与多列表成员、智能排序、紧凑外层列表、双击编辑、并行互斥、顺序队列及配置快照 |
| 发送体验 | 次数 0 只执行一轮、显式无限循环、转义解析、本地发送回显、目标名称刷新、长命令编辑与分类切换布局修复 |
| 配置交换 | 独立命令组 JSON、协作定向投递与确认接收、工作区快照导入新建、身份与目标重映射、来源设备和路径清理 |
| 界面外观 | 集中控件模板、窗口级主题、对比度与状态反馈、自有关于弹窗、稳定布局基线、动画与高对比度适配 |
| 串口稳定性 | 原流与句柄检查、安全轮询恢复、显式释放、订阅者异常隔离、独立接收线程、及时提交日志、有界异步诊断 |
| 剪贴板 | 即时 Unicode 写入、后台占用重试、失败判定与原生资源释放，不再依赖二次 OLE Flush |
| 交付 | README／帮助、功能及诊断文档、回归测试和布局基线、版本元数据、Release 更新说明 |

正式更新说明见 [release-notes.md](../.github/release-notes.md)，精确文件范围见 [release-v0.4.0-files.txt](release-v0.4.0-files.txt)。新增源码、测试及 `tests/SerialLog.Tests/Baselines/appearance-geometry.json` 必须一并提交，不能只处理已跟踪 diff。

## 不纳入提交

- `promo/`：独立宣传内容。
- `tests/SerialLog.Cli.Tests/`：独立 EcoLink 自动化测试，不在本次桌面解决方案测试范围。
- `docs/ecolink-auto-iteration.md`、`docs/ecolink-loop-config.example.json`、`docs/ecolink-ota-auto-test.md`、`docs/ecolink-ota-config.example.json`。
- 忽略目录中的预览、全部 `data`、采集日志、诊断、截图、构建产物、候选 ZIP、测试结果及本地经验文件。

这些文件全部保留原样，不删除、不暂存。不要直接 `git add .`。截图文档明确标注本地 `artifacts` 链接；用于 CI 的布局数值基线单独随测试提交，不依赖这些图片。

## 兼容性与风险边界

1. v0.3.2 的配置格式 3 迁至 5；备份后原子写入。升级保留完整 `data`，降级须恢复旧备份，不能直接读取新版配置。
2. 次数 0 不再隐含无限循环；转义与发送回显默认开启。正式更新说明必须突出提醒，避免沿用旧理解操作设备。
3. 命令组导入不带目标，工作区快照保留逻辑关系但清除 COM 映射。接收确认不等于自动运行，重启也不自动执行。
4. 配置协作需要支持该能力的双方及中转主机；协议 3 不变不表示旧版支持配置传输。接收箱是内存状态，退出清除。
5. 用户已确认首批日志延迟修复有效。系统中 COM 口实际消失是独立问题，不能写成已经修复所有硬件断连。
6. 用户当前反馈未发现其他问题，但不将其等同于所有硬件、DPI、输入法和跨电脑网络场景全部验证。
7. 诊断有总量与期限限制，默认不记录完整命令／固件；异常和路径仍可能含内部信息，外发前检查。

## 验证与候选

- 版本调整前全量 496 项通过、0 失败、0 跳过，TRX：`artifacts/test-results/release-preparation-20261022/release-preparation.trx`。
- v0.4.0 元数据调整后全量 **496 项通过、0 失败、0 跳过**，TRX：`artifacts/test-results/release-preparation-20261022/v0.4.0-preparation.trx`。
- Release 全量构建成功，0 警告、0 错误；`git diff --check` 通过。拟提交清单共 **87 个文件**（源码 52、测试 23、docs 9、README 1、GitHub 配置 2）；范围核验无遗漏，未暂存任何文件。
- 干净候选位于 `artifacts/v0.4.0-preparation-20261022`。应用 EXE、DLL、单文件更新助手均为 `0.4.0.0`；便携 ZIP 内 470 个文件逐项 SHA256 与候选目录一致，无 `data`、`workspace.json` 或日志。
- ZIP：`SerialLog-v0.4.0-win-x64-portable.zip`；大小 103643262 字节；SHA256：`EBA41BD0406C3F8DA740262AABBC0064DEDDA2E803437AD60264FE0725EA4D29`（同目录另存校验文件）。本地未生成 MSIX／证书，留待正式发布 CI。
- 预览目录保持不变，不在本轮覆盖；用户实机配置及日志不进入候选包。

测试命令沿用仓库 CI 的 WPF 串行测试集合策略：

```powershell
dotnet test SerialLog.sln -c Release --no-restore --logger "trx;LogFileName=v0.4.0-preparation.trx" --results-directory artifacts/test-results/release-preparation-20261022 -- xUnit.ParallelizeTestCollections=false
dotnet build SerialLog.sln -c Release --no-restore
```

## 提交与发布流程

1. 再次确认工作区 diff 与精确文件清单，保留上述独立内容；按文件清单明确暂存，不全目录暂存。
2. 建议本批以一个完整功能提交交付，避免拆出依赖未跟踪源码／配置格式的不可构建中间提交。建议信息：`feat: 重构命令组与配置共享，优化界面和串口接收，发布 v0.4.0`。
3. 提交前核对版本与文档、测试和构建结果。提交后核对清单无遗漏，检查远端已有标签／提交状态；不得覆盖已有标签或强推。
4. 创建并推送 `v0.4.0` 标签，使用既有 Release 工作流。等待该标签 CI 测试、便携 ZIP、MSIX、证书及 SHA256 资产全部生成并校验，才确认正式发布成功。
5. 升级验收需核对从旧正式版升级后的实际运行路径、显示版本和重启结果，保留 `data` 并验证没有旧进程／旧快捷方式误启动。此前曾有升级回滚反馈，本轮不宣称完成此实机验证。

本地候选不等于正式 Release，准备完成也不代表已经提交、推送或上线。
