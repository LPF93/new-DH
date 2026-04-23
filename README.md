# DH 一期开发骨架

本仓库已基于 architecture_plan.md 完成一期可实施骨架，覆盖以下主链路：

- 双进程工程拆分：AcqShell.UI / AcqEngine.Host
- 采集入口：支持 `模拟回调` 与 `真实SDK回调` 两种模式
- 内存与分发：BlockPool + IngestDispatcher + FrameBus + RecentDataCache
- 写盘链路：StorageOrchestrator + IContainerWriter（TDMS/HDF5 两套实现骨架）
- 处理链路：ProcessingPipeline + PassThrough + BasicStats
- 命名策略：模板化文件名生成与非法字符清洗
- 会话产物：session.manifest.json 基础落盘
- 基础测试：命名、BlockPool、处理流水线

## 解决方案结构

- src/AcqShell.UI
- src/AcqShell.Contracts
- src/AcqEngine.Host
- src/AcqEngine.Core
- src/AcqEngine.DeviceSdk
- src/AcqEngine.Storage.Abstractions
- src/AcqEngine.Storage.Tdms
- src/AcqEngine.Storage.Hdf5
- src/AcqEngine.Processing
- src/AcqEngine.Diagnostics
- src/AcqEngine.Replay
- tests/AcqEngine.Tests

## 快速启动

1. 构建：

```powershell
dotnet build .\DH.Acq.sln
```

2. 运行引擎：

```powershell
dotnet run --project .\src\AcqEngine.Host\AcqEngine.Host.csproj
```

3. 运行 UI：

```powershell
dotnet run --project .\src\AcqShell.UI\AcqShell.UI.csproj
```

4. 执行测试：

```powershell
dotnet test .\DH.Acq.sln --no-build
```

## 当前说明

- 当前存储写入器为可运行骨架，文件扩展名分别为 .tdms / .h5，内部数据帧格式为自定义二进制头 + payload。
- 后续可在不改动采集主链路的情况下替换为真实 NI TDMS 与 HDF5 SDK 调用。
- 运行时目标当前为 net8.0（因本机模板不支持 net6 初始化）；如部署到 Win7 约束环境，建议在安装 net6 SDK/运行时后统一回切 TargetFramework。

## SDK 回调联调流程

1. 先启动模拟仪器程序：

- dh11/模拟仪器程序(运行AutoStartVirtualInstrument.exe)/

2. 在模拟仪器中填写目标 IP（本机 IP）。

3. 修改引擎配置：

- 文件：src/AcqEngine.Host/appsettings.json
- 将 `sdk.mode` 改为 `SDK`（或 `真实`）
- 将 `sdk.sdkDirectory` / `sdk.configDirectory` 指向 SDK 目录（默认示例为 dh11/DHDAS安装包/srcfile）

4. 启动引擎：

```powershell
dotnet run --project .\src\AcqEngine.Host\AcqEngine.Host.csproj
```

5. 观察引擎日志：

- 若回调正常，会持续输出块数、字节数、队列状态。
