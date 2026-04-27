#if false
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace AcqShell.UI.Controls
{
    public class SkiaWaveformCanvas : Control
    {
        private double _windowDurationMs = 4000d;

        public double WindowDurationMs
        {
            get => _windowDurationMs;
            set
            {
                if (value <= 0) return;
                _windowDurationMs = value;
                ClearWaveform();
            }
        }

        private readonly object _gate = new();
        private readonly List<double> _times = new();
        private readonly List<float> _values = new();

        private double _windowStartMs;
        private double _currentTimeMs;
        private double _lastValue;
        private double? _fixedMin;
        private double? _fixedMax;
        private bool _boundsLocked;

        public event Action<double, double>? WindowRangeChanged;

        public void AppendSamples(IReadOnlyList<double> samples, double sampleIntervalMs)
        {
            if (samples == null || samples.Count == 0) return;
            if (sampleIntervalMs <= 0) sampleIntervalMs = 1;

            lock (_gate)
            {
                double t = _currentTimeMs;

                if (_times.Count == 0)
                {
                    StartNewWindow(t, samples[0]);
                }

                for (int i = 0; i < samples.Count; i++)
                {
                    double elapsed = t - _windowStartMs;
                    if (elapsed >= _windowDurationMs)
                    {
                        if (!_boundsLocked && _values.Count > 0)
                        {
                            _fixedMin = _values.Min();
                            _fixedMax = _values.Max();
                            _boundsLocked = true;
                        }

                        double nextWindowStart = _windowStartMs + _windowDurationMs;
                        StartNewWindow(nextWindowStart, (float)_lastValue);
                        elapsed = t - _windowStartMs;
                    }

                    if (elapsed < 0) elapsed = 0;

                    _times.Add(elapsed);
                    _values.Add((float)samples[i]);
                    _lastValue = samples[i];
                    t += sampleIntervalMs;
                }

                _currentTimeMs = t;
            }

            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }

        private void StartNewWindow(double startMs, double initialVal)
        {
            _windowStartMs = startMs;
            _times.Clear();
            _values.Clear();

            _times.Add(0);
            _values.Add((float)initialVal);
            WindowRangeChanged?.Invoke(_windowStartMs, _windowStartMs + _windowDurationMs);
        }

        public void ClearWaveform()
        {
            lock (_gate)
            {
                _times.Clear();
                _values.Clear();
                _currentTimeMs = 0;
                _windowStartMs = 0;
                _boundsLocked = false;
                _fixedMin = null;
                _fixedMax = null;
            }
            Dispatcher.UIThread.Post(InvalidateVisual);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var bounds = Bounds;
            if (bounds.Width <= 2 || bounds.Height <= 2) return;

            context.FillRectangle(new SolidColorBrush(Color.Parse("#1E1E1E")), bounds);

            double[] tArray;
            float[] vArray;
            double last;
            double? fMin, fMax;

            lock (_gate)
            {
                if (_times.Count < 2) return;

                tArray = _times.ToArray();
                vArray = _values.ToArray();
                last = _lastValue;
                fMin = _fixedMin;
                fMax = _fixedMax;
            }

            context.Custom(new WaveformDrawOp(bounds, _windowStartMs, _windowDurationMs, tArray, vArray, last, fMin, fMax));
        }

        private sealed class WaveformDrawOp : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly double _windowDuration;
            private readonly double[] _times;
            private readonly float[] _values;
            private readonly double _last;
            private readonly double? _fixedMin;
            private readonly double? _fixedMax;

            public Rect Bounds => _bounds;

            public WaveformDrawOp(Rect bounds, double windowStart, double windowDuration, double[] times, float[] values, double last, double? fixedMin, double? fixedMax)
            {
                _bounds = bounds;
                _windowDuration = windowDuration;
                _times = times;
                _values = values;
                _last = last;
                _fixedMin = fixedMin;
                _fixedMax = fixedMax;
            }

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
                if (leaseFeature == null) return;

                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;

                float width = (float)_bounds.Width;
                float height = (float)_bounds.Height;

                using var bgPaint = new SKPaint { Color = SKColor.Parse("#1E1E1E"), Style = SKPaintStyle.Fill };
                canvas.DrawRect(0, 0, width, height, bgPaint);

                using var gridPaint = new SKPaint
                {
                    Color = SKColor.Parse("#33FFFFFF"),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1,
                    IsAntialias = false
                };

                int hSub = 10;
                int vSub = 8;
                for (int i = 1; i < hSub; i++)
                {
                    float x = width * i / hSub;
                    canvas.DrawLine(x, 0, x, height, gridPaint);
                }
                for (int i = 1; i < vSub; i++)
                {
                    float y = height * i / vSub;
                    canvas.DrawLine(0, y, width, y, gridPaint);
                }

                if (_values.Length < 2) return;

                double min = _fixedMin ?? _values.Min();
                double max = _fixedMax ?? _values.Max();

                if (Math.Abs(max - min) < 1e-6)
                {
                    min -= 1;
                    max += 1;
                }

                float range = (float)(max - min);

                using var wavePaint = new SKPaint
                {
                    Color = SKColor.Parse("#00FF00"),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.5f,
                    IsAntialias = true,
                    StrokeJoin = SKStrokeJoin.Round
                };

                int ptCount = _values.Length;
                var pts = new SKPoint[ptCount];

                for (int i = 0; i < ptCount; i++)
                {
                    float x = (float)(_times[i] / _windowDuration * width);
                    float y = height - (float)((_values[i] - min) / range * height);
                    pts[i] = new SKPoint(x, y);
                }

                using var path = new SKPath();
                path.MoveTo(pts[0]);
                for (int i = 1; i < ptCount; i++)
                {
                    path.LineTo(pts[i]);
                }
                canvas.DrawPath(path, wavePaint);

                float lx = pts[ptCount - 1].X;
                float ly = pts[ptCount - 1].Y;
                using var currentPointPaint = new SKPaint
                {
                    Color = SKColor.Parse("#FFFF00"),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                canvas.DrawCircle(lx, ly, 3f, currentPointPaint);
            }

            public bool HitTest(Point p) => _bounds.Contains(p);
            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
        }
    }
}
#endif

using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AcqShell.UI.Controls;

public sealed record WaveformPoint(double OffsetMs, double Value);

public sealed record WaveformSeries(string Label, IReadOnlyList<WaveformPoint> Samples, Color Color);

public class SkiaWaveformCanvas : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#F3F7FB"));
    private static readonly IBrush PlotBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.Parse("#C7D5E5"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#DDE7F2"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#0F172A"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly Pen BorderPen = new(BorderBrush, 1);
    private static readonly Pen GridPen = new(GridBrush, 1);

    private readonly object _gate = new();
    private WaveformSeries[] _series = Array.Empty<WaveformSeries>();
    private double _sampleIntervalMs = 1d;
    private double _windowDurationMs = 2000d;
    private string _overlayLabel = "未选择通道";
    private string _placeholderText = "等待数据";

    public event Action<double, double>? WindowRangeChanged;

    public void SetWaveform(IReadOnlyList<double> samples, double sampleIntervalMs, double windowDurationMs, string channelLabel)
    {
        var points = new WaveformPoint[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            points[index] = new WaveformPoint(index * sampleIntervalMs, samples[index]);
        }

        SetWaveforms(
        [
            new WaveformSeries(channelLabel, points, Color.Parse("#22C55E"))
        ],
        sampleIntervalMs,
        windowDurationMs,
        channelLabel);
    }

    public void SetWaveforms(IReadOnlyList<WaveformSeries> series, double sampleIntervalMs, double windowDurationMs, string overlayLabel)
    {
        lock (_gate)
        {
            _series = series
                .Select(static item => new WaveformSeries(item.Label, item.Samples.ToArray(), item.Color))
                .ToArray();
            _sampleIntervalMs = Math.Max(0.001d, sampleIntervalMs);
            _windowDurationMs = Math.Max(_sampleIntervalMs, windowDurationMs);
            _overlayLabel = overlayLabel;
            _placeholderText = string.Empty;
        }

        WindowRangeChanged?.Invoke(0d, _windowDurationMs);
        InvalidateVisual();
    }

    public void SetPlaceholder(string message)
    {
        lock (_gate)
        {
            _series = Array.Empty<WaveformSeries>();
            _placeholderText = message;
        }

        WindowRangeChanged?.Invoke(0d, _windowDurationMs);
        InvalidateVisual();
    }

    public void ClearWaveform()
    {
        SetPlaceholder("等待数据");
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        WaveformSeries[] series;
        double sampleIntervalMs;
        double windowDurationMs;
        string overlayLabel;
        string placeholderText;

        lock (_gate)
        {
            series = _series
                .Select(static item => new WaveformSeries(item.Label, item.Samples.ToArray(), item.Color))
                .ToArray();
            sampleIntervalMs = _sampleIntervalMs;
            windowDurationMs = _windowDurationMs;
            overlayLabel = _overlayLabel;
            placeholderText = _placeholderText;
        }

        context.FillRectangle(BackgroundBrush, bounds);

        var plotRect = new Rect(14, 44, Math.Max(0, bounds.Width - 28), Math.Max(0, bounds.Height - 60));
        context.FillRectangle(PlotBrush, plotRect);
        context.DrawRectangle(null, BorderPen, plotRect);
        DrawGrid(context, plotRect);
        DrawOverlay(context, plotRect, overlayLabel, sampleIntervalMs, windowDurationMs);

        var drawableSeries = series
            .Where(static item => item.Samples.Count >= 2)
            .ToList();

        if (drawableSeries.Count == 0)
        {
            DrawCenteredText(context, plotRect, placeholderText);
            return;
        }

        DrawWaveforms(context, plotRect, drawableSeries);
    }

    private static void DrawGrid(DrawingContext context, Rect plotRect)
    {
        for (var column = 1; column < 10; column++)
        {
            var x = plotRect.X + plotRect.Width * column / 10d;
            context.DrawLine(GridPen, new Point(x, plotRect.Y), new Point(x, plotRect.Bottom));
        }

        for (var row = 1; row < 6; row++)
        {
            var y = plotRect.Y + plotRect.Height * row / 6d;
            context.DrawLine(GridPen, new Point(plotRect.X, y), new Point(plotRect.Right, y));
        }
    }

    private static void DrawCenteredText(DrawingContext context, Rect plotRect, string message)
    {
        var text = new FormattedText(
            message,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            16,
            MutedBrush);

        var location = new Point(
            plotRect.X + (plotRect.Width - text.Width) / 2d,
            plotRect.Y + (plotRect.Height - text.Height) / 2d);

        context.DrawText(text, location);
    }

    private static void DrawOverlay(DrawingContext context, Rect plotRect, string overlayLabel, double sampleIntervalMs, double windowDurationMs)
    {
        var header = new FormattedText(
            overlayLabel,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            15,
            AccentBrush);
        context.DrawText(header, new Point(plotRect.X, 12));

        var meta = new FormattedText(
            $"显示窗口 {windowDurationMs / 1000d:0.##} 秒 · 显示步长 {sampleIntervalMs:0.###} 毫秒",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            MutedBrush);
        context.DrawText(meta, new Point(plotRect.X, 28));
    }

    private static void DrawWaveforms(DrawingContext context, Rect plotRect, IReadOnlyList<WaveformSeries> series)
    {
        var min = series.Min(static item => item.Samples.Min(static point => point.Value));
        var max = series.Max(static item => item.Samples.Max(static point => point.Value));
        if (Math.Abs(max - min) < 1e-9d)
        {
            min -= 1d;
            max += 1d;
        }

        foreach (var item in series)
        {
            var points = BuildPlotPoints(plotRect, item.Samples, min, max);
            if (points.Count < 2)
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var figure = geometry.Open())
            {
                for (var index = 0; index < points.Count; index++)
                {
                    var point = points[index];

                    if (index == 0)
                    {
                        figure.BeginFigure(point, false);
                    }
                    else
                    {
                        figure.LineTo(point);
                    }
                }

                figure.EndFigure(false);
            }

            var strokeBrush = new SolidColorBrush(item.Color);
            var wavePen = new Pen(strokeBrush, 2);
            context.DrawGeometry(null, wavePen, geometry);
            context.DrawEllipse(strokeBrush, null, points[^1], 2.5d, 2.5d);
        }
    }

    private static List<Point> BuildPlotPoints(Rect plotRect, IReadOnlyList<WaveformPoint> samples, double min, double max)
    {
        var points = new List<Point>(samples.Count);
        var maxOffsetMs = Math.Max(1d, samples[^1].OffsetMs);

        for (var index = 0; index < samples.Count; index++)
        {
            var x = plotRect.X + plotRect.Width * samples[index].OffsetMs / maxOffsetMs;
            var normalized = (samples[index].Value - min) / (max - min);
            var y = plotRect.Bottom - normalized * plotRect.Height;
            points.Add(new Point(x, y));
        }

        return points;
    }
}

