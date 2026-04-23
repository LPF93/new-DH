using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AcqShell.UI;

public abstract class BindableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class DeviceItem : BindableObject
{
    private bool _isOnline;
    private bool _isSelected;

    public DeviceItem(int machineId, string ipAddress)
    {
        MachineId = machineId;
        IpAddress = ipAddress;
    }

    public int MachineId { get; }

    public string IpAddress { get; }

    public ObservableCollection<ChannelItem> Channels { get; } = new();

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetField(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBadgeBackground));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(CardBackground));
                OnPropertyChanged(nameof(CardBorderBrush));
                OnPropertyChanged(nameof(SelectionText));
            }
        }
    }

    public string DisplayName => $"AI{MachineId}";

    public string Subtitle => string.IsNullOrWhiteSpace(IpAddress)
        ? $"{Channels.Count} 个通道"
        : $"{IpAddress} · {Channels.Count} 个通道";

    public string StateText => IsOnline ? "在线" : "离线";

    public Avalonia.Media.IBrush StateBadgeBackground => new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.Parse(IsOnline ? "#0F766E" : "#94A3B8"));

    public Avalonia.Media.IBrush CardBackground => new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.Parse(IsSelected ? "#DBEAFE" : "#FFFFFF"));

    public Avalonia.Media.IBrush CardBorderBrush => new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.Parse(IsSelected ? "#2563EB" : "#D7E2F0"));

    public string SelectionText => IsSelected ? "当前设备" : "点击切换";

    public void NotifyChannelSummaryChanged()
    {
        OnPropertyChanged(nameof(Subtitle));
    }
}

public sealed class ChannelItem : BindableObject
{
    private bool _isEnabled;
    private bool _isOnline;
    private double _lastValue;
    private DateTime? _lastSeenLocal;

    public ChannelItem(int machineId, int channelNo, int rawChannelId, string description)
    {
        MachineId = machineId;
        ChannelNo = channelNo;
        RawChannelId = rawChannelId;
        Description = description;
    }

    public int MachineId { get; }

    public int ChannelNo { get; }

    public int RawChannelId { get; }

    public string Description { get; }

    public long CompositeId => ((long)MachineId << 32) | (uint)ChannelNo;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetField(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(CardBorderBrush));
            }
        }
    }

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetField(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(OnlineText));
                OnPropertyChanged(nameof(StatusBadgeBackground));
            }
        }
    }

    public double LastValue
    {
        get => _lastValue;
        set
        {
            if (SetField(ref _lastValue, value))
            {
                OnPropertyChanged(nameof(LastValueText));
            }
        }
    }

    public DateTime? LastSeenLocal
    {
        get => _lastSeenLocal;
        set
        {
            if (SetField(ref _lastSeenLocal, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
            }
        }
    }

    public string DisplayCode => $"AI{MachineId}-通道{ChannelNo:D2}";

    public string DescriptionText => RawChannelId > 0
        ? $"{Description} · 原始编号 {RawChannelId}"
        : Description;

    public string OnlineText => IsOnline ? "在线" : "离线";

    public Avalonia.Media.IBrush StatusBadgeBackground => new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.Parse(IsOnline ? "#0F766E" : "#64748B"));

    public Avalonia.Media.IBrush CardBorderBrush => new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.Parse(IsEnabled ? "#1D4ED8" : "#D7E2F0"));

    public string LastValueText => $"最新值 {LastValue:F4}";

    public string LastSeenText => LastSeenLocal.HasValue
        ? $"最近回调 {LastSeenLocal:HH:mm:ss.fff}"
        : "暂无回调";
}

public sealed class ResultViewItem
{
    private static readonly Avalonia.Media.Color[] Palette =
    [
        Avalonia.Media.Color.Parse("#22C55E"),
        Avalonia.Media.Color.Parse("#38BDF8"),
        Avalonia.Media.Color.Parse("#F97316"),
        Avalonia.Media.Color.Parse("#A78BFA"),
        Avalonia.Media.Color.Parse("#F43F5E"),
        Avalonia.Media.Color.Parse("#FACC15"),
        Avalonia.Media.Color.Parse("#14B8A6"),
        Avalonia.Media.Color.Parse("#FB7185")
    ];

    private readonly Dictionary<long, ChannelItem> _availableChannels = new();
    private readonly List<long> _selectedChannelIds = new();
    private readonly StackPanel _channelOptionsPanel;
    private readonly WrapPanel _legendPanel;

    public ResultViewItem(string title)
    {
        Title = title;
        TitleText = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0F172A"))
        };
        SubtitleText = new TextBlock
        {
            Text = "等待数据",
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#526581"))
        };
        SelectionSummaryText = new TextBlock
        {
            Text = "当前未选择通道",
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2563EB"))
        };

        Canvas = new Controls.SkiaWaveformCanvas
        {
            Height = 260
        };

        _channelOptionsPanel = new StackPanel
        {
            Spacing = 6
        };

        _legendPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            IsVisible = false
        };

        var selectAllButton = new Button
        {
            Content = "全选已启用",
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1D4ED8")),
            Foreground = Avalonia.Media.Brushes.White
        };
        selectAllButton.Click += (_, _) => SelectAllChannels();

        var clearButton = new Button
        {
            Content = "清空选择",
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#64748B")),
            Foreground = Avalonia.Media.Brushes.White
        };
        clearButton.Click += (_, _) => ClearSelectedChannels();

        ChannelSelectorExpander = new Expander
        {
            Header = new TextBlock
            {
                Text = "选择通道",
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1F2937"))
            },
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            selectAllButton,
                            clearButton
                        }
                    },
                    new ScrollViewer
                    {
                        MaxHeight = 170,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = _channelOptionsPanel
                    }
                }
            }
        };

        Card = new Border
        {
            Width = 620,
            MinWidth = 560,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF")),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D7E2F0")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(14),
            Padding = new Avalonia.Thickness(14),
            Margin = new Avalonia.Thickness(6),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    TitleText,
                    SubtitleText,
                    SelectionSummaryText,
                    ChannelSelectorExpander,
                    _legendPanel,
                    Canvas
                }
            }
        };

        UpdateSelectionSummary();
        RebuildChannelOptions();
    }

    public event Action<ResultViewItem>? SelectionChanged;

    public string Title { get; }

    public TextBlock TitleText { get; }

    public TextBlock SubtitleText { get; }

    public TextBlock SelectionSummaryText { get; }

    public Expander ChannelSelectorExpander { get; }

    public Controls.SkiaWaveformCanvas Canvas { get; }

    public Border Card { get; }

    public bool HasManualSelection { get; private set; }

    public int SelectedChannelCount => _selectedChannelIds.Count;

    public static Avalonia.Media.Color GetPaletteColor(int index)
    {
        return Palette[index % Palette.Length];
    }

    public void SyncSelectableChannels(IReadOnlyList<ChannelItem> channels)
    {
        var orderedChannels = channels
            .OrderBy(static channel => channel.CompositeId)
            .ToList();

        var changed = orderedChannels.Count != _availableChannels.Count ||
                      orderedChannels.Any(channel => !_availableChannels.ContainsKey(channel.CompositeId));

        _availableChannels.Clear();
        foreach (var channel in orderedChannels)
        {
            _availableChannels[channel.CompositeId] = channel;
        }

        if (_selectedChannelIds.RemoveAll(compositeId => !_availableChannels.ContainsKey(compositeId)) > 0)
        {
            changed = true;
        }

        if (changed)
        {
            RebuildChannelOptions();
        }

        UpdateSelectionSummary();
    }

    public void SetSelectedChannels(IEnumerable<long> compositeIds, bool markAsManual, bool raiseEvent = false)
    {
        var target = compositeIds
            .Distinct()
            .Where(compositeId => _availableChannels.ContainsKey(compositeId))
            .ToList();

        if (_selectedChannelIds.SequenceEqual(target))
        {
            if (markAsManual)
            {
                HasManualSelection = true;
            }

            UpdateSelectionSummary();
            return;
        }

        _selectedChannelIds.Clear();
        _selectedChannelIds.AddRange(target);

        if (markAsManual)
        {
            HasManualSelection = true;
        }

        RebuildChannelOptions();
        UpdateSelectionSummary();

        if (raiseEvent)
        {
            SelectionChanged?.Invoke(this);
        }
    }

    public IReadOnlyList<ChannelItem> GetSelectedChannels()
    {
        var result = new List<ChannelItem>(_selectedChannelIds.Count);
        foreach (var compositeId in _selectedChannelIds)
        {
            if (_availableChannels.TryGetValue(compositeId, out var channel))
            {
                result.Add(channel);
            }
        }

        return result;
    }

    public void SetLegend(IReadOnlyList<(string Label, Avalonia.Media.Color Color)> entries)
    {
        _legendPanel.Children.Clear();
        _legendPanel.IsVisible = entries.Count > 0;

        foreach (var entry in entries)
        {
            var chip = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F5F9FF")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D7E2F0")),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(999),
                Padding = new Avalonia.Thickness(10, 4),
                Margin = new Avalonia.Thickness(0, 0, 8, 8),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new Border
                        {
                            Width = 10,
                            Height = 10,
                            CornerRadius = new Avalonia.CornerRadius(999),
                            Background = new Avalonia.Media.SolidColorBrush(entry.Color),
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = entry.Label,
                            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1F2937")),
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };

            _legendPanel.Children.Add(chip);
        }
    }

    private void SelectAllChannels()
    {
        SetSelectedChannels(_availableChannels.Keys, true, true);
    }

    private void ClearSelectedChannels()
    {
        SetSelectedChannels(Array.Empty<long>(), true, true);
    }

    private void SetChannelSelected(long compositeId, bool isSelected, bool markAsManual)
    {
        if (isSelected)
        {
            if (_selectedChannelIds.Contains(compositeId))
            {
                return;
            }

            _selectedChannelIds.Add(compositeId);
        }
        else if (!_selectedChannelIds.Remove(compositeId))
        {
            return;
        }

        if (markAsManual)
        {
            HasManualSelection = true;
        }

        RebuildChannelOptions();
        UpdateSelectionSummary();
        SelectionChanged?.Invoke(this);
    }

    private void RebuildChannelOptions()
    {
        _channelOptionsPanel.Children.Clear();

        if (_availableChannels.Count == 0)
        {
            _channelOptionsPanel.Children.Add(new TextBlock
            {
                Text = "当前没有可选通道",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#526581"))
            });
            return;
        }

        foreach (var channel in _availableChannels.Values.OrderBy(static channel => channel.CompositeId))
        {
            var selectedIndex = _selectedChannelIds.IndexOf(channel.CompositeId);
            var swatchBrush = new Avalonia.Media.SolidColorBrush(
                selectedIndex >= 0 ? GetPaletteColor(selectedIndex) : Avalonia.Media.Color.Parse("#94A3B8"));

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 10,
                        Height = 10,
                        CornerRadius = new Avalonia.CornerRadius(999),
                        Background = swatchBrush,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Avalonia.Thickness(0, 4, 0, 0)
                    },
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = channel.DisplayCode,
                                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F5F9FF"))
                            },
                            new TextBlock
                            {
                                Text = channel.DescriptionText,
                                FontSize = 11,
                                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#526581")),
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap
                            }
                        }
                    }
                }
            };

            var checkBox = new CheckBox
            {
                IsChecked = selectedIndex >= 0,
                Content = content
            };
            checkBox.IsCheckedChanged += (_, _) => SetChannelSelected(channel.CompositeId, checkBox.IsChecked == true, true);

            _channelOptionsPanel.Children.Add(checkBox);
        }
    }

    private void UpdateSelectionSummary()
    {
        if (_availableChannels.Count == 0)
        {
            SelectionSummaryText.Text = "当前没有可用通道";
            return;
        }

        if (_selectedChannelIds.Count == 0)
        {
            SelectionSummaryText.Text = "当前未选择通道";
            return;
        }

        var selectedNames = GetSelectedChannels()
            .Select(static channel => channel.DisplayCode)
            .Take(3)
            .ToList();

        var suffix = _selectedChannelIds.Count > selectedNames.Count ? " 等" : string.Empty;
        SelectionSummaryText.Text = $"已选 {_selectedChannelIds.Count} 个通道：{string.Join("、", selectedNames)}{suffix}";
    }
}

public sealed class StorageSegmentItem : BindableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _path = string.Empty;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetField(ref _subtitle, value);
    }

    public string Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }
}

