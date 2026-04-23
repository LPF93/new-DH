$code = @'
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using AcqEngine.Core;
using AcqEngine.DeviceSdk;
using AcqShell.UI.Controls;

namespace AcqShell.UI
{
    public class DeviceItem
    {
        public int Index { get; set; }
        public DeviceInfo Info { get; set; } = new();
    }

    public class ChannelItem
    {
        public int PhysicalIndex { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public int CompositeId { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class ChannelResultView
    {
        public int CompositeId { get; set; }
        public string ChannelName { get; set; } = "";
        public SkiaWaveformCanvas Canvas { get; set; } = default!;
        public TextBlock VppText { get; set; } = default!;
        public TextBlock AvgText { get; set; } = default!;
    }

    public partial class MainWindow : Window
    {
        private bool _useNativeMode = true;
        private IDescriptorAcquisitionSource? _sdkValidationSource;
        private long _callbackBlocks;
        private long _callbackBytes;
        private long _lastCallbackUnixMs;

        private readonly ObservableCollection<DeviceItem> _devices = new();
        private readonly ObservableCollection<ChannelItem> _visibleChannels = new();
        private readonly Dictionary<int, ChannelItem> _channelsByCompositeId = new();
        private readonly Dictionary<int, ChannelResultView> _resultViewsByCompositeId = new();

        public MainWindow()
        {
            InitializeComponent();
            DeviceListItemsControl.ItemsSource = _devices;
            ChannelListBox.ItemsSource = _visibleChannels;
            
            SdkDirectoryTextBox.Text = @"C:\MockSdk";
            ConfigDirectoryTextBox.Text = @"C:\MockSdk\DevCfg";
            
            UpdateStateBadge("待命", Brushes.Green);
        }
        
        private void UpdateStateBadge(string text, IBrush color)
        {
            Dispatcher.UIThread.Post(() => {
                StateBadgeText.Text = text;
                StateBadgeText.Foreground = color;
            });
        }

        private void AppendLog(string message)
        {
            Dispatcher.UIThread.Post(() => {
                LogBox.Text += $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
                LogBox.CaretIndex = LogBox.Text.Length;
            });
        }

        private void OnModeChanged(object? sender, RoutedEventArgs e)
        {
            if (NativeModeRadio?.IsChecked == true)
            {
                _useNativeMode = true;
                NativeConfigPanel.IsVisible = true;
                MockConfigPanel.IsVisible = false;
                AppendLog("切换到 设备 SDK 模式");
            }
            else
            {
                _useNativeMode = false;
                NativeConfigPanel.IsVisible = false;
                MockConfigPanel.IsVisible = true;
                AppendLog("切换到 模拟生成模式");
            }
        }

        private async void OnBrowseSdkDirectoryClick(object? sender, RoutedEventArgs e)
        {
            var storage = GetTopLevel(this)!.StorageProvider;
            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择 SDK 目录" });
            if (result.Count > 0)
            {
                SdkDirectoryTextBox.Text = result[0].Path.LocalPath;
            }
        }

        private async void OnBrowseConfigDirectoryClick(object? sender, RoutedEventArgs e)
        {
            var storage = GetTopLevel(this)!.StorageProvider;
            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择配置目录" });
            if (result.Count > 0)
            {
                ConfigDirectoryTextBox.Text = result[0].Path.LocalPath;
            }
        }

        private void OnRefreshTopologyClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                _devices.Clear();
                _visibleChannels.Clear();
                _channelsByCompositeId.Clear();

                if (_useNativeMode)
                {
                    if (string.IsNullOrWhiteSpace(SdkDirectoryTextBox.Text))
                    {
                        AppendLog("请提供 SDK 目录");
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(ConfigDirectoryTextBox.Text))
                    {
                        AppendLog("请提供 配置 目录");
                        return;
                    }
                    
                    var factory = new NativeAcquisitionSourceFactory();
                    factory.InitializeSdk(SdkDirectoryTextBox.Text, ConfigDirectoryTextBox.Text);
                    var topology = factory.DiscoverTopology();
                    
                    SdkStatusText.Text = "SDK 已初始化并发现设备";
                    SdkStatusText.Foreground = Brushes.Green;
                    
                    foreach (var dev in topology.Machines)
                    {
                        _devices.Add(new DeviceItem { Index = dev.Id, Info = dev });
                        
                        SelectedDeviceText.Text = $"当前设备: {dev.DeviceId}";
                        
                        foreach (var ch in dev.Channels)
                        {
                            var item = new ChannelItem
                            {
                                PhysicalIndex = ch.PhysicalIndex,
                                Name = $"CH{ch.PhysicalIndex}",
                                Status = "就绪",
                                CompositeId = (dev.Id << 5) | ch.PhysicalIndex
                            };
                            _visibleChannels.Add(item);
                            _channelsByCompositeId[item.CompositeId] = item;
                        }
                    }
                    TopologyStatusText.Text = $"发现 {_devices.Count} 个设备, 共 {_visibleChannels.Count} 个通道";
                    AppendLog("刷新原生设备拓扑成功。");
                }
                else
                {
                    SdkStatusText.Text = "Mock 模式";
                    SdkStatusText.Foreground = Brushes.Blue;
                    
                    int devCount = int.Parse(MockDeviceCountTextBox.Text!);
                    int chCount = int.Parse(MockChannelCountTextBox.Text!);
                    
                    for (int m = 0; m < devCount; m++)
                    {
                        _devices.Add(new DeviceItem { Index = m, Info = new DeviceInfo { DeviceId = $"MockDev-{m}", Protocol = "Pcap" } });
                        if (m == 0) SelectedDeviceText.Text = $"当前设备: MockDev-0";
                        
                        for (int c = 0; c < chCount; c++)
                        {
                            var item = new ChannelItem
                            {
                                PhysicalIndex = c,
                                Name = $"MockCH{c}",
                                Status = "模拟",
                                CompositeId = (m << 5) | c
                            };
                            _visibleChannels.Add(item);
                            _channelsByCompositeId[item.CompositeId] = item;
                        }
                    }
                    TopologyStatusText.Text = $"生成了 {devCount} 个模拟设备, 共 {_visibleChannels.Count} 个通道";
                    AppendLog("刷新模拟拓扑成功。");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"刷新失败: {ex.Message}");
                TopologyStatusText.Text = "拓扑发现失败";
            }
        }

        private void OnChannelSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            int selCount = ChannelListBox.SelectedItems?.Count ?? 0;
            SelectedChannelsSummaryText.Text = $"已选 {selCount} 个通道";
            
            foreach (ChannelItem item in _visibleChannels)
            {
                item.IsEnabled = false;
            }
            
            if (ChannelListBox.SelectedItems != null)
            {
                foreach (ChannelItem item in ChannelListBox.SelectedItems)
                {
                    item.IsEnabled = true;
                }
            }
        }

        private void RebuildResultViews()
        {
            ResultViewsStackPanel.Children.Clear();
            _resultViewsByCompositeId.Clear();
            
            foreach (var kvp in _channelsByCompositeId)
            {
                if (!kvp.Value.IsEnabled) continue;
                
                var item = kvp.Value;
                
                var canvas = new SkiaWaveformCanvas 
                { 
                    Margin = new Thickness(10, 0),
                    ClipToBounds = true
                };
                
                var vppStr = new TextBlock { Text = "Vpp: --", Foreground = Brushes.Gray, FontSize=11 };
                var avgStr = new TextBlock { Text = "Avg: --", Foreground = Brushes.Gray, FontSize=11 };
                
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#444")),
                    BorderThickness = new Thickness(0,0,0,1),
                    Height = 200,
                    Padding = new Thickness(10)
                };
                
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("50, *, 80")
                };
                
                var infoPanel = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                infoPanel.Children.Add(new TextBlock { Text = item.Name, Foreground = Brushes.White, FontWeight = Avalonia.Media.FontWeight.Bold });
                infoPanel.Children.Add(new TextBlock { Text = $"CH{item.PhysicalIndex}", Foreground = Brushes.Gray, FontSize=11 });
                
                var statPanel = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                statPanel.Children.Add(vppStr);
                statPanel.Children.Add(avgStr);
                
                Grid.SetColumn(infoPanel, 0);
                Grid.SetColumn(canvas, 1);
                Grid.SetColumn(statPanel, 2);
                
                grid.Children.Add(infoPanel);
                grid.Children.Add(canvas);
                grid.Children.Add(statPanel);
                
                border.Child = grid;
                ResultViewsStackPanel.Children.Add(border);
                
                _resultViewsByCompositeId[item.CompositeId] = new ChannelResultView
                {
                    CompositeId = item.CompositeId,
                    ChannelName = item.Name,
                    Canvas = canvas,
                    VppText = vppStr,
                    AvgText = avgStr
                };
            }
        }

        private void OnWaveWindowChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (WaveWindowComboBox?.SelectedItem is ComboBoxItem cbi)
            {
                var content = cbi.Content?.ToString();
                if (content != null)
                {
                    int s = int.Parse(content.Replace("秒", ""));
                    // Optional: loop over _resultViewsByCompositeId and tell them the X scale changed
                    AppendLog($"显示窗口调整为 {s} 秒");
                }
            }
        }

        private async void OnStartSdkValidationClick(object? sender, RoutedEventArgs e)
        {
            if (_sdkValidationSource != null)
            {
                AppendLog("采集已在运行。");
                return;
            }
            
            var enabled = _channelsByCompositeId.Values.Where(c => c.IsEnabled).ToList();
            if (enabled.Count == 0)
            {
                AppendLog("请先选择要采集的通道。");
                return;
            }

            try
            {
                RebuildResultViews();
            
                var config = new AcqSystemConfig { SampleRateHz = 1000, MaxHardwareChannels = enabled.Max(c => c.PhysicalIndex)+1 };
                var enabledChannels = enabled.Select(c => new AcqChannelConfig { Id = c.CompositeId, PhysicalIndex = c.PhysicalIndex, Name = c.Name }).ToList();
                config.Channels = enabledChannels;
                
                if (_useNativeMode)
                {
                    var factory = new NativeAcquisitionSourceFactory();
                    _sdkValidationSource = factory.CreateSource(config);
                }
                else
                {
                    var factory = new MockAcquisitionSourceFactory();
                    _sdkValidationSource = factory.CreateSource(config);
                }
                
                _sdkValidationSource.DataArrived += OnDataArrived;
                await _sdkValidationSource.StartAsync();
                
                _callbackBlocks = 0;
                _callbackBytes = 0;
                
                UpdateStateBadge("采集中...", Brushes.Red);
                StartValidationBtn.IsEnabled = false;
                StopValidationBtn.IsEnabled = true;
                AppendLog("开始采集。");
            }
            catch (Exception ex)
            {
                AppendLog($"采集启动失败: {ex.Message}");
                _sdkValidationSource = null;
            }
        }

        private async void OnStopSdkValidationClick(object? sender, RoutedEventArgs e)
        {
            if (_sdkValidationSource != null)
            {
                _sdkValidationSource.DataArrived -= OnDataArrived;
                await _sdkValidationSource.StopAsync();
                _sdkValidationSource.Dispose();
                _sdkValidationSource = null;
                
                UpdateStateBadge("已停止", Brushes.Gray);
                StartValidationBtn.IsEnabled = true;
                StopValidationBtn.IsEnabled = false;
                AppendLog("采集已停止。");
            }
        }

        private void OnDataArrived(object? sender, DataBlock e)
        {
            _callbackBlocks++;
            _callbackBytes += e.Data.Length * 4;
            
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            long timeSpanMs = e.Timestamp / 10000; 
            
            foreach (var chunk in e.ParseWaveChunks())
            {
                int compId = chunk.ChannelId;
                if (_resultViewsByCompositeId.TryGetValue(compId, out var view))
                {
                    Dispatcher.UIThread.Post(() => {
                        view.Canvas.SampleRate = 1000;
                        view.Canvas.AddSamples(chunk.Samples.ToArray());
                    });
                }
            }

            if (nowUnix - _lastCallbackUnixMs > 250)
            {
                _lastCallbackUnixMs = nowUnix;
                Dispatcher.UIThread.Post(() => {
                    CallbackBlocksText.Text = $"块数: {_callbackBlocks}";
                    CallbackBytesText.Text = $"字节: {_callbackBytes}";
                    LastCallbackText.Text = $"时间: {DateTime.Now:HH:mm:ss}";
                });
            }
        }
    }
}
'@
Set-Content "src\AcqShell.UI\MainWindow.axaml.cs" $code -Encoding UTF8
