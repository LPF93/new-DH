# DH 数据采集系统

本项目用于设备 SDK 实时采集、通道管理、结果预览和 TDMS 文件落盘。

当前版本特性：

- 仅保留设备 SDK 采集模式，不再包含旧的演示采样链路
- UI 端支持设备初始化、通道选择、实时采样和单文件 TDMS 存储
- 存储目录由用户指定，文件可按自定义名称或采样开始时间命名
- 文件重名时自动按 `_001`、`_002` 递增
- TDMS 文件直接写入目标目录，不再创建多级子目录
- 默认统一使用 UTF-8

## 项目结构

- `src/AcqShell.UI`：桌面界面
- `src/AcqEngine.Host`：采集主机进程
- `src/AcqEngine.DeviceSdk`：设备 SDK 接入
- `src/AcqEngine.Storage.Tdms`：TDMS 存储实现
- `src/AcqEngine.Storage.Hdf5`：HDF5 存储实现
- `src/AcqEngine.Storage.Abstractions`：存储抽象
- `src/AcqEngine.Core`：核心模型与基础设施

## 运行方式

构建：

```powershell
dotnet build .\DH.Acq.sln
```

运行 Host：

```powershell
dotnet run --project .\src\AcqEngine.Host\AcqEngine.Host.csproj
```

运行 UI：

```powershell
dotnet run --project .\src\AcqShell.UI\AcqShell.UI.csproj
```

## 配置说明

Host 默认配置文件位于 `src/AcqEngine.Host/appsettings.json`。

重点配置项：

- `sdk.sdkDirectory`：SDK 根目录
- `sdk.configDirectory`：配置目录
- `sdk.dataCountPerCallback`：每次回调数据量
- `storage.basePath`：默认输出目录
- `storage.primaryFormat`：主存储格式

## 当前约束

- 当前主流程按正式设备 SDK 路径开发
- 单文件 TDMS 是默认落盘方式
- 若本机 `dotnet restore` 环境异常，重新构建前需要先修复 NuGet 回退包目录配置
