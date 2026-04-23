#if false
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AcqEngine.Core;
using AcqEngine.DeviceSdk;

namespace AcqShell.UI;

public partial class MainWindow : Window
{
    private readonly object _topologyLock = new();

    private int _sessionNo = 1;
    private bool _useNativeMode = true;
    private bool _sdkInitialized;
    private int _selectedMachineId = -1;
    private double _detectedSampleRateHz = 1000;

    private readonly ObservableCollection<DeviceItem> _devices = new();
    private readonly ObservableCollection<ChannelItem> _visibleChannels = new();
    private readonly Dictionary<int, DeviceItem> _devicesByMachineId = new();
    private readonly Dictionary<int, ChannelItem> _channelsByCompositeId = new();
    private readonly ObservableCollection<ResultViewItem> _resultViews = new();
    private readonly Dictionary<int, Queue<double>> _channelWaveBuffers = new();

    private IDescriptorAcquisitionSource? _sdkValidationSource;
    private long _callbackBlocks;
    private long _callbackBytes;
    private long _lastCallbackUnixMs;

    private int _waveBufferCapacity = 8192;
    private const int MaxRenderedWavePoints = 1400;
    private double _waveDisplayWindowSeconds = 0.002;

    public MainWindow()
    {
        InitializeComponent();
        BindCollections();
        InitializeResultViews();
        InitializeCounters();

        AppendLog("界面已启动，等待接入引擎进程通信。");
        AppendResultLog("请先在通道管理页完成SDK初始化与设备探测。");
        UpdateModeState();
        UpdateEnabledChannelCount();
    }

    private void OnStartSessionClick(object? sender, RoutedEventArgs e)
    {
        StateBadgeText.Text = "运行中";
        SdkStatusText.Text = $"会话 {_sessionNo} 正在采集";
        StartValidationBtn.IsEnabled = false;
        StopValidationBtn.IsEnabled = true;

        AppendLog($"[{DateTime.Now:HH:mm:ss}] 已请求启动会话 {_sessionNo}。");
    }

    private void OnStopSessionClick(object? sender, RoutedEventArgs e)
    {
        StateBadgeText.Text = "空闲";
        SdkStatusText.Text = $"会话 {_sessionNo} 已停止";
        StartValidationBtn.IsEnabled = true;
        StopValidationBtn.IsEnabled = false;

        AppendLog($"[{DateTime.Now:HH:mm:ss}] 已请求停止会话 {_sessionNo}。");
        _sessionNo++;
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(LogBox.Text))
        {
            LogBox.Text = line;
            return;
        }

        LogBox.Text += Environment.NewLine + line;
    }

    private void OnDataSourceModeChanged(object? sender, RoutedEventArgs e)
    {
        UpdateModeState();
    }

    private void UpdateModeState()
    {
        _useNativeMode = NativeModeRadio.IsChecked == true;
        TopologyStatusText.Text = _useNativeMode
            ? "真实SDK模式，等待初始化"
            : "模拟模式，等待生成拓扑";
    }

    private async void OnBrowseSdkDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderAsync("选择SDK目录", SdkDirectoryTextBox);
    }

    private async void OnBrowseConfigDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderAsync("选择配置目录", ConfigDirectoryTextBox);
    }

    private async Task BrowseFolderAsync(string title, TextBox target)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            AppendResultLog("当前环境不支持目录选择器。可手动输入路径。");
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
        }
        catch (Exception ex)
        {
            AppendResultLog($"打开目录选择器失败：{ex.Message}");
            return;
        }

        if (folders.Count == 0)
        {
            return;
        }

        target.Text = folders[0].Path.LocalPath;
    }

    private void OnInitializeSdkClick(object? sender, RoutedEventArgs e)
    {
        if (_sdkValidationSource is not null)
        {
            AppendResultLog("当前正在验证，请先停止后再重新初始化。");
            return;
        }

        try
        {
            ClearTopology();

            if (_useNativeMode)
            {
                var resolvedSdkDirectory = DhSdkPathResolver.ResolveSdkDirectory(
                    SdkDirectoryTextBox.Text ?? string.Empty,
                    ConfigDirectoryTextBox.Text ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(resolvedSdkDirectory))
                {
                    SdkDirectoryTextBox.Text = resolvedSdkDirectory;
                    AppendResultLog($"自动识别SDK目录：{resolvedSdkDirectory}");
                }

                var topology = DhSdkTopologyDiscovery.Discover(
                    SdkDirectoryTextBox.Text ?? string.Empty,
                    ConfigDirectoryTextBox.Text ?? string.Empty);

                ApplyNativeTopology(topology);
                _detectedSampleRateHz = topology.SampleRateHz;
                _waveBufferCapacity = CalculateWaveBufferCapacity(_detectedSampleRateHz);

                TopologyStatusText.Text =
                    $"探测完成：{_devices.Count} 台设备，采样率 {_detectedSampleRateHz:F0}Hz";
                SdkStatusText.Text = "SDK初始化成功";
                AppendResultLog(TopologyStatusText.Text);
            }
            else
            {
                var deviceCount = ParsePositiveInt(MockDeviceCountTextBox.Text, 4, 1, 64);
                var channelsPerDevice = ParsePositiveInt(MockChannelCountTextBox.Text, 16, 1, 128);

                BuildMockTopology(deviceCount, channelsPerDevice);
                _detectedSampleRateHz = 10000;
                _waveBufferCapacity = CalculateWaveBufferCapacity(_detectedSampleRateHz);

                TopologyStatusText.Text =
                    $"模拟拓扑已生成：{deviceCount} 台设备，每台 {channelsPerDevice} 通道";
                SdkStatusText.Text = "模拟拓扑初始化完成";
                AppendResultLog(TopologyStatusText.Text);
            }

            _sdkInitialized = _devices.Count > 0;
            StartValidationBtn.IsEnabled = _sdkInitialized;
            StopValidationBtn.IsEnabled = false;
            // InitializeSdkButtonControl.IsEnabled = true;

            if (_sdkInitialized)
            {
                StateBadgeText.Text = "就绪";
            }

            UpdateEnabledChannelCount();
            RenderResultViews();
            RefreshDeviceListBindings();
            RefreshChannelListBindings();
        }
        catch (Exception ex)
        {
            _sdkInitialized = false;
            StartValidationBtn.IsEnabled = false;
            TopologyStatusText.Text = "初始化失败";
            SdkStatusText.Text = "初始化失败";
            AppendResultLog($"初始化失败：{ex.Message}");
            AppendResultLog("提示：如果东华软件安装在 D:\\DHDAS，可只填写配置目录 D:\\DHDAS\\config，SDK目录留空即可。");
        }
    }

    private static int ParsePositiveInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, out var parsed))
        {
            return fallback;
        }

        if (parsed < min)
        {
            return min;
        }

        if (parsed > max)
        {
            return max;
        }

        return parsed;
    }

    private static int CalculateWaveBufferCapacity(double sampleRateHz)
    {
        // 对齐 DH-example 的“较长时间窗”思路：缓存约 50ms 的历史点，避免高采样率下只看到线段。
        var estimate = (int)Math.Round(sampleRateHz * 0.05);
        return Math.Clamp(estimate, 4000, 50000);
    }

    private void ApplyNativeTopology(DhSdkTopologySnapshot topology)
    {
        lock (_topologyLock)
        {
            foreach (var device in topology.Devices.OrderBy(static x => x.MachineId))
            {
                var uiDevice = new DeviceItem
                {
                    MachineId = device.MachineId,
                    IpAddress = device.IpAddress,
                    IsOnline = true
                };

                foreach (var channel in device.Channels.OrderBy(static x => x.ChannelIndex))
                {
                    var uiChannel = new ChannelItem
                    {
                        MachineId = device.MachineId,
                        ChannelNo = channel.ChannelIndex,
                        RawChannelId = channel.ChannelId,
                        IsEnabled = true,
                        IsOnline = channel.Online,
                        Description = $"SDK通道ID {channel.ChannelId}"
                    };

                    uiDevice.Channels.Add(uiChannel);
                    _channelsByCompositeId[uiChannel.CompositeId] = uiChannel;
                }

                _devices.Add(uiDevice);
                _devicesByMachineId[uiDevice.MachineId] = uiDevice;
            }

            if (_devices.Count > 0)
            {
                SelectDevice(_devices[0].MachineId);
            }
        }
    }

    private void BuildMockTopology(int deviceCount, int channelsPerDevice)
    {
        lock (_topologyLock)
        {
            for (var machineId = 1; machineId <= deviceCount; machineId++)
            {
                var device = new DeviceItem
                {
                    MachineId = machineId,
                    IpAddress = "127.0.0.1",
                    IsOnline = true
                };

                for (var channelNo = 1; channelNo <= channelsPerDevice; channelNo++)
                {
                    var channel = new ChannelItem
                    {
                        MachineId = machineId,
                        ChannelNo = channelNo,
                        RawChannelId = channelNo,
                        IsEnabled = true,
                        IsOnline = true,
                        Description = "模拟通道"
                    };

                    device.Channels.Add(channel);
                    _channelsByCompositeId[channel.CompositeId] = channel;
                }

                _devices.Add(device);
                _devicesByMachineId[machineId] = device;
            }

            if (_devices.Count > 0)
            {
                SelectDevice(_devices[0].MachineId);
            }
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

        if (machineId < 0)
        {
            return;
        }

        SelectDevice(machineId);
        RefreshDeviceListBindings();
        RefreshChannelListBindings();
    }

    private void SelectDevice(int machineId)
    {
        _selectedMachineId = machineId;

        foreach (var device in _devices)
        {
            device.IsSelected = device.MachineId == machineId;
        }

        _visibleChannels.Clear();
        if (_devicesByMachineId.TryGetValue(machineId, out var selectedDevice))
        {
            foreach (var channel in selectedDevice.Channels)
            {
                _visibleChannels.Add(channel);
            }

            SelectedDeviceText.Text =
                $"AI{machineId} 通道列表（{selectedDevice.Channels.Count} 路）";
        }
        else
        {
            SelectedDeviceText.Text = "通道列表";
        }
    }

    private void OnSelectAllChannelsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var channel in _visibleChannels)
        {
            channel.IsEnabled = true;
        }

        RefreshChannelListBindings();
        UpdateEnabledChannelCount();
        RenderResultViews();
    }

    private void OnClearAllChannelsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var channel in _visibleChannels)
        {
            channel.IsEnabled = false;
        }

        RefreshChannelListBindings();
        UpdateEnabledChannelCount();
        RenderResultViews();
    }

    private void OnInvertChannelsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var channel in _visibleChannels)
        {
            channel.IsEnabled = !channel.IsEnabled;
        }

        RefreshChannelListBindings();
        UpdateEnabledChannelCount();
        RenderResultViews();
    }

    private void OnChannelChecked(object? sender, RoutedEventArgs e)
    {
        UpdateEnabledChannelCount();
        RenderResultViews();
    }

    private void OnWaveWindowSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is not string tag ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return;
        }

        _waveDisplayWindowSeconds = Math.Clamp(seconds, 0.0002, 0.05);
        RenderResultViews();
    }

    private void UpdateEnabledChannelCount()
    {
        IEnumerable<ChannelItem> channels = _channelsByCompositeId.Values;
        if (_useNativeMode && _sdkValidationSource is not null)
        {
            channels = channels.Where(static c => c.IsOnline).ToList();
        }

        var enabledCount = channels.Count(static c => c.IsEnabled);
        var totalCount = channels.Count();

        SelectedChannelsSummaryText.Text = $"已启用通道: {enabledCount}/{totalCount}";
        SelectedChannelsSummaryText.Text = $"已选通道: {enabledCount}/{totalCount}";
    }

    private void OnStartSdkValidationClick(object? sender, RoutedEventArgs e)
    {
        if (_sdkValidationSource is not null)
        {
            AppendResultLog("SDK验证已在运行，请勿重复启动。");
            return;
        }

        if (!_sdkInitialized)
        {
            AppendResultLog("请先在通道管理页完成初始化。");
            return;
        }

        var enabledChannels = _channelsByCompositeId.Values
            .Where(static x => x.IsEnabled)
            .OrderBy(static x => x.CompositeId)
            .ToList();

        if (enabledChannels.Count == 0)
        {
            SdkStatusText.Text = "启动失败：未选择通道";
            AppendResultLog("启动失败：请至少勾选一个通道。");
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
                channel.LastValue = 0;
            }
            UpdateEnabledChannelCount();
            RefreshChannelListBindings();

            var primarySourceId = enabledChannels[0].MachineId;
            var descriptor = new SourceDescriptor
            {
                SourceId = primarySourceId,
                DeviceName = _useNativeMode ? "真实SDK采集源" : "模拟SDK采集源",
                ChannelCount = enabledChannels.Count,
                SampleRateHz = _detectedSampleRateHz,
                SampleType = SampleType.Float32
            };

            var pool = new BlockPool();
            if (_useNativeMode)
            {
                _sdkValidationSource = new DhSdkAcquisitionSource(
                    descriptor,
                    pool,
                    new DhSdkOptions
                    {
                        SdkDirectory = SdkDirectoryTextBox.Text ?? string.Empty,
                        ConfigDirectory = ConfigDirectoryTextBox.Text ?? string.Empty,
                        DataCountPerCallback = 128,
                        SingleMachineMode = false,
                        AutoConnectDevices = true
                    });
            }
            else
            {
                _sdkValidationSource = new DemoCallbackAcquisitionSource(
                    descriptor,
                    pool,
                    new MockSdkBridge(),
                    128,
                    TimeSpan.FromMilliseconds(10));
            }

            _sdkValidationSource.BlockArrived += OnValidationBlockArrived;
            _sdkValidationSource.Start();

            // InitializeSdkButtonControl.IsEnabled = false;
            StartValidationBtn.IsEnabled = false;
            StopValidationBtn.IsEnabled = true;
            StateBadgeText.Text = "验证中";
            SdkStatusText.Text = _useNativeMode
                ? "真实SDK回调验证运行中"
                : "模拟SDK回调验证运行中";

            AppendResultLog(SdkStatusText.Text);
        }
        catch (Exception ex)
        {
            SdkStatusText.Text = "验证启动失败";
            AppendResultLog($"验证启动失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[UI] 验证启动失败: {ex}");
            CleanupValidationSource();
        }
    }

    private void OnStopSdkValidationClick(object? sender, RoutedEventArgs e)
    {
        StopSdkValidation();
    }

    private void OnValidationBlockArrived(DataBlock block)
    {
        try
        {
            var callbackCount = Interlocked.Increment(ref _callbackBlocks);
            var bytes = Interlocked.Add(ref _callbackBytes, block.PayloadLength);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.Exchange(ref _lastCallbackUnixMs, nowMs);

            var waveChunks = ParseWaveChunks(block);
            var samples = BuildLatestSamples(waveChunks);
            var update = new CallbackUiUpdate(
                callbackCount,
                bytes,
                nowMs,
                samples,
                waveChunks,
                block.Header.SourceId,
                block.Header.ChannelCount);

            Dispatcher.UIThread.Post(() => ApplyCallbackUpdate(update));
        }
        finally
        {
            block.Release();
        }
    }

    private static List<ChannelWaveChunk> ParseWaveChunks(DataBlock block)
    {
        var chunks = new List<ChannelWaveChunk>();
        var machineId = block.Header.SourceId >= 0 ? block.Header.SourceId : 0;
        var channelCount = Math.Max(1, block.Header.ChannelCount);
        var payload = block.Payload.Span;

        if (block.Header.SampleType == SampleType.Float32)
        {
            var floatCount = payload.Length / sizeof(float);
            if (floatCount <= 0)
            {
                return chunks;
            }

            channelCount = Math.Clamp(channelCount, 1, floatCount);
            var samplesPerChannel = floatCount / channelCount;
            if (samplesPerChannel <= 0)
            {
                return chunks;
            }

            var interleaved = DecodeFloatChannels(payload, channelCount, samplesPerChannel, interleaved: false);
            return BuildWaveChunks(machineId, interleaved);
        }

        if (block.Header.SampleType == SampleType.Int16)
        {
            var shortCount = payload.Length / sizeof(short);
            if (shortCount <= 0)
            {
                return chunks;
            }

            channelCount = Math.Clamp(channelCount, 1, shortCount);
            var samplesPerChannel = shortCount / channelCount;
            if (samplesPerChannel <= 0)
            {
                return chunks;
            }

            var data = new double[channelCount][];
            for (var ch = 0; ch < channelCount; ch++)
            {
                data[ch] = new double[samplesPerChannel];
            }

            for (var i = 0; i < samplesPerChannel; i++)
            {
                for (var ch = 0; ch < channelCount; ch++)
                {
                    var sampleIndex = i * channelCount + ch;
                    if (sampleIndex >= shortCount)
                    {
                        break;
                    }

                    var offset = sampleIndex * sizeof(short);
                    var value = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, sizeof(short)));
                    data[ch][i] = value;
                }
            }

            return BuildWaveChunks(machineId, data);
        }

        return chunks;
    }

    private static double[][] DecodeFloatChannels(
        ReadOnlySpan<byte> payload,
        int channelCount,
        int samplesPerChannel,
        bool interleaved)
    {
        var data = new double[channelCount][];
        for (var ch = 0; ch < channelCount; ch++)
        {
            data[ch] = new double[samplesPerChannel];
        }

        var floatCount = payload.Length / sizeof(float);
        if (interleaved)
        {
            for (var i = 0; i < samplesPerChannel; i++)
            {
                for (var ch = 0; ch < channelCount; ch++)
                {
                    var sampleIndex = i * channelCount + ch;
                    if (sampleIndex >= floatCount)
                    {
                        break;
                    }

                    var offset = sampleIndex * sizeof(float);
                    data[ch][i] = BitConverter.ToSingle(payload.Slice(offset, sizeof(float)));
                }
            }

            return data;
        }

        for (var ch = 0; ch < channelCount; ch++)
        {
            var channelStart = ch * samplesPerChannel;
            for (var i = 0; i < samplesPerChannel; i++)
            {
                var sampleIndex = channelStart + i;
                if (sampleIndex >= floatCount)
                {
                    break;
                }

                var offset = sampleIndex * sizeof(float);
                data[ch][i] = BitConverter.ToSingle(payload.Slice(offset, sizeof(float)));
            }
        }

        return data;
    }

    private static List<ChannelWaveChunk> BuildWaveChunks(int machineId, IReadOnlyList<double[]> data)
    {
        var chunks = new List<ChannelWaveChunk>(data.Count);
        for (var ch = 0; ch < data.Count; ch++)
        {
            chunks.Add(new ChannelWaveChunk(machineId, ch + 1, data[ch]));
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
        CallbackBlocksText.Text = update.CallbackCount.ToString();
        CallbackBytesText.Text = update.TotalBytes.ToString();
        LastCallbackText.Text = DateTimeOffset
            .FromUnixTimeMilliseconds(update.LastCallbackUnixMs)
            .ToString("HH:mm:ss.fff");

        var sawNewTopology = false;
        lock (_topologyLock)
        {
            foreach (var sample in update.Samples)
            {
                var compositeId = sample.MachineId * 100 + sample.ChannelNo;
                if (!_channelsByCompositeId.TryGetValue(compositeId, out var channel))
                {
                    if (!_devicesByMachineId.TryGetValue(sample.MachineId, out var device))
                    {
                        device = new DeviceItem
                        {
                            MachineId = sample.MachineId,
                            IpAddress = string.Empty,
                            IsOnline = true
                        };

                        _devices.Add(device);
                        _devicesByMachineId[sample.MachineId] = device;
                        sawNewTopology = true;
                    }

                    channel = new ChannelItem
                    {
                        MachineId = sample.MachineId,
                        ChannelNo = sample.ChannelNo,
                        RawChannelId = sample.ChannelNo,
                        IsEnabled = true,
                        IsOnline = true,
                        Description = "回调自动发现"
                    };

                    device.Channels.Add(channel);
                    _channelsByCompositeId[channel.CompositeId] = channel;
                    sawNewTopology = true;
                }

                channel.IsOnline = true;
                channel.LastSeenLocal = DateTime.Now;
                channel.LastValue = sample.Value;
            }

            foreach (var chunk in update.WaveChunks)
            {
                AppendWaveSamples(chunk);
            }
        }

        if (_selectedMachineId < 0 && _devices.Count > 0)
        {
            SelectDevice(_devices[0].MachineId);
            sawNewTopology = true;
        }

        if (sawNewTopology)
        {
            RefreshDeviceListBindings();
            RefreshChannelListBindings();
            UpdateEnabledChannelCount();
        }
        else
        {
            RefreshChannelListBindings();
        }

        RenderResultViews();

        if (update.CallbackCount % 80 == 0)
        {
            AppendResultLog(
                $"回调持续中：块 {update.CallbackCount}，字节 {update.TotalBytes}，本次设备 {update.SourceId}，通道 {update.ChannelCount}");
        }
    }

    private void AppendWaveSamples(ChannelWaveChunk chunk)
    {
        var compositeId = chunk.MachineId * 100 + chunk.ChannelNo;
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

    private void FillWaveDisplaySamples(Queue<double> source, Controls.SkiaWaveformCanvas target)
    {
        var raw = source.ToArray();
        if (raw.Length == 0) return;
        target.AppendSamples(raw, 1000.0 / _detectedSampleRateHz);
    }

    private void InitializeCounters()
    {
        CallbackBlocksText.Text = "0";
        CallbackBytesText.Text = "0";
        LastCallbackText.Text = "-";
    }

    private void StopSdkValidation()
    {
        if (_sdkValidationSource is null)
        {
            return;
        }

        var source = _sdkValidationSource;
        _sdkValidationSource = null;

        try
        {
            source.BlockArrived -= OnValidationBlockArrived;
            source.Stop();
            source.Dispose();
        }
        catch (Exception ex)
        {
            AppendResultLog($"停止验证时出现异常：{ex.Message}");
        }

        // InitializeSdkButtonControl.IsEnabled = true;
        StartValidationBtn.IsEnabled = _sdkInitialized;
        StopValidationBtn.IsEnabled = false;
        StateBadgeText.Text = "空闲";
        SdkStatusText.Text = "已停止验证";
        AppendResultLog("SDK验证已停止。");
    }

    private void CleanupValidationSource()
    {
        if (_sdkValidationSource is null)
        {
            // InitializeSdkButtonControl.IsEnabled = true;
            StartValidationBtn.IsEnabled = _sdkInitialized;
            StopValidationBtn.IsEnabled = false;
            return;
        }

        try
        {
            _sdkValidationSource.BlockArrived -= OnValidationBlockArrived;
            _sdkValidationSource.Stop();
            _sdkValidationSource.Dispose();
        }
        catch
        {
            // Ignore cleanup errors.
        }
        finally
        {
            _sdkValidationSource = null;
            // InitializeSdkButtonControl.IsEnabled = true;
            StartValidationBtn.IsEnabled = _sdkInitialized;
            StopValidationBtn.IsEnabled = false;
        }
    }

    private void OnAddResultViewClick(object? sender, RoutedEventArgs e)
    {
        if (_resultViews.Count >= 64)
        {
            return;
        }

        _resultViews.Add(new ResultViewItem($"视图 {_resultViews.Count + 1}"));
        RebuildResultViewGrid();
        RenderResultViews();
    }

    private void OnRemoveResultViewClick(object? sender, RoutedEventArgs e)
    {
        if (_resultViews.Count <= 1)
        {
            return;
        }

        _resultViews.RemoveAt(_resultViews.Count - 1);
        RebuildResultViewGrid();
        RenderResultViews();
    }

    private void OnResetStatisticsClick(object? sender, RoutedEventArgs e)
    {
        Interlocked.Exchange(ref _callbackBlocks, 0);
        Interlocked.Exchange(ref _callbackBytes, 0);
        Interlocked.Exchange(ref _lastCallbackUnixMs, 0);
        InitializeCounters();

        foreach (var channel in _channelsByCompositeId.Values)
        {
            channel.LastValue = 0;
            channel.LastSeenLocal = null;
            channel.IsOnline = false;
        }

        _channelWaveBuffers.Clear();
        foreach (var view in _resultViews)
        {
            view.Canvas.ClearWaveform();
        }

        RefreshChannelListBindings();
        RenderResultViews();
        AppendResultLog("已重置回调统计与通道预览。");
    }

    private void RenderResultViews()
    {
        if (_resultViews.Count == 0)
        {
            return;
        }

        foreach (var view in _resultViews)
        {
            view.Canvas.ClearWaveform();
        }

        var enabledChannels = _channelsByCompositeId.Values
            .Where(static x => x.IsEnabled)
            .Where(x => _channelWaveBuffers.TryGetValue(x.CompositeId, out var queue) && queue.Count > 1)
            .OrderBy(static x => x.CompositeId)
            .ToList();

        if (enabledChannels.Count == 0)
        {
            enabledChannels = _channelsByCompositeId.Values
                .Where(static x => x.IsEnabled)
                .OrderBy(static x => x.CompositeId)
                .ToList();
        }

        if (enabledChannels.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _resultViews.Count; i++)
        {
            var view = _resultViews[i];
            var waveformChannel = enabledChannels[i % enabledChannels.Count];

            if (_channelWaveBuffers.TryGetValue(waveformChannel.CompositeId, out var waveBuffer) && waveBuffer.Count > 1)
            {
                FillWaveDisplaySamples(waveBuffer, view.Canvas);
            }
        }
    }

    private void InitializeResultViews()
    {
        _resultViews.Clear();
        for (var i = 1; i <= 4; i++)
        {
            _resultViews.Add(new ResultViewItem($"视图 {i}"));
        }

        RebuildResultViewGrid();
    }

    private void BindCollections()
    {
        DeviceListItemsControl.ItemsSource = _devices;
        ChannelListBox.ItemsSource = _visibleChannels;
    }

    private void RebuildResultViewGrid()
    {
        var grid = ResultViewsGridControl;
        grid.Children.Clear();

        var (rows, cols) = CalculateOptimalGrid(_resultViews.Count);
        grid.Rows = rows;
        grid.Columns = cols;

        foreach (var view in _resultViews)
        {
            var waveform = view.Canvas;

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#22444444")),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Margin = new Thickness(4),
                MinWidth = 220,
                MinHeight = 180,
                Child = waveform
            };

            grid.Children.Add(card);
        }
    }

    private static (int Rows, int Columns) CalculateOptimalGrid(int viewCount)
    {
        if (viewCount <= 0)
        {
            return (1, 1);
        }

        var root = (int)Math.Ceiling(Math.Sqrt(viewCount));
        for (var rows = root; rows >= 1; rows--)
        {
            var cols = (int)Math.Ceiling((double)viewCount / rows);
            if (rows * cols >= viewCount && Math.Abs(rows - cols) <= 1)
            {
                return (rows, cols);
            }
        }

        var fallbackRows = root;
        var fallbackCols = (int)Math.Ceiling((double)viewCount / fallbackRows);
        return (fallbackRows, fallbackCols);
    }

    private void RefreshDeviceListBindings()
    {
        DeviceListItemsControl.ItemsSource = null;
        DeviceListItemsControl.ItemsSource = _devices;
    }

    private void RefreshChannelListBindings()
    {
        if (_selectedMachineId >= 0)
        {
            SelectDevice(_selectedMachineId);
        }

        ChannelListBox.ItemsSource = null;
        ChannelListBox.ItemsSource = _visibleChannels;
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

        SelectedDeviceText.Text = "通道列表";
        UpdateEnabledChannelCount();
    }

    private void AppendResultLog(string line)
    {
        if (string.IsNullOrWhiteSpace(LogBox.Text))
        {
            LogBox.Text = line;
            return;
        }

        LogBox.Text += Environment.NewLine + line;
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
        IReadOnlyList<ChannelWaveChunk> WaveChunks,
        int SourceId,
        int ChannelCount);

    private sealed record ChannelValueSample(int MachineId, int ChannelNo, double Value);

    private sealed record ChannelWaveChunk(int MachineId, int ChannelNo, IReadOnlyList<double> Samples);
}

public sealed class DeviceItem
{
    public int MachineId { get; init; }

    public string IpAddress { get; init; } = string.Empty;

    public bool IsOnline { get; set; }

    public bool IsSelected { get; set; }

    public ObservableCollection<ChannelItem> Channels { get; } = new();

    public string DisplayName => IsSelected ? $"AI{MachineId} (当前)" : $"AI{MachineId}";

    public string ChannelSummary => $"{Channels.Count} 路通道";

    public string StateText => IsOnline ? "在线" : "离线";
}

public sealed class ChannelItem
{
    public int MachineId { get; init; }

    public int ChannelNo { get; init; }

    public int RawChannelId { get; init; }

    public bool IsEnabled { get; set; }

    public bool IsOnline { get; set; }

    public double LastValue { get; set; }

    public DateTime? LastSeenLocal { get; set; }

    public string Description { get; init; } = string.Empty;

    public int CompositeId => MachineId * 100 + ChannelNo;

    public string DisplayCode => $"AI{MachineId}-CH{ChannelNo:D2}";

    public string OnlineText => IsOnline ? "在线" : "离线";

    public string LastSeenText => LastSeenLocal.HasValue
        ? $"最近回调: {LastSeenLocal:HH:mm:ss.fff}"
        : "最近回调: -";
}

public sealed class ResultViewItem
{
    public ResultViewItem(string title) { Title = title; }
    public string Title { get; }
    public Controls.SkiaWaveformCanvas Canvas { get; } = new();
}
#endif
