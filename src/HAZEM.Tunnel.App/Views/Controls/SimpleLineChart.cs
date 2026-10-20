using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HAZEM.Tunnel.App.Views.Controls;

/// <summary>
/// Dependency-free dual line chart (sent cyan / received green). Peak-normalized,
/// no third-party chart library, redraws on data change. Zero synthetic smoothing.
/// </summary>
public sealed class SimpleLineChart : FrameworkElement
{
    public static readonly System.Windows.DependencyProperty SeriesSentProperty =
        System.Windows.DependencyProperty.Register(nameof(SeriesSent), typeof(IReadOnlyList<double>),
            typeof(SimpleLineChart), new PropertyMetadata(Array.Empty<double>(), OnDataChanged));

    public static readonly System.Windows.DependencyProperty SeriesRecvProperty =
        System.Windows.DependencyProperty.Register(nameof(SeriesRecv), typeof(IReadOnlyList<double>),
            typeof(SimpleLineChart), new PropertyMetadata(Array.Empty<double>(), OnDataChanged));

    public IReadOnlyList<double> SeriesSent
    {
        get => (IReadOnlyList<double>)GetValue(SeriesSentProperty);
        set => SetValue(SeriesSentProperty, value);
    }

    public IReadOnlyList<double> SeriesRecv
    {
        get => (IReadOnlyList<double>)GetValue(SeriesRecvProperty);
        set => SetValue(SeriesRecvProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SimpleLineChart)d).InvalidateVisual();

    public SimpleLineChart()
    {
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Background grid
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x24, 0x33, 0x50)), 1);
        gridPen.Freeze();
        for (var i = 1; i < 4; i++)
        {
            var y = h * i / 4.0;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        var max = Math.Max(1.0, Math.Max(Peak(SeriesSent), Peak(SeriesRecv)));
        DrawSeries(dc, SeriesSent, Color.FromRgb(0x22, 0xD3, 0xEE), w, h, max);
        DrawSeries(dc, SeriesRecv, Color.FromRgb(0x34, 0xD3, 0x99), w, h, max);

        // Legend + scale hint
        var ft = new FormattedText("█ sent   █ received   peak " + Format(max),
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, new SolidColorBrush(Color.FromRgb(0x8C, 0xA2, 0xC0)), 1.25);
        dc.DrawText(ft, new Point(4, h - ft.Height - 2));
    }

    private static double Peak(IReadOnlyList<double> series) =>
        series.Count == 0 ? 0 : series.Max();

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double> series, Color color, double w, double h, double max)
    {
        if (series.Count < 2) return;
        var step = w / (series.Count - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var started = false;
            for (var i = 0; i < series.Count; i++)
            {
                var x = i * step;
                var y = h - 18 - (series[i] / max) * (h - 30);
                if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 1.6);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static string Format(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1L << 30 => $"{bytesPerSecond / (1L << 30):F1} GB/s",
        >= 1L << 20 => $"{bytesPerSecond / (1L << 20):F1} MB/s",
        >= 1L << 10 => $"{bytesPerSecond / (1L << 10):F1} KB/s",
        _ => $"{bytesPerSecond:0} B/s"
    };
}
