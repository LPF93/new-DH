using System.Runtime.InteropServices;
using System.Text;
using AcqEngine.Core;

namespace AcqEngine.DeviceSdk;

public interface IDescriptorAcquisitionSource : IAcquisitionSource
{
    SourceDescriptor Descriptor { get; }
}

public sealed class DhSdkOptions
{
    public string SdkDirectory { get; init; } = string.Empty;
    public string ConfigDirectory { get; init; } = string.Empty;
    public int DataCountPerCallback { get; init; } = 128;
    public bool SingleMachineMode { get; init; } = false;
    public bool AutoConnectDevices { get; init; } = true;
}

public sealed class DhSdkAcquisitionSource : IDescriptorAcquisitionSource
{
    private readonly SourceDescriptor _descriptor;
    private readonly BlockPool _blockPool;
    private readonly DhSdkOptions _options;
    private readonly Dictionary<int, int> _messageTypeLogCounts = new();
    private readonly Dictionary<string, int> _mappedBlockLogCounts = new();
    private readonly object _messageTypeLogLock = new();

    private DhHardwareSdk.SampleDataChangeEventHandle? _sampleDataHandler;
    private CancellationTokenRegistration _tokenRegistration;
    private long _sequence;
    private long _callbackPos;
    private int _started;

    public DhSdkAcquisitionSource(SourceDescriptor descriptor, BlockPool blockPool, DhSdkOptions options)
    {
        _descriptor = descriptor;
        _blockPool = blockPool;
        _options = options;
    }

    public int SourceId => _descriptor.SourceId;

    public SourceDescriptor Descriptor => _descriptor;

    public event Action<DataBlock>? BlockArrived;

    public void Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        var sdkDir = PrepareSdkDirectory();

        var configDir = ResolveConfigDirectory();
        var initResult = DhSdkInitialization.TryInitWithRecovery(configDir);
        Console.WriteLine($"[DhSdk] InitMacControl 返回: {initResult}");

        if (_options.AutoConnectDevices)
        {
            DhSdkInitialization.TryRefindAndConnect();
        }

        var onlineDeviceCount = DhSdkInitialization.TryGetOnlineDeviceCount();
        Console.WriteLine($"[DhSdk] 在线设备探测结果: {onlineDeviceCount}");
        if (initResult < 0 && onlineDeviceCount <= 0)
        {
            Interlocked.Exchange(ref _started, 0);
            throw new InvalidOperationException(
                DhSdkInitialization.BuildInitFailureMessage(configDir, sdkDir, initResult, onlineDeviceCount));
            throw new InvalidOperationException($"SDK 初始化失败，返回值: {initResult}");
        }

        _sampleDataHandler = OnSampleDataReceived;
        var callbackResult = DhHardwareSdk.SetDataChangeCallBackFun(_sampleDataHandler);
        // 与 Demo_C# 一致：该返回值在部分 SDK 版本上并非错误码，不能据此判定失败。
        Console.WriteLine($"[DhSdk] SetDataChangeCallBackFun 返回: {callbackResult}");

        TrySetSingleMachineMode();
        TrySetDataCountEveryTime(Math.Max(1, _options.DataCountPerCallback));
        TryStartSampling();

        if (cancellationToken.CanBeCanceled)
        {
            _tokenRegistration = cancellationToken.Register(Stop);
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _tokenRegistration.Dispose();

        try
        {
            DhHardwareSdk.StopMacSample();
        }
        catch
        {
            // Ignore cleanup errors.
        }

        try
        {
            DhHardwareSdk.QuitMacControl();
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnSampleDataReceived(
        long sampleTime,
        int groupIdSize,
        IntPtr groupInfo,
        int messageType,
        int groupId,
        int channelStyle,
        int channelId,
        int machineId,
        long totalDataCount,
        int dataCountPerChannel,
        int bufferCount,
        int blockIndex,
        long sampleData)
    {
        if (!DhSdkMessageTypes.IsRawWaveformMessage(messageType))
        {
            LogCallbackMessageType(messageType, sampleTime, groupId, machineId, dataCountPerChannel, bufferCount, accepted: false);
            ReleaseBufferSafe(sampleData);
            return;
        }

        if (sampleData == 0 || bufferCount <= 0 || dataCountPerChannel <= 0)
        {
            ReleaseBufferSafe(sampleData);
            return;
        }

        LogCallbackMessageType(messageType, sampleTime, groupId, machineId, dataCountPerChannel, bufferCount, accepted: true);

        var rawBytes = new byte[bufferCount];
        try
        {
            Marshal.Copy((IntPtr)sampleData, rawBytes, 0, rawBytes.Length);
        }
        finally
        {
            ReleaseBufferSafe(sampleData);
        }

        var channelCount = ResolveChannelCount(rawBytes.Length, dataCountPerChannel, _descriptor.ChannelCount);
        var mappedSourceId = ResolveSourceId(machineId, groupId, _descriptor.SourceId);
        LogMappedBlock(machineId, groupId, mappedSourceId, channelCount, dataCountPerChannel, bufferCount, blockIndex, totalDataCount);

        if (totalDataCount + dataCountPerChannel > _callbackPos)
        {
            _callbackPos = totalDataCount + dataCountPerChannel;
        }

        var block = _blockPool.Rent(rawBytes.Length);
        block.CopyFrom(rawBytes);
        block.Header = new DataBlockHeader(
            Guid.Empty,
            mappedSourceId,
            StreamKind.Raw,
            Interlocked.Increment(ref _sequence),
            totalDataCount,
            dataCountPerChannel,
            channelCount,
            SampleType.Float32,
            sampleTime,
            TimestampNs.Now());

        var callback = BlockArrived;
        if (callback is null)
        {
            block.Release();
            return;
        }

        callback.Invoke(block);
    }

    private string PrepareSdkDirectory()
    {
        var resolvedSdkDir = DhSdkPathResolver.ResolveSdkDirectory(_options.SdkDirectory, _options.ConfigDirectory);
        DhSdkPathResolver.ApplySdkDirectory(resolvedSdkDir);
        return resolvedSdkDir;
    }

    private string ResolveConfigDirectory()
    {
        return DhSdkPathResolver.ResolveConfigDirectory(_options.SdkDirectory, _options.ConfigDirectory);
    }

    private void TrySetSingleMachineMode()
    {
        try
        {
            DhHardwareSdk.ChangeGetDataStatus(_options.SingleMachineMode);
        }
        catch (EntryPointNotFoundException)
        {
            // Ignore when old SDK does not expose ChangeGetDataStatus.
        }
    }

    private static int TryInitWithRecovery(string configDir)
    {
        var initResult = DhHardwareSdk.InitMacControl(configDir);
        if (initResult >= 0)
        {
            return initResult;
        }

        try
        {
            DhHardwareSdk.QuitMacControl();
        }
        catch
        {
            // Ignore cleanup failures during recovery.
        }

        return DhHardwareSdk.InitMacControl(configDir);
    }

    private static void TrySetDataCountEveryTime(int count)
    {
        try
        {
            DhHardwareSdk.SetGetDataCountEveryTime(count);
        }
        catch (EntryPointNotFoundException)
        {
            // Some SDK builds do not expose this optional API.
        }
    }

    private static void TryStartSampling()
    {
        try
        {
            DhHardwareSdk.StartMacSample();
        }
        catch (EntryPointNotFoundException)
        {
            // Some SDK builds already stream after simulator starts, no explicit call required.
        }
    }

    private static int ResolveChannelCount(int bufferBytes, int dataCountPerChannel, int fallbackChannelCount)
    {
        var floatCount = bufferBytes / sizeof(float);
        var channelCount = dataCountPerChannel > 0 ? floatCount / dataCountPerChannel : 0;
        if (channelCount <= 0)
        {
            channelCount = Math.Max(1, fallbackChannelCount);
        }

        return channelCount;
    }

    private static int ResolveSourceId(int machineId, int groupId, int fallbackSourceId)
    {
        if (machineId > 0)
        {
            return machineId;
        }

        if (groupId > 0)
        {
            return groupId;
        }

        return fallbackSourceId;
    }

    private void LogCallbackMessageType(
        int messageType,
        long sampleTime,
        int groupId,
        int machineId,
        int dataCountPerChannel,
        int bufferCount,
        bool accepted)
    {
        lock (_messageTypeLogLock)
        {
            _messageTypeLogCounts.TryGetValue(messageType, out var count);
            count++;
            _messageTypeLogCounts[messageType] = count;
            if (count > 3)
            {
                return;
            }

            var state = accepted ? "accepted" : "ignored";
            Console.WriteLine(
                $"[DhSdk] Callback {state}: type={messageType} (0x{messageType:X2}), group={groupId}, machine={machineId}, perChannel={dataCountPerChannel}, bytes={bufferCount}, sampleTime={sampleTime}");
        }
    }

    private void LogMappedBlock(
        int machineId,
        int groupId,
        int mappedSourceId,
        int channelCount,
        int dataCountPerChannel,
        int bufferCount,
        int blockIndex,
        long totalDataCount)
    {
        var key = $"{machineId}|{groupId}|{mappedSourceId}|{channelCount}|{dataCountPerChannel}|{bufferCount}";

        lock (_messageTypeLogLock)
        {
            _mappedBlockLogCounts.TryGetValue(key, out var count);
            count++;
            _mappedBlockLogCounts[key] = count;
            if (count > 3)
            {
                return;
            }

            Console.WriteLine(
                $"[DhSdk] RawBlock mapped: machine={machineId}, group={groupId}, source={mappedSourceId}, channels={channelCount}, samples/ch={dataCountPerChannel}, bytes={bufferCount}, blockIndex={blockIndex}, totalData={totalDataCount}");
        }
    }

    private static void ReleaseBufferSafe(long point)
    {
        if (point == 0)
        {
            return;
        }

        try
        {
            DhHardwareSdk.DA_ReleaseBuffer(point);
        }
        catch
        {
            // Ignore release failures to avoid breaking callback thread.
        }
    }
}

public sealed record DhSdkChannelTopology(int ChannelIndex, int ChannelId, bool Online);

public sealed record DhSdkDeviceTopology(int MachineId, string IpAddress, IReadOnlyList<DhSdkChannelTopology> Channels);

public sealed record DhSdkTopologySnapshot(IReadOnlyList<DhSdkDeviceTopology> Devices, float SampleRateHz);

public static class DhSdkTopologyDiscovery
{
    public static DhSdkTopologySnapshot Discover(string sdkDirectory, string configDirectory)
    {
        var resolvedSdkDirectory = DhSdkPathResolver.ResolveSdkDirectory(sdkDirectory, configDirectory);
        var resolvedConfigDirectory = DhSdkPathResolver.ResolveConfigDirectory(sdkDirectory, configDirectory);
        DhSdkPathResolver.ApplySdkDirectory(resolvedSdkDirectory);

        var initResult = DhSdkInitialization.TryInitWithRecovery(resolvedConfigDirectory);
        DhSdkInitialization.TryRefindAndConnect();
        var onlineDeviceCount = DhSdkInitialization.TryGetOnlineDeviceCount();
        if (initResult < 0 && onlineDeviceCount <= 0)
        {
            throw new InvalidOperationException(
                $"SDK 初始化失败，返回值: {initResult}。配置目录: {resolvedConfigDirectory}，SDK目录: {resolvedSdkDirectory}");
        }

        try
        {
            var deviceCount = Math.Max(0, onlineDeviceCount);
            var devices = new List<DhSdkDeviceTopology>(deviceCount);

            for (var i = 0; i < deviceCount; i++)
            {
                const int ipBufferBytes = 128;
                var ipBuffer = Marshal.AllocHGlobal(ipBufferBytes);

                try
                {
                    var infoResult = DhHardwareSdk.GetMacInfoFromIndex(i, out var machineId, ipBuffer, ipBufferBytes, out _);
                    if (infoResult < 0)
                    {
                        continue;
                    }

                    var ipAddress = Marshal.PtrToStringAnsi(ipBuffer) ?? string.Empty;
                    var channelCount = Math.Max(0, DhHardwareSdk.GetMacCurrentChnCount(machineId, ipAddress));
                    var channels = new List<DhSdkChannelTopology>(channelCount);

                    for (var ch = 0; ch < channelCount; ch++)
                    {
                        var channelResult = DhHardwareSdk.GetChannelIDFromAllChannelIndex(
                            machineId,
                            ipAddress,
                            ch,
                            out var channelId,
                            out var online);

                        if (channelResult < 0 || channelId <= 0)
                        {
                            channelId = ch + 1;
                        }

                        channels.Add(new DhSdkChannelTopology(ch + 1, channelId, online != 0));
                    }

                    devices.Add(new DhSdkDeviceTopology(machineId, ipAddress, channels));
                }
                finally
                {
                    Marshal.FreeHGlobal(ipBuffer);
                }
            }

            var sampleRate = DhHardwareSdk.GetMacCurrentSampleFreq();
            if (sampleRate <= 0)
            {
                sampleRate = 1000f;
            }

            return new DhSdkTopologySnapshot(devices, sampleRate);
        }
        finally
        {
            try
            {
                DhHardwareSdk.QuitMacControl();
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

}

internal static class DhSdkInitialization
{
    public static int TryInitWithRecovery(string configDir)
    {
        var initResult = DhHardwareSdk.InitMacControl(configDir);
        if (initResult >= 0)
        {
            return initResult;
        }

        try
        {
            DhHardwareSdk.QuitMacControl();
        }
        catch
        {
            // Ignore cleanup failures during recovery.
        }

        return DhHardwareSdk.InitMacControl(configDir);
    }

    public static void TryRefindAndConnect()
    {
        try
        {
            DhHardwareSdk.RefindAndConnecMac();
        }
        catch (EntryPointNotFoundException)
        {
            // Optional API on some SDK builds.
        }
        catch
        {
            // Ignore transient probe failures and fall back to count checks.
        }
    }

    public static int TryGetOnlineDeviceCount()
    {
        try
        {
            return DhHardwareSdk.GetAllMacOnlineCount();
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
        catch
        {
            return -1;
        }
    }

    public static string BuildInitFailureMessage(string configDir, string sdkDir, int initResult, int onlineDeviceCount)
    {
        var builder = new StringBuilder();
        builder.Append($"SDK 初始化失败，返回值: {initResult}。在线设备探测结果: {onlineDeviceCount}。");
        builder.Append($"配置目录: {configDir}，SDK目录: {sdkDir}。");

        var diagnostics = DhSdkConfigDiagnostics.Describe(configDir);
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            builder.Append(' ');
            builder.Append(diagnostics);
        }

        return builder.ToString();
    }
}

public static class DhSdkPathResolver
{
    private const string HardwareDllName = "Hardware_Standard_C_Interface.dll";

    public static string ResolveSdkDirectory(string sdkDirectory, string configDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateCandidates(sdkDirectory, configDirectory))
        {
            if (!seen.Add(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            var dllPath = Path.Combine(candidate, HardwareDllName);
            if (File.Exists(dllPath))
            {
                return candidate;
            }
        }

        foreach (var candidate in EnumerateCandidates(sdkDirectory, configDirectory))
        {
            if (!seen.Add(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            return candidate;
        }

        return string.Empty;
    }

    public static string ResolveConfigDirectory(string sdkDirectory, string configDirectory)
    {
        if (TryNormalize(configDirectory, out var normalizedConfig))
        {
            return EnsureTrailingSlash(normalizedConfig);
        }

        if (TryNormalize(sdkDirectory, out var normalizedSdk))
        {
            var sdkParent = Directory.GetParent(normalizedSdk)?.FullName;
            if (sdkParent is not null)
            {
                var inferredConfig = Path.Combine(sdkParent, "config");
                if (Directory.Exists(inferredConfig))
                {
                    return EnsureTrailingSlash(inferredConfig);
                }
            }

            return EnsureTrailingSlash(normalizedSdk);
        }

        return EnsureTrailingSlash(AppContext.BaseDirectory);
    }

    public static void ApplySdkDirectory(string sdkDirectory)
    {
        if (string.IsNullOrWhiteSpace(sdkDirectory))
        {
            return;
        }

        if (!Directory.Exists(sdkDirectory))
        {
            throw new DirectoryNotFoundException($"SDK 目录不存在: {sdkDirectory}");
        }

        var ok = Kernel32.SetDllDirectory(sdkDirectory);
        if (ok)
        {
            return;
        }

        var errorCode = Marshal.GetLastWin32Error();
        throw new InvalidOperationException($"设置 SDK 目录失败，错误码: {errorCode}");
    }

    private static IEnumerable<string> EnumerateCandidates(string sdkDirectory, string configDirectory)
    {
        if (TryNormalize(sdkDirectory, out var sdk))
        {
            yield return sdk;
            yield return Path.Combine(sdk, "srcfile");

            var sdkParent = Directory.GetParent(sdk)?.FullName;
            if (!string.IsNullOrWhiteSpace(sdkParent))
            {
                yield return sdkParent;
                yield return Path.Combine(sdkParent, "srcfile");
                yield return Path.Combine(sdkParent, "bin");
            }
        }

        if (TryNormalize(configDirectory, out var config))
        {
            yield return config;
            yield return Path.Combine(config, "srcfile");

            var configParent = Directory.GetParent(config)?.FullName;
            if (!string.IsNullOrWhiteSpace(configParent))
            {
                yield return configParent;
                yield return Path.Combine(configParent, "srcfile");
                yield return Path.Combine(configParent, "bin");
            }
        }

        yield return AppContext.BaseDirectory;
    }

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        normalized = value.Trim().Trim('"');
        normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.Length > 0;
    }

    private static string EnsureTrailingSlash(string path)
    {
        return path.EndsWith("\\", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal)
            ? path
            : path + "\\";
    }
}

internal static class DhSdkConfigDiagnostics
{
    public static string Describe(string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir))
        {
            return string.Empty;
        }

        if (!Directory.Exists(configDir))
        {
            return $"配置目录不存在: {configDir}";
        }

        var hardwareCfgPath = Path.Combine(configDir, "HardWareCfg.ini");
        var deviceInfoPath = Path.Combine(configDir, "DeviceInfo.ini");
        var builder = new StringBuilder();

        var deviceCount = TryReadIniValue(deviceInfoPath, "DeviceCount");
        if (!string.IsNullOrWhiteSpace(deviceCount))
        {
            builder.Append($"DeviceInfo.ini 中 DeviceCount={deviceCount}。");
        }

        var interfaceType = TryReadIniValue(hardwareCfgPath, "InterfaceType");
        if (!string.IsNullOrWhiteSpace(interfaceType))
        {
            builder.Append($"HardWareCfg.ini 中 InterfaceType={interfaceType}。");
        }

        var ipAddresses = ReadIpAddresses(deviceInfoPath);
        if (interfaceType == "8" && ipAddresses.Any(IsLanAddress))
        {
            builder.Append(
                "检测到 InterfaceType=8(4G)，但 DeviceInfo.ini 使用了 192.168.* / 10.* 这类网口地址；如果当前设备走的是千兆网等以太网链路，建议先改成 7(千兆网) 后重试。");
        }

        return builder.ToString().Trim();
    }

    private static bool IsLanAddress(string value)
    {
        return value.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("10.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.16.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.17.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.18.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.19.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.20.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.21.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.22.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.23.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.24.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.25.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.26.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.27.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.28.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.29.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.30.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("172.31.", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadIniValue(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var currentKey = line[..equalsIndex].Trim();
            if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(equalsIndex + 1)..].Trim();
        }

        return null;
    }

    private static List<string> ReadIpAddresses(string path)
    {
        var values = new List<string>();
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("DeviceIP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var value = line[(equalsIndex + 1)..].Trim();
            if (value.Length > 0)
            {
                values.Add(value);
            }
        }

        return values;
    }
}

internal static class DhSdkMessageTypes
{
    public const int SampleAnalogData = 0;
    public const int SampleSingleGroupAnalogData = 5;
    public const int SampleAnalogMultiFreqChannelData = 20;
    public const int SampleAnalogMultiChannelData = 21;

    public const int SampleAnalogDataV2 = 0x81;
    public const int SampleAnalogMultiChannelDataV2 = 0x82;
    public const int SampleSingleGroupAnalogDataV2 = 0x83;

    public static bool IsRawWaveformMessage(int messageType)
    {
        return messageType == SampleAnalogData
            || messageType == SampleSingleGroupAnalogData
            || messageType == SampleAnalogMultiChannelData
            || messageType == SampleAnalogDataV2
            || messageType == SampleAnalogMultiChannelDataV2
            || messageType == SampleSingleGroupAnalogDataV2;
    }
}

internal static class DhHardwareSdk
{
    private const string LibName = "Hardware_Standard_C_Interface.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate void SampleDataChangeEventHandle(
        long sampleTime,
        int groupIdSize,
        IntPtr groupInfo,
        int messageType,
        int groupId,
        int channelStyle,
        int channelId,
        int machineId,
        long totalDataCount,
        int dataCountPerChannel,
        int bufferCount,
        int blockIndex,
        long sampleData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int InitMacControl(string dllDir);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void QuitMacControl();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int SetDataChangeCallBackFun(SampleDataChangeEventHandle callback);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int DA_ReleaseBuffer(long point);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern bool RefindAndConnecMac();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetAllMacOnlineCount();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetMacInfoFromIndex(int nIndex, out int pMacId, IntPtr strMacIp, int nMacBuffer, out int nUseBuffer);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetMacCurrentChnCount(int nMachineId, [MarshalAs(UnmanagedType.LPStr)] string strMacIp);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetChannelIDFromAllChannelIndex(int nMachineId, [MarshalAs(UnmanagedType.LPStr)] string pMacIp, int nIndex, out int nMacChnId, out int bOnLine);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern float GetMacCurrentSampleFreq();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int SetGetDataCountEveryTime(int count);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void StartMacSample();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void StopMacSample();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ChangeGetDataStatus(bool singleMachine);
}

internal static class Kernel32
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string lpPathName);
}
