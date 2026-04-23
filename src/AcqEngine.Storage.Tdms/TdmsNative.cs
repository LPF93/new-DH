using System.Runtime.InteropServices;
using System.Text;
using AcqEngine.Core;

namespace AcqEngine.Storage.Tdms;

internal static class TdmsNative
{
    private const string DllName = "nilibddc.dll";
    private static readonly string[] DependencyDlls =
    [
        "xerces-c_3_1_usi.dll",
        "Uds.dll",
        "usiPluginTDM.dll",
        "uspTdms.dll",
        "usiEx.dll",
        "tdms_ebd.dll",
        "dacasr.dll"
    ];

    public static bool IsAvailable { get; }

    public static string? ResolvedDllDirectory { get; }

    public static string AvailabilityMessage { get; }

    static TdmsNative()
    {
        try
        {
            ResolvedDllDirectory = ResolveDllDirectory();
            if (string.IsNullOrWhiteSpace(ResolvedDllDirectory) || !Directory.Exists(ResolvedDllDirectory))
            {
                AvailabilityMessage = "未找到 TDMS 原生库目录。可通过 TDMS_DLL_DIR 环境变量指定 nilibddc.dll 所在目录。";
                return;
            }

            PrepareEnvironment(ResolvedDllDirectory);
            PreloadDependencies(ResolvedDllDirectory);

            var mainLibraryPath = Path.Combine(ResolvedDllDirectory, DllName);
            if (!File.Exists(mainLibraryPath))
            {
                AvailabilityMessage = $"未找到 {DllName}：{mainLibraryPath}";
                return;
            }

            NativeLibrary.Load(mainLibraryPath);
            IsAvailable = true;
            AvailabilityMessage = $"已加载 TDMS 原生库：{mainLibraryPath}";
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AvailabilityMessage = $"TDMS 原生库加载失败：{ex.Message}";
        }
    }

    public static void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(AvailabilityMessage);
        }
    }

    public static DDCDataType MapSampleType(SampleType sampleType)
    {
        return sampleType switch
        {
            SampleType.Int16 => DDCDataType.Int16,
            SampleType.Int32 => DDCDataType.Int32,
            SampleType.Float32 => DDCDataType.Float,
            SampleType.Float64 => DDCDataType.Double,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleType), sampleType, "不支持的样本类型。")
        };
    }

    public static string DescribeError(int errorCode)
    {
        try
        {
            var buffer = new StringBuilder(512);
            var result = DDC_GetLibraryErrorDescription(errorCode, buffer, buffer.Capacity);
            if (result == 0 && buffer.Length > 0)
            {
                return buffer.ToString();
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string? ResolveDllDirectory()
    {
        var env = Environment.GetEnvironmentVariable("TDMS_DLL_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        var repoRoot = FindRepoRoot();
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var candidates = new[]
            {
                Path.Combine(repoRoot, "dh11", "DH-example", "tdms", "TDM C DLL[官方源文件]", "dev", "bin", "64-bit"),
                Path.Combine(repoRoot, "tdms", "TDM C DLL[官方源文件]", "dev", "bin", "64-bit")
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var appDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(appDir, DllName)) ? appDir : null;
    }

    private static void PrepareEnvironment(string dllDirectory)
    {
        try
        {
            SetDllDirectory(dllDirectory);
        }
        catch
        {
        }

        try
        {
            AddDllDirectory(dllDirectory);
        }
        catch
        {
        }

        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!path.Contains(dllDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", dllDirectory + Path.PathSeparator + path);
            }
        }
        catch
        {
        }

        var dataModelsDirectory = Path.Combine(dllDirectory, "DataModels");
        if (!Directory.Exists(dataModelsDirectory))
        {
            return;
        }

        try
        {
            Environment.SetEnvironmentVariable("USI_PLUGINSPATH", dllDirectory);
            Environment.SetEnvironmentVariable("USI_RESOURCEDIR", dataModelsDirectory);

            var coreDirectory = Path.Combine(dataModelsDirectory, "USI");
            if (Directory.Exists(coreDirectory))
            {
                Environment.SetEnvironmentVariable("USICORERESOURCEDIR", coreDirectory);
            }
        }
        catch
        {
        }
    }

    private static void PreloadDependencies(string dllDirectory)
    {
        foreach (var dependency in DependencyDlls)
        {
            var path = Path.Combine(dllDirectory, dependency);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                NativeLibrary.Load(path);
            }
            catch
            {
            }
        }
    }

    private static string? FindRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var solutionPath = Path.Combine(dir.FullName, "DH.Acq.sln");
                if (File.Exists(solutionPath))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    public enum DDCDataType
    {
        UInt8 = 5,
        Int16 = 2,
        Int32 = 3,
        Float = 9,
        Double = 10,
        String = 23,
        Timestamp = 30
    }

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_CreateFile(
        string filePath,
        string fileType,
        string name,
        string description,
        string title,
        string author,
        ref IntPtr file);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_CloseFile(IntPtr file);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_SaveFile(IntPtr file);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AddChannelGroup(
        IntPtr file,
        string groupName,
        string groupDescription,
        ref IntPtr group);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AddChannel(
        IntPtr group,
        DDCDataType dataType,
        string name,
        string description,
        string unitString,
        ref IntPtr channel);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AppendDataValuesInt16(
        IntPtr channel,
        short[] values,
        uint count);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AppendDataValuesInt32(
        IntPtr channel,
        int[] values,
        uint count);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AppendDataValuesFloat(
        IntPtr channel,
        float[] values,
        uint count);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AppendDataValuesDouble(
        IntPtr channel,
        double[] values,
        uint count);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_CreateFilePropertyString(IntPtr file, string property, string value);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_CreateChannelPropertyString(IntPtr channel, string property, string value);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_CreateChannelPropertyDouble(IntPtr channel, string property, double value);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    private static extern int DDC_GetLibraryErrorDescription(int errorCode, StringBuilder buffer, int bufferLength);
}
