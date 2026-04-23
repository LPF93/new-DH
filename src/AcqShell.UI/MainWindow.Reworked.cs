using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AcqEngine.Core;
using AcqEngine.DeviceSdk;
using AcqEngine.Storage.Abstractions;
using AcqEngine.Storage.Tdms;
using AcqShell.Contracts;

namespace AcqShell.UI;

public partial class MainWindow : Window
{
    private const double DefaultSampleRateHz = 1000d;
    private const double DefaultWaveWindowSeconds = 2d;
    private const double MaxWaveWindowSeconds = 10d;
    private const int MaxRenderedWavePoints = 1400;

    private readonly object _topologyLock = new();
    private readonly ObservableCollection<DeviceItem> _devices = new();
    private readonly ObservableCollection<ChannelItem> _visibleChannels = new();
    private readonly Dictionary<int, DeviceItem> _devicesByMachineId = new();
    private readonly Dictionary<long, ChannelItem> _channelsByCompositeId = new();
    private readonly ObservableCollection<ResultViewItem> _resultViews = new();
    private readonly ObservableCollection<StorageSegmentItem> _storageSegments = new();
    private readonly Dictionary<long, Queue<double>> _channelWaveBuffers = new();
    private readonly List<IDescriptorAcquisitionSource> _validationSources = new();
    private readonly ConcurrentDictionary<int, long> _storageBlocksBySource = new();
    private StorageOrchestrator? _storageOrchestrator;
    private SessionContext? _storageSession;
    private string _storageManifestPath = string.Empty;
    private bool _useNativeMode = true;
    private bool _sdkInitialized;
    private int _selectedMachineId = -1;
    private double _detectedSampleRateHz = DefaultSampleRateHz;
    private double _waveDisplayWindowSeconds = DefaultWaveWindowSeconds;
    private int _waveBufferCapacity = 4096;
    private long _callbackBlocks;
    private long _callbackBytes;
    private long _lastCallbackUnixMs;
    private long _storageEnqueuedBlocks;
    private long _storageEnqueuedBytes;
    private long _storageEnqueueFailures;
    private int _nextResultViewNumber = 1;

    public MainWindow()
    {
        InitializeComponent();
        BindCollections();
        InitializeResultViews();
        InitializeStorageState();
        UpdateModeState();
        UpdateWaveBufferCapacity();
        InitializeCounters();
        UpdateStorageSummary();
        UpdateTopologySummary();
        RenderResultViews();

        StartValidationBtn.IsEnabled = false;
        StopValidationBtn.IsEnabled = false;
        UpdateStorageButtons();

        AppendLog("界面已就绪，等待初始化。");
    }

    private static long CreateCompositeId(int machineId, int channelNo)
    {
        return ((long)machineId << 32) | (uint)channelNo;
    }

    private void BindCollections()
    {
        DeviceListItemsControl.ItemsSource = _devices;
        ChannelListBox.ItemsSource = _visibleChannels;
        StorageSegmentsListBox.ItemsSource = _storageSegments;
    }

    private void InitializeStorageState()
    {
        StorageDirectoryTextBox.Text = Path.Combine(Environment.CurrentDirectory, "data");
        StorageTaskNameTextBox.Text = "模拟采集";
        StorageOperatorTextBox.Text = Environment.UserName;
        StorageBatchNoTextBox.Text = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        StorageSegmentSecondsTextBox.Text = "30";
        AutoStartStorageCheckBox.IsChecked = true;
        StorageFormatText.Text = "TDMS / Float32 / 原始流";
        StorageStatusText.Text = "未开始存储";
        StorageSessionIdText.Text = "--";
        StorageSessionPathText.Text = "--";
        StorageManifestPathText.Text = "--";
        StorageCurrentSegmentText.Text = "--";
        StorageStartButton.IsEnabled = false;
        StorageStopButton.IsEnabled = false;
    }

    private void OnDataSourceModeChanged(object? sender, RoutedEventArgs e)
    {
        UpdateModeState();
    }

    private void UpdateModeState()
    {
        _useNativeMode = NativeModeRadio.IsChecked == true;
        NativeConfigPanel.IsVisible = _useNativeMode;
        MockConfigPanel.IsVisible = !_useNativeMode;
        InitializeSdkButton.Content = _useNativeMode ? "初始化 SDK" : "构建拓扑";
        StartValidationBtn.Content = _useNativeMode ? "启动采样" : "启动模拟";
        TopologyStatusText.Text = _useNativeMode
            ? "请选择配置目录后再初始化 SDK。"
            : "请设置模拟设备数和通道数后构建拓扑。";
    }

    private async void OnBrowseConfigDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            AppendLog("文件夹选择器不可用，请手动输入路径。");
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择配置目录",
                AllowMultiple = false
            });
        }
        catch (Exception ex)
        {
            AppendLog($"打开文件夹选择器失败：{ex.Message}");
            return;
        }

        if (folders.Count == 0)
        {
            return;
        }

        ConfigDirectoryTextBox.Text = folders[0].Path.LocalPath;
    }

    private async void OnBrowseStorageDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            AppendLog("文件夹选择器不可用，请手动输入存储目录。");
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择数据存储目录",
                AllowMultiple = false
            });
        }
        catch (Exception ex)
        {
            AppendLog($"打开存储目录选择器失败：{ex.Message}");
            return;
        }

        if (folders.Count == 0)
        {
            return;
        }

        StorageDirectoryTextBox.Text = folders[0].Path.LocalPath;
    }

    private async void OnStartStorageClick(object? sender, RoutedEventArgs e)
    {
        if (_validationSources.Count == 0)
        {
            AppendLog("请先启动采样，再开始实时存储。");
            return;
        }

        var enabledChannels = _channelsByCompositeId.Values
            .Where(static channel => channel.IsEnabled)
            .OrderBy(static channel => channel.CompositeId)
            .ToList();

        try
        {
            await StartStorageSessionAsync(enabledChannels);
        }
        catch (Exception ex)
        {
            StorageStatusText.Text = "存储启动失败";
            UpdateStorageButtons();
            AppendLog($"启动 TDMS 存储失败：{ex.Message}");
        }
    }

    private async void OnStopStorageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await StopStorageSessionAsync();
        }
        catch (Exception ex)
        {
            StorageStatusText.Text = "存储停止失败";
            AppendLog($"停止 TDMS 存储失败：{ex.Message}");
        }
    }

    private void OnInitializeSdkClick(object? sender, RoutedEventArgs e)
    {
        if (_validationSources.Count > 0)
        {
            AppendLog("当前仍在采样，请先停止后再重新初始化。");
            return;
        }

        try
        {
            ClearTopology();

            if (_useNativeMode)
            {
                InitializeNativeTopology();
            }
            else
            {
                InitializeMockTopology();
            }

            _sdkInitialized = _channelsByCompositeId.Count > 0;
            StartValidationBtn.IsEnabled = _sdkInitialized;
            StopValidationBtn.IsEnabled = false;
            StateBadgeText.Text = _sdkInitialized ? "灏辩华" : "寰呮満";

            UpdateTopologySummary();
            UpdateStorageButtons();
            UpdateStorageSummary();
            RenderResultViews();
        }
        catch (Exception ex)
        {
            _sdkInitialized = false;
            StartValidationBtn.IsEnabled = false;
            StateBadgeText.Text = "错误";
            SdkStatusText.Text = "初始化失败";
            TopologyStatusText.Text = "初始化失败";
            AppendLog($"初始化失败：{ex.Message}");
        }
    }

    private void InitializeNativeTopology()
    {
        var configDirectory = (ConfigDirectoryTextBox.Text ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new InvalidOperationException("请先选择配置目录。");
        }

        if (!Directory.Exists(configDirectory))
        {
            throw new DirectoryNotFoundException($"配置目录不存在：{configDirectory}");
        }

        var resolvedConfigDirectory = DhSdkPathResolver.ResolveConfigDirectory(string.Empty, configDirectory);
        ConfigDirectoryTextBox.Text = resolvedConfigDirectory.TrimEnd('\\', '/');

        var resolvedSdkDirectory = DhSdkPathResolver.ResolveSdkDirectory(string.Empty, configDirectory);
        if (!string.IsNullOrWhiteSpace(resolvedSdkDirectory))
        {
            AppendLog($"已解析 SDK 目录：{resolvedSdkDirectory}");
        }

        var topology = DhSdkTopologyDiscovery.Discover(string.Empty, configDirectory);
        ApplyNativeTopology(topology);

        _detectedSampleRateHz = topology.SampleRateHz > 0 ? topology.SampleRateHz : DefaultSampleRateHz;
        UpdateWaveBufferCapacity();

        TopologyStatusText.Text = $"已检测到 {_devices.Count} 台设备、{_channelsByCompositeId.Count} 个通道，采样率 {_detectedSampleRateHz:F0} Hz。";
        SdkStatusText.Text = "SDK 已初始化";
        AppendLog(TopologyStatusText.Text);
    }

    private void InitializeMockTopology()
    {
        var deviceCount = ParsePositiveInt(MockDeviceCountTextBox.Text, 1, 1, 64);
        var channelCount = ParsePositiveInt(MockChannelCountTextBox.Text, 16, 1, 256);

        BuildMockTopology(deviceCount, channelCount);
        _detectedSampleRateHz = 5000d;
        UpdateWaveBufferCapacity();

        TopologyStatusText.Text = $"已构建 {deviceCount} 台模拟设备，共 {_channelsByCompositeId.Count} 个通道。";
        SdkStatusText.Text = "模拟拓扑已就绪";
        AppendLog(TopologyStatusText.Text);
    }

    private static int ParsePositiveInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private void UpdateWaveBufferCapacity()
    {
        var desired = (int)Math.Ceiling(_detectedSampleRateHz * MaxWaveWindowSeconds * 1.1d);
        _waveBufferCapacity = Math.Clamp(desired, 4096, 300000);
    }

    private bool ShouldAutoStartStorage()
    {
        return AutoStartStorageCheckBox.IsChecked == true;
    }

    private bool IsStorageRunning()
    {
        return _storageOrchestrator is not null;
    }

    private static int ParseSegmentSeconds(string? text)
    {
        if (!int.TryParse(text, out var seconds))
        {
            return 30;
        }

        return Math.Clamp(seconds, 1, 3600);
    }

    private string ResolveStorageDirectory()
    {
        var directory = (StorageDirectoryTextBox.Text ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("请先填写数据存储目录。");
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private IReadOnlyList<SourceDescriptor> BuildStorageSourceDescriptors(IReadOnlyList<ChannelItem> enabledChannels)
    {
        var activeMachineIds = enabledChannels
            .Select(static channel => channel.MachineId)
            .Distinct()
            .OrderBy(static machineId => machineId);

        var descriptors = new List<SourceDescriptor>();
        foreach (var machineId in activeMachineIds)
        {
            var channelCount = _devicesByMachineId.TryGetValue(machineId, out var device)
                ? Math.Max(1, device.Channels.Count)
                : Math.Max(1, enabledChannels.Count(channel => channel.MachineId == machineId));

            descriptors.Add(new SourceDescriptor
            {
                SourceId = machineId,
                DeviceName = _devicesByMachineId.TryGetValue(machineId, out var deviceItem)
                    ? deviceItem.DisplayName
                    : $"AI{machineId}",
                ChannelCount = channelCount,
                SampleRateHz = _detectedSampleRateHz,
                SampleType = SampleType.Float32
            });
        }

        return descriptors;
    }

    private SessionContext BuildStorageSession(IReadOnlyList<ChannelItem> enabledChannels)
    {
        var taskName = (StorageTaskNameTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(taskName))
        {
            taskName = "模拟采集";
            StorageTaskNameTextBox.Text = taskName;
        }

        return new SessionContext
        {
            SessionId = Guid.NewGuid(),
            TaskName = taskName,
            OperatorName = (StorageOperatorTextBox.Text ?? string.Empty).Trim(),
            BatchNo = (StorageBatchNoTextBox.Text ?? string.Empty).Trim(),
            StartTime = DateTimeOffset.UtcNow,
            StorageFormat = StorageFormat.Tdms,
            WriteRaw = true,
            WriteProcessed = false,
            Sources = BuildStorageSourceDescriptors(enabledChannels)
        };
    }

    private async Task StartStorageSessionAsync(IReadOnlyList<ChannelItem> enabledChannels, CancellationToken cancellationToken = default)
    {
        if (IsStorageRunning())
        {
            return;
        }

        if (enabledChannels.Count == 0)
        {
            throw new InvalidOperationException("没有可用于存储的已启用通道。");
        }

        var storageDirectory = ResolveStorageDirectory();
        var session = BuildStorageSession(enabledChannels);
        var namingPolicy = new NamingTemplateFileNamingPolicy(storageDirectory, session.FileNamingTemplate);
        var writer = new TdmsWriter(namingPolicy);
        var orchestrator = new StorageOrchestrator(writer, null, new SegmentPolicy(ParseSegmentSeconds(StorageSegmentSecondsTextBox.Text)));

        try
        {
            await orchestrator.StartAsync(session, cancellationToken);
        }
        catch
        {
            await orchestrator.DisposeAsync();
            throw;
        }

        _storageOrchestrator = orchestrator;
        _storageSession = session;
        _storageManifestPath = string.Empty;
        _storageSegments.Clear();
        Interlocked.Exchange(ref _storageEnqueuedBlocks, 0);
        Interlocked.Exchange(ref _storageEnqueuedBytes, 0);
        Interlocked.Exchange(ref _storageEnqueueFailures, 0);
        _storageBlocksBySource.Clear();

        StorageStatusText.Text = "实时存储中";
        StorageSessionIdText.Text = session.SessionId.ToString("N");
        StorageSessionPathText.Text = namingPolicy.BuildSessionDirectory(session);
        StorageManifestPathText.Text = "--";
        StorageCurrentSegmentText.Text = "原始段 #0001";
        UpdateStorageSummary();
        UpdateStorageButtons();
        AppendLog($"已开始 TDMS 实时存储：{StorageSessionPathText.Text}");
    }

    private async Task StopStorageSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_storageOrchestrator is null)
        {
            UpdateStorageButtons();
            return;
        }

        var orchestrator = _storageOrchestrator;
        var session = _storageSession;
        _storageOrchestrator = null;
        _storageSession = null;

        try
        {
            await orchestrator.StopAsync(cancellationToken);
        }
        finally
        {
            await orchestrator.DisposeAsync();
        }

        var segments = orchestrator.GetCompletedSegments();
        var manifestPath = string.Empty;

        if (session is not null)
        {
            manifestPath = WriteStorageManifest(session, segments);
        }

        StorageStatusText.Text = segments.Count > 0 ? "存储已完成" : "未写入任何 TDMS 段";
        UpdateStorageSummary();
        UpdateStorageButtons();

        if (segments.Count > 0)
        {
            AppendLog($"TDMS 存储已停止，共写入 {segments.Count} 个段。");
        }
        else
        {
            AppendLog("TDMS 存储已停止，但没有生成段文件。");
        }
    }

    private void RefreshStorageSegments(IReadOnlyList<WrittenSegmentInfo> segments)
    {
        _storageSegments.Clear();

        foreach (var segment in segments.OrderBy(static item => item.SegmentNo))
        {
            _storageSegments.Add(new StorageSegmentItem
            {
                Title = $"{segment.ContainerFormat} / {ResolveStreamLabel(segment.StreamKind)} / 段 {segment.SegmentNo:D4}",
                Subtitle = $"块 {segment.BlockCount:N0} · 数据 {segment.PayloadBytes:N0} 字节 · 文件 {segment.FileBytes:N0} 字节",
                Path = segment.FilePath
            });
        }
    }

    private void UpdateStorageSummary()
    {
        var blockCount = Interlocked.Read(ref _storageEnqueuedBlocks);
        var byteCount = Interlocked.Read(ref _storageEnqueuedBytes);
        var failureCount = Interlocked.Read(ref _storageEnqueueFailures);

        StorageBlockCountText.Text = blockCount.ToString("N0", CultureInfo.InvariantCulture);
        StorageByteCountText.Text = $"{byteCount:N0} 字节";
        StorageSegmentCountText.Text = _storageSegments.Count.ToString(CultureInfo.InvariantCulture);
        StorageFailureCountText.Text = failureCount.ToString("N0", CultureInfo.InvariantCulture);

        if (IsStorageRunning())
        {
            StorageCurrentSegmentText.Text = $"原始段 #{_storageSegments.Count + 1:D4}";
        }
    }

    private void UpdateStorageButtons()
    {
        var acquisitionRunning = _validationSources.Count > 0;
        var storageRunning = IsStorageRunning();

        StorageStartButton.IsEnabled = acquisitionRunning && !storageRunning;
        StorageStopButton.IsEnabled = storageRunning;
    }

    private static string ResolveStreamLabel(StreamKind streamKind)
    {
        return streamKind switch
        {
            StreamKind.Raw => "原始",
            StreamKind.Processed => "处理后",
            StreamKind.Event => "事件",
            StreamKind.State => "状态",
            _ => streamKind.ToString()
        };
    }

    private string WriteStorageManifest(SessionContext session, IReadOnlyList<WrittenSegmentInfo> segments)
    {
        try
        {
            var sessionDirectory = StorageSessionPathText.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sessionDirectory))
            {
                return string.Empty;
            }

            Directory.CreateDirectory(sessionDirectory);

            var manifest = new SessionManifestDto(
                session.SessionId,
                session.TaskName,
                session.OperatorName,
                session.BatchNo,
                session.StartTime,
                DateTimeOffset.UtcNow,
                session.StorageFormat.ToString(),
                session.WriteRaw,
                session.WriteProcessed,
                session.Sources
                    .Select(static source => new SessionManifestSourceDto(
                        source.SourceId,
                        source.DeviceName,
                        source.ChannelCount,
                        source.SampleRateHz,
                        source.SampleType.ToString()))
                    .ToArray(),
                segments
                    .Select(segment => new SessionSegmentDto(
                        segment.ContainerFormat,
                        segment.StreamKind.ToString(),
                        segment.SegmentNo,
                        Path.GetRelativePath(sessionDirectory, segment.FilePath),
                        segment.StartedAt,
                        segment.EndedAt,
                        segment.BlockCount,
                        segment.PayloadBytes,
                        segment.FileBytes))
                    .ToArray(),
                new SessionMetricsDto(
                    Interlocked.Read(ref _storageEnqueuedBlocks),
                    Interlocked.Read(ref _storageEnqueuedBytes),
                    Interlocked.Read(ref _storageEnqueueFailures),
                    0,
                    _storageBlocksBySource.ToDictionary(static pair => pair.Key, static pair => pair.Value)));

            var manifestPath = Path.Combine(sessionDirectory, "session.manifest.json");
            var json = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(manifestPath, json);
            return manifestPath;
        }
        catch (Exception ex)
        {
            AppendLog($"写入会话清单失败：{ex.Message}");
            return string.Empty;
        }
    }

    private void ApplyNativeTopology(DhSdkTopologySnapshot topology)
    {
        lock (_topologyLock)
        {
            foreach (var device in topology.Devices.OrderBy(static item => item.MachineId))
            {
                var uiDevice = new DeviceItem(device.MachineId, device.IpAddress)
                {
                    IsOnline = true
                };

                foreach (var channel in device.Channels.OrderBy(static item => item.ChannelIndex))
                {
                    var uiChannel = new ChannelItem(device.MachineId, channel.ChannelIndex, channel.ChannelId, $"閫氶亾缂栧彿 {channel.ChannelId}")
                    {
                        IsEnabled = true,
                        IsOnline = channel.Online
                    };

                    uiDevice.Channels.Add(uiChannel);
                    _channelsByCompositeId[uiChannel.CompositeId] = uiChannel;
                }

                uiDevice.NotifyChannelSummaryChanged();
                _devices.Add(uiDevice);
                _devicesByMachineId[uiDevice.MachineId] = uiDevice;
            }
        }

        if (_devices.Count > 0)
        {
            SelectDevice(_devices[0].MachineId);
        }
    }

    private void BuildMockTopology(int deviceCount, int channelsPerDevice)
    {
        lock (_topologyLock)
        {
            for (var machineId = 1; machineId <= deviceCount; machineId++)
            {
                var device = new DeviceItem(machineId, "127.0.0.1")
                {
                    IsOnline = true
                };

                for (var channelNo = 1; channelNo <= channelsPerDevice; channelNo++)
                {
                    var channel = new ChannelItem(machineId, channelNo, channelNo, "妯℃嫙閫氶亾")
                    {
                        IsEnabled = true,
                        IsOnline = true
                    };

                    device.Channels.Add(channel);
                    _channelsByCompositeId[channel.CompositeId] = channel;
                }

                device.NotifyChannelSummaryChanged();
                _devices.Add(device);
                _devicesByMachineId[machineId] = device;
            }
        }

        if (_devices.Count > 0)
        {
            SelectDevice(_devices[0].MachineId);
        }
    }

    private void OnDeviceCardClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var machineId = button.Tag switch
        {
            int value => value,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => -1
        };

        if (machineId > 0)
        {
            SelectDevice(machineId);
        }
    }

    private void SelectDevice(int machineId)
    {
        _selectedMachineId = machineId;
        _visibleChannels.Clear();

        foreach (var device in _devices)
        {
            device.IsSelected = device.MachineId == machineId;
        }

        if (_devicesByMachineId.TryGetValue(machineId, out var deviceItem))
        {
            foreach (var channel in deviceItem.Channels)
            {
                _visibleChannels.Add(channel);
            }

            SelectedDeviceText.Text = $"当前设备：AI{machineId}（{deviceItem.Channels.Count} 个通道）";
        }
        else
        {
            SelectedDeviceText.Text = "当前设备：未选择";
        }
    }

    private void OnSelectAllChannelsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var channel in _visibleChannels)
        {
            channel.IsEnabled = true;
        }

        UpdateTopologySummary();
        RenderResultViews();
    }

    private void OnClearAllChannelsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var channel in _visibleChannels)
        {
            channel.IsEnabled = false;
        }

        UpdateTopologySummary();
        RenderResultViews();
    }

    private void OnInvertChannelsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var channel in _visibleChannels)
        {
            channel.IsEnabled = !channel.IsEnabled;
        }

        UpdateTopologySummary();
        RenderResultViews();
    }

    private void OnChannelEnabledChanged(object? sender, RoutedEventArgs e)
    {
        UpdateTopologySummary();
        RenderResultViews();
    }

    private void OnWaveWindowSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WaveWindowComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is not string tag ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return;
        }

        _waveDisplayWindowSeconds = Math.Clamp(seconds, 0.5d, MaxWaveWindowSeconds);
        RenderResultViews();
    }

    private void UpdateTopologySummary()
    {
        var deviceCount = _devices.Count;
        var totalChannelCount = _channelsByCompositeId.Count;
        var enabledChannelCount = _channelsByCompositeId.Values.Count(static channel => channel.IsEnabled);
        var onlineChannelCount = _channelsByCompositeId.Values.Count(static channel => channel.IsOnline);

        DeviceCountText.Text = deviceCount.ToString(CultureInfo.InvariantCulture);
        TotalChannelCountText.Text = totalChannelCount.ToString(CultureInfo.InvariantCulture);
        EnabledChannelCountText.Text = enabledChannelCount.ToString(CultureInfo.InvariantCulture);
        OnlineChannelCountText.Text = onlineChannelCount.ToString(CultureInfo.InvariantCulture);
        SelectedChannelsSummaryText.Text = $"已选 {enabledChannelCount} / {totalChannelCount}";
    }

    private async void OnStartSdkValidationClick(object? sender, RoutedEventArgs e)
    {
        if (_validationSources.Count > 0)
        {
            AppendLog("采样已在运行。");
            return;
        }

        if (!_sdkInitialized)
        {
            AppendLog("请先初始化拓扑后再启动采样。");
            return;
        }

        var enabledChannels = _channelsByCompositeId.Values
            .Where(static channel => channel.IsEnabled)
            .OrderBy(static channel => channel.CompositeId)
            .ToList();

        if (enabledChannels.Count == 0)
        {
            AppendLog("请至少勾选一个通道。");
            return;
        }

        try
        {
            Interlocked.Exchange(ref _callbackBlocks, 0);
            Interlocked.Exchange(ref _callbackBytes, 0);
            Interlocked.Exchange(ref _lastCallbackUnixMs, 0);
            InitializeCounters();
            _channelWaveBuffers.Clear();

            foreach (var channel in _channelsByCompositeId.Values)
            {
                channel.IsOnline = false;
                channel.LastSeenLocal = null;
                channel.LastValue = 0d;
            }

            var descriptor = new SourceDescriptor
            {
                SourceId = enabledChannels[0].MachineId,
                DeviceName = _useNativeMode ? "DH SDK 数据源" : "模拟数据源",
                ChannelCount = BuildExpectedChannelCount(enabledChannels),
                SampleRateHz = _detectedSampleRateHz,
                SampleType = SampleType.Float32
            };

            var blockPool = new BlockPool();
            IReadOnlyList<IDescriptorAcquisitionSource> sources = _useNativeMode
                ? [CreateNativeValidationSource(descriptor, blockPool)]
                : CreateMockValidationSources(enabledChannels, blockPool);

            if (ShouldAutoStartStorage())
            {
                await StartStorageSessionAsync(enabledChannels);
            }

            foreach (var source in sources)
            {
                source.BlockArrived += OnValidationBlockArrived;
                source.Start();
                _validationSources.Add(source);
            }

            InitializeSdkButton.IsEnabled = false;
            StartValidationBtn.IsEnabled = false;
            StopValidationBtn.IsEnabled = true;
            StateBadgeText.Text = "运行中";
            SdkStatusText.Text = _useNativeMode ? "SDK 采样中" : "模拟采样中";

            UpdateTopologySummary();
            UpdateStorageButtons();
            UpdateStorageSummary();
            RenderResultViews();
            AppendLog($"已启动采样，共 {enabledChannels.Count} 个已启用通道。");
        }
        catch (Exception ex)
        {
            CleanupValidationSource();
            UpdateStorageButtons();
            SdkStatusText.Text = "鍚姩澶辫触";
            AppendLog($"启动采样失败：{ex.Message}");
        }
    }

    private IDescriptorAcquisitionSource CreateNativeValidationSource(SourceDescriptor descriptor, BlockPool blockPool)
    {
        return new DhSdkAcquisitionSource(
            descriptor,
            blockPool,
            new DhSdkOptions
            {
                SdkDirectory = string.Empty,
                ConfigDirectory = ConfigDirectoryTextBox.Text ?? string.Empty,
                DataCountPerCallback = 128,
                SingleMachineMode = false,
                AutoConnectDevices = true
            });
    }

    private static IDescriptorAcquisitionSource CreateMockValidationSource(SourceDescriptor descriptor, BlockPool blockPool)
    {
        return new DemoCallbackAcquisitionSource(
            descriptor,
            blockPool,
            new MockSdkBridge(),
            128,
            TimeSpan.FromMilliseconds(10));
    }

    private int BuildExpectedChannelCount(IReadOnlyList<ChannelItem> enabledChannels)
    {
        return enabledChannels
            .GroupBy(static channel => channel.MachineId)
            .Select(group =>
            {
                if (_devicesByMachineId.TryGetValue(group.Key, out var device))
                {
                    return Math.Max(1, device.Channels.Count);
                }

                return Math.Max(1, group.Count());
            })
            .DefaultIfEmpty(1)
            .Max();
    }

    private IReadOnlyList<IDescriptorAcquisitionSource> CreateMockValidationSources(
        IReadOnlyList<ChannelItem> enabledChannels,
        BlockPool blockPool)
    {
        var sources = new List<IDescriptorAcquisitionSource>();
        var machineIds = enabledChannels
            .Select(static channel => channel.MachineId)
            .Distinct()
            .OrderBy(static machineId => machineId);

        foreach (var machineId in machineIds)
        {
            var channelCount = _devicesByMachineId.TryGetValue(machineId, out var device)
                ? Math.Max(1, device.Channels.Count)
                : Math.Max(1, enabledChannels.Count(channel => channel.MachineId == machineId));

            var descriptor = new SourceDescriptor
            {
                SourceId = machineId,
                DeviceName = $"模拟数据源 AI{machineId}",
                ChannelCount = channelCount,
                SampleRateHz = _detectedSampleRateHz,
                SampleType = SampleType.Float32
            };

            sources.Add(CreateMockValidationSource(descriptor, blockPool));
        }

        return sources;
    }

    private async void OnStopSdkValidationClick(object? sender, RoutedEventArgs e)
    {
        if (IsStorageRunning())
        {
            await StopStorageSessionAsync();
        }

        StopSdkValidation();
    }

    private void OnValidationBlockArrived(DataBlock block)
    {
        try
        {
            var callbackCount = Interlocked.Increment(ref _callbackBlocks);
            var totalBytes = Interlocked.Add(ref _callbackBytes, block.PayloadLength);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.Exchange(ref _lastCallbackUnixMs, nowMs);

            if (_storageOrchestrator is not null)
            {
                if (_storageOrchestrator.TryEnqueueRaw(block))
                {
                    Interlocked.Increment(ref _storageEnqueuedBlocks);
                    Interlocked.Add(ref _storageEnqueuedBytes, block.PayloadLength);
                    _storageBlocksBySource.AddOrUpdate(block.Header.SourceId, 1, static (_, current) => current + 1);
                }
                else
                {
                    Interlocked.Increment(ref _storageEnqueueFailures);
                }
            }

            int? expectedChannelCount = null;
            lock (_topologyLock)
            {
                if (_devicesByMachineId.TryGetValue(block.Header.SourceId, out var device))
                {
                    expectedChannelCount = device.Channels.Count;
                }
            }

            var chunks = ParseWaveChunks(block, expectedChannelCount);
            var samples = BuildLatestSamples(chunks);
            var update = new CallbackUiUpdate(callbackCount, totalBytes, nowMs, samples, chunks);

            Dispatcher.UIThread.Post(() => ApplyCallbackUpdate(update));
        }
        finally
        {
            block.Release();
        }
    }

    private static List<ChannelWaveChunk> ParseWaveChunks(DataBlock block, int? expectedChannelCount)
    {
        var payload = block.Payload.Span;
        if (payload.Length == 0)
        {
            return new List<ChannelWaveChunk>();
        }

        var channelCount = Math.Max(1, block.Header.ChannelCount);
        var machineId = block.Header.SourceId;

        if (block.Header.SampleType == SampleType.Float32)
        {
            var floatCount = payload.Length / sizeof(float);
            if (floatCount <= 0)
            {
                return new List<ChannelWaveChunk>();
            }

            channelCount = Math.Clamp(channelCount, 1, floatCount);
            if (expectedChannelCount.HasValue)
            {
                channelCount = Math.Min(channelCount, Math.Max(1, expectedChannelCount.Value));
            }

            var samplesPerChannel = Math.Max(1, floatCount / channelCount);
            var perChannelData = DecodeFloatChannels(payload, channelCount, samplesPerChannel, interleaved: true);
            return BuildWaveChunks(machineId, perChannelData);
        }

        if (block.Header.SampleType == SampleType.Int16)
        {
            var shortCount = payload.Length / sizeof(short);
            if (shortCount <= 0)
            {
                return new List<ChannelWaveChunk>();
            }

            channelCount = Math.Clamp(channelCount, 1, shortCount);
            if (expectedChannelCount.HasValue)
            {
                channelCount = Math.Min(channelCount, Math.Max(1, expectedChannelCount.Value));
            }

            var samplesPerChannel = Math.Max(1, shortCount / channelCount);
            var perChannelData = new double[channelCount][];

            for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                perChannelData[channelIndex] = new double[samplesPerChannel];
            }

            for (var sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
            {
                for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    var rawIndex = sampleIndex * channelCount + channelIndex;
                    if (rawIndex >= shortCount)
                    {
                        break;
                    }

                    var offset = rawIndex * sizeof(short);
                    perChannelData[channelIndex][sampleIndex] = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, sizeof(short)));
                }
            }

            return BuildWaveChunks(machineId, perChannelData);
        }

        return new List<ChannelWaveChunk>();
    }

    private static double[][] DecodeFloatChannels(ReadOnlySpan<byte> payload, int channelCount, int samplesPerChannel, bool interleaved)
    {
        var data = new double[channelCount][];
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            data[channelIndex] = new double[samplesPerChannel];
        }

        var floatCount = payload.Length / sizeof(float);
        if (interleaved)
        {
            for (var sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
            {
                for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    var rawIndex = sampleIndex * channelCount + channelIndex;
                    if (rawIndex >= floatCount)
                    {
                        break;
                    }

                    var offset = rawIndex * sizeof(float);
                    data[channelIndex][sampleIndex] = BitConverter.ToSingle(payload.Slice(offset, sizeof(float)));
                }
            }

            return data;
        }

        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            var channelStart = channelIndex * samplesPerChannel;
            for (var sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
            {
                var rawIndex = channelStart + sampleIndex;
                if (rawIndex >= floatCount)
                {
                    break;
                }

                var offset = rawIndex * sizeof(float);
                data[channelIndex][sampleIndex] = BitConverter.ToSingle(payload.Slice(offset, sizeof(float)));
            }
        }

        return data;
    }

    private static List<ChannelWaveChunk> BuildWaveChunks(int machineId, IReadOnlyList<double[]> perChannelData)
    {
        var chunks = new List<ChannelWaveChunk>(perChannelData.Count);
        for (var index = 0; index < perChannelData.Count; index++)
        {
            chunks.Add(new ChannelWaveChunk(machineId, index + 1, perChannelData[index]));
        }

        return chunks;
    }

    private static List<ChannelValueSample> BuildLatestSamples(IReadOnlyList<ChannelWaveChunk> chunks)
    {
        var samples = new List<ChannelValueSample>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Samples.Count == 0)
            {
                continue;
            }

            samples.Add(new ChannelValueSample(chunk.MachineId, chunk.ChannelNo, chunk.Samples[^1]));
        }

        return samples;
    }

    private void ApplyCallbackUpdate(CallbackUiUpdate update)
    {
        CallbackBlocksText.Text = update.CallbackCount.ToString("N0", CultureInfo.InvariantCulture);
        CallbackBytesText.Text = $"{update.TotalBytes:N0} 瀛楄妭";
        LastCallbackText.Text = DateTimeOffset.FromUnixTimeMilliseconds(update.LastCallbackUnixMs).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        UpdateStorageSummary();

        var topologyChanged = false;
        lock (_topologyLock)
        {
            foreach (var sample in update.Samples)
            {
                var compositeId = CreateCompositeId(sample.MachineId, sample.ChannelNo);
                if (!_channelsByCompositeId.TryGetValue(compositeId, out var channel))
                {
                    if (_sdkInitialized)
                    {
                        continue;
                    }

                    if (!_devicesByMachineId.TryGetValue(sample.MachineId, out var device))
                    {
                        device = new DeviceItem(sample.MachineId, string.Empty)
                        {
                            IsOnline = true
                        };
                        _devices.Add(device);
                        _devicesByMachineId[sample.MachineId] = device;
                        topologyChanged = true;
                    }

                    channel = new ChannelItem(sample.MachineId, sample.ChannelNo, sample.ChannelNo, "回调自动发现")
                    {
                        IsEnabled = true,
                        IsOnline = true
                    };

                    device.Channels.Add(channel);
                    device.NotifyChannelSummaryChanged();
                    _channelsByCompositeId[compositeId] = channel;
                    topologyChanged = true;
                }

                channel.IsOnline = true;
                channel.LastSeenLocal = DateTime.Now;
                channel.LastValue = sample.Value;
            }

            foreach (var chunk in update.Chunks)
            {
                if (!_channelsByCompositeId.ContainsKey(CreateCompositeId(chunk.MachineId, chunk.ChannelNo)))
                {
                    continue;
                }

                AppendWaveSamples(chunk);
            }
        }

        if (_selectedMachineId < 0 && _devices.Count > 0)
        {
            SelectDevice(_devices[0].MachineId);
            topologyChanged = true;
        }
        else if (topologyChanged && _selectedMachineId > 0)
        {
            SelectDevice(_selectedMachineId);
        }

        UpdateTopologySummary();
        RenderResultViews();
    }

    private void AppendWaveSamples(ChannelWaveChunk chunk)
    {
        var compositeId = CreateCompositeId(chunk.MachineId, chunk.ChannelNo);
        if (!_channelWaveBuffers.TryGetValue(compositeId, out var queue))
        {
            queue = new Queue<double>(_waveBufferCapacity);
            _channelWaveBuffers[compositeId] = queue;
        }

        foreach (var sample in chunk.Samples)
        {
            queue.Enqueue(sample);
            while (queue.Count > _waveBufferCapacity)
            {
                queue.Dequeue();
            }
        }
    }

    private void InitializeCounters()
    {
        CallbackBlocksText.Text = "0";
        CallbackBytesText.Text = "0 瀛楄妭";
        LastCallbackText.Text = "--";
    }

    private void StopSdkValidation()
    {
        if (_validationSources.Count == 0)
        {
            if (IsStorageRunning())
            {
                StopStorageSessionAsync().GetAwaiter().GetResult();
            }

            UpdateStorageButtons();
            return;
        }

        foreach (var source in _validationSources.ToArray())
        {
            try
        {
            source.BlockArrived -= OnValidationBlockArrived;
            source.Stop();
            source.Dispose();
        }
            catch (Exception ex)
        {
            AppendLog($"鍋滄閲囨牱鏃跺彂鐢熼敊璇細{ex.Message}");
            }
        }

        _validationSources.Clear();

        if (IsStorageRunning())
        {
            StopStorageSessionAsync().GetAwaiter().GetResult();
        }

        InitializeSdkButton.IsEnabled = true;
        StartValidationBtn.IsEnabled = _sdkInitialized;
        StopValidationBtn.IsEnabled = false;
        StateBadgeText.Text = _sdkInitialized ? "就绪" : "待机";
        SdkStatusText.Text = _sdkInitialized ? "已初始化" : "未初始化";
        AppendLog("采样已停止。");
    }

    private void CleanupValidationSource()
    {
        if (_validationSources.Count == 0)
        {
            if (IsStorageRunning())
            {
                StopStorageSessionAsync().GetAwaiter().GetResult();
            }

            InitializeSdkButton.IsEnabled = true;
            StartValidationBtn.IsEnabled = _sdkInitialized;
            StopValidationBtn.IsEnabled = false;
            UpdateStorageButtons();
            return;
        }

        foreach (var source in _validationSources.ToArray())
        {
            try
            {
                source.BlockArrived -= OnValidationBlockArrived;
                source.Stop();
                source.Dispose();
            }
            catch
            {
            }
        }

        _validationSources.Clear();
        if (IsStorageRunning())
        {
            StopStorageSessionAsync().GetAwaiter().GetResult();
        }
        InitializeSdkButton.IsEnabled = true;
        StartValidationBtn.IsEnabled = _sdkInitialized;
        StopValidationBtn.IsEnabled = false;
        UpdateStorageButtons();
    }

    private void OnAddResultViewClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _resultViews.Add(CreateResultView());
            RebuildResultViewGrid();
            RenderResultViews();
        }
        catch (Exception ex)
        {
            AppendLog($"新增结果视图失败：{ex.Message}");
        }
    }

    private void OnRemoveResultViewClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_resultViews.Count <= 1)
            {
                return;
            }

            _resultViews.RemoveAt(_resultViews.Count - 1);
            RebuildResultViewGrid();
            RenderResultViews();
        }
        catch (Exception ex)
        {
            AppendLog($"移除结果视图失败：{ex.Message}");
        }
    }

    private void OnResetStatisticsClick(object? sender, RoutedEventArgs e)
    {
        Interlocked.Exchange(ref _callbackBlocks, 0);
        Interlocked.Exchange(ref _callbackBytes, 0);
        Interlocked.Exchange(ref _lastCallbackUnixMs, 0);
        InitializeCounters();

        foreach (var channel in _channelsByCompositeId.Values)
        {
            channel.LastValue = 0d;
            channel.LastSeenLocal = null;
            channel.IsOnline = false;
        }

        _channelWaveBuffers.Clear();
        foreach (var view in _resultViews)
        {
            view.Canvas.ClearWaveform();
            view.SubtitleText.Text = "等待数据";
        }

        UpdateTopologySummary();
        RenderResultViews();
        AppendLog("统计信息与波形缓存已重置。");
    }

    private void InitializeResultViews()
    {
        _resultViews.Clear();
        _nextResultViewNumber = 1;
        for (var index = 1; index <= 4; index++)
        {
            _resultViews.Add(CreateResultView());
        }

        RebuildResultViewGrid();
    }

    private ResultViewItem CreateResultView()
    {
        var view = new ResultViewItem($"视图 {_nextResultViewNumber++}");
        view.SelectionChanged += OnResultViewSelectionChanged;
        return view;
    }

    private void OnResultViewSelectionChanged(ResultViewItem view)
    {
        RenderResultViews();
    }

    private void RebuildResultViewGrid()
    {
        ResultViewsGridControl.Children.Clear();

        foreach (var view in _resultViews)
        {
            ResultViewsGridControl.Children.Add(view.Card);
        }
    }

    private void RenderResultViews()
    {
        if (_resultViews.Count == 0)
        {
            return;
        }

        var enabledChannels = _channelsByCompositeId.Values
            .Where(static channel => channel.IsEnabled)
            .OrderBy(static channel => channel.CompositeId)
            .ToList();

        if (enabledChannels.Count == 0)
        {
            foreach (var view in _resultViews)
            {
                view.SyncSelectableChannels(enabledChannels);
                view.TitleText.Text = view.Title;
                view.SetLegend(Array.Empty<(string Label, Avalonia.Media.Color Color)>());
                view.SubtitleText.Text = "请先在通道管理中勾选至少一个通道";
                view.Canvas.SetPlaceholder("请先在通道管理中勾选至少一个通道");
            }

            return;
        }

        var desiredSamples = (int)Math.Ceiling(_detectedSampleRateHz * _waveDisplayWindowSeconds);

        for (var index = 0; index < _resultViews.Count; index++)
        {
            var view = _resultViews[index];
            view.SyncSelectableChannels(enabledChannels);

            if (view.SelectedChannelCount == 0 && !view.HasManualSelection)
            {
                view.SetSelectedChannels(
                [
                    enabledChannels[index % enabledChannels.Count].CompositeId
                ],
                false);
            }

            var selectedChannels = view.GetSelectedChannels();
            view.TitleText.Text = $"{view.Title} · 已选 {selectedChannels.Count} 个通道";

            if (selectedChannels.Count == 0)
            {
                view.SetLegend(Array.Empty<(string Label, Avalonia.Media.Color Color)>());
                view.SubtitleText.Text = "请展开“选择通道”并勾选要查看的通道";
                view.Canvas.SetPlaceholder("请选择要显示的通道");
                continue;
            }

            var series = new List<AcqShell.UI.Controls.WaveformSeries>();
            var legends = new List<(string Label, Avalonia.Media.Color Color)>(selectedChannels.Count);
            var onlineCount = 0;

            for (var channelIndex = 0; channelIndex < selectedChannels.Count; channelIndex++)
            {
                var channel = selectedChannels[channelIndex];
                var color = ResultViewItem.GetPaletteColor(channelIndex);
                legends.Add((channel.DisplayCode, color));

                if (channel.IsOnline)
                {
                    onlineCount++;
                }

                if (_channelWaveBuffers.TryGetValue(channel.CompositeId, out var queue) && queue.Count > 1)
                {
                    var samples = BuildVisibleWaveform(queue, desiredSamples);
                    series.Add(new AcqShell.UI.Controls.WaveformSeries(channel.DisplayCode, samples, color));
                }
            }

            view.SetLegend(legends);

            if (series.Count == 0)
            {
                view.SubtitleText.Text = $"已选 {selectedChannels.Count} 个通道，正在等待数据";
                view.Canvas.SetPlaceholder("已选通道正在等待数据");
                continue;
            }

            var displayPointCount = series.Max(static item => item.Samples.Count);
            var displayIntervalMs = _waveDisplayWindowSeconds * 1000d / Math.Max(1, displayPointCount - 1);
            view.SubtitleText.Text = $"在线 {onlineCount}/{selectedChannels.Count}，已绘制 {series.Count} 条波形";
            view.Canvas.SetWaveforms(series, displayIntervalMs, _waveDisplayWindowSeconds * 1000d, $"叠加显示 {series.Count} 条通道");
        }
    }

    private static double[] BuildVisibleWaveform(Queue<double> queue, int desiredSamples)
    {
        var allSamples = queue.ToArray();
        if (allSamples.Length <= 2)
        {
            return allSamples;
        }

        var takeCount = desiredSamples > 0 ? Math.Min(allSamples.Length, desiredSamples) : allSamples.Length;
        var startIndex = Math.Max(0, allSamples.Length - takeCount);
        var visible = new double[takeCount];
        Array.Copy(allSamples, startIndex, visible, 0, takeCount);

        return Downsample(visible, MaxRenderedWavePoints);
    }

    private static double[] Downsample(double[] samples, int maxPoints)
    {
        if (samples.Length <= maxPoints)
        {
            return samples;
        }

        return DownsampleWithLargestTriangleThreeBuckets(samples, maxPoints);
    }

    private static double[] DownsampleWithLargestTriangleThreeBuckets(IReadOnlyList<double> samples, int threshold)
    {
        if (threshold >= samples.Count || threshold < 3)
        {
            return samples.ToArray();
        }

        var sampled = new double[threshold];
        sampled[0] = samples[0];
        sampled[^1] = samples[^1];

        var bucketSize = (double)(samples.Count - 2) / (threshold - 2);
        var anchorIndex = 0;

        for (var bucketIndex = 0; bucketIndex < threshold - 2; bucketIndex++)
        {
            var averageRangeStart = (int)Math.Floor((bucketIndex + 1) * bucketSize) + 1;
            var averageRangeEnd = (int)Math.Floor((bucketIndex + 2) * bucketSize) + 1;
            averageRangeEnd = Math.Min(averageRangeEnd, samples.Count);

            if (averageRangeStart >= averageRangeEnd)
            {
                averageRangeStart = Math.Max(1, averageRangeEnd - 1);
            }

            double averageX = 0d;
            double averageY = 0d;
            var averageRangeLength = Math.Max(1, averageRangeEnd - averageRangeStart);

            for (var sampleIndex = averageRangeStart; sampleIndex < averageRangeEnd; sampleIndex++)
            {
                averageX += sampleIndex;
                averageY += samples[sampleIndex];
            }

            averageX /= averageRangeLength;
            averageY /= averageRangeLength;

            var rangeStart = (int)Math.Floor(bucketIndex * bucketSize) + 1;
            var rangeEnd = (int)Math.Floor((bucketIndex + 1) * bucketSize) + 1;
            rangeEnd = Math.Min(rangeEnd, samples.Count - 1);
            rangeStart = Math.Min(rangeStart, rangeEnd);

            var maxArea = double.MinValue;
            var selectedIndex = rangeStart;

            for (var sampleIndex = rangeStart; sampleIndex < rangeEnd; sampleIndex++)
            {
                var area = Math.Abs(
                    (anchorIndex - averageX) * (samples[sampleIndex] - samples[anchorIndex]) -
                    (anchorIndex - sampleIndex) * (averageY - samples[anchorIndex]));

                if (area > maxArea)
                {
                    maxArea = area;
                    selectedIndex = sampleIndex;
                }
            }

            sampled[bucketIndex + 1] = samples[selectedIndex];
            anchorIndex = selectedIndex;
        }

        return sampled;
    }

    private void ClearTopology()
    {
        lock (_topologyLock)
        {
            _devices.Clear();
            _visibleChannels.Clear();
            _devicesByMachineId.Clear();
            _channelsByCompositeId.Clear();
            _channelWaveBuffers.Clear();
            _selectedMachineId = -1;
        }

        SelectedDeviceText.Text = "当前设备：未选择";
        UpdateTopologySummary();
    }

    private void AppendLog(string line)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}";
        LogBox.Text = string.IsNullOrWhiteSpace(LogBox.Text)
            ? entry
            : $"{LogBox.Text}{Environment.NewLine}{entry}";
    }

    protected override void OnClosed(EventArgs e)
    {
        StopSdkValidation();
        base.OnClosed(e);
    }

    private sealed record CallbackUiUpdate(
        long CallbackCount,
        long TotalBytes,
        long LastCallbackUnixMs,
        IReadOnlyList<ChannelValueSample> Samples,
        IReadOnlyList<ChannelWaveChunk> Chunks);

    private sealed record ChannelValueSample(int MachineId, int ChannelNo, double Value);

    private sealed record ChannelWaveChunk(int MachineId, int ChannelNo, IReadOnlyList<double> Samples);
}
