namespace Platform.Client.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// 轻量趋势图（总览趋势 / 点位右栏 / 大屏曲线）：4 条水平网格 + 归一化面积线 + Y 轴等宽刻度。
/// 只依赖 OnRender：单实例每秒重绘，不建可视化树。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesBrushProperty = DependencyProperty.Register(
        nameof(SeriesBrush), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowAreaFillProperty = DependencyProperty.Register(
        nameof(ShowAreaFill), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? SeriesBrush
    {
        get => (Brush?)GetValue(SeriesBrushProperty);
        set => SetValue(SeriesBrushProperty, value);
    }

    public bool ShowAreaFill
    {
        get => (bool)GetValue(ShowAreaFillProperty);
        set => SetValue(ShowAreaFillProperty, value);
    }

    private static readonly Typeface MonoTypeface =
        new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        const double leftMargin = 44;    // Y 轴标签区
        const double bottomMargin = 14;  // X 轴标签区
        var plotWidth = Math.Max(width - leftMargin - 4, 1);
        var plotHeight = Math.Max(height - bottomMargin - 6, 1);
        var seriesBrush = SeriesBrush ?? Brushes.Transparent;

        // 网格（5 行）
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x25, 0x31, 0x3C)), 1);
        for (var lineIndex = 0; lineIndex <= 4; lineIndex++)
        {
            var y = 6 + plotHeight * lineIndex / 4;
            drawingContext.DrawLine(gridPen, new Point(leftMargin, y), new Point(leftMargin + plotWidth, y));
        }

        var values = Values;
        if (values is not { Length: >= 2 })
        {
            DrawAxisLabel(drawingContext, "--", 4, 6);
            return;
        }

        double min = values.Min();
        double max = values.Max();
        if (Math.Abs(max - min) < 1e-9)
        {
            max = min + 1;   // 恒定值：避免除零
            min -= 0.5;
        }

        var pen = new Pen(seriesBrush, 1.6);
        var geometry = new StreamGeometry();
        var stepX = plotWidth / (values.Length - 1);
        double PointY(double value) => 6 + plotHeight - ((value - min) / (max - min) * plotHeight);

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(leftMargin, PointY(values[0])), false, false);
            for (var index = 1; index < values.Length; index++)
            {
                context.LineTo(new Point(leftMargin + index * stepX, PointY(values[index])), true, false);
            }
        }
        geometry.Freeze();

        if (ShowAreaFill)
        {
            // 面积填充：主线色 15% 透明（「极淡填充」，S01）
            var fillGeometry = new StreamGeometry();
            using (var context = fillGeometry.Open())
            {
                context.BeginFigure(new Point(leftMargin, 6 + plotHeight), true, true);
                for (var index = 0; index < values.Length; index++)
                {
                    context.LineTo(new Point(leftMargin + index * stepX, PointY(values[index])), true, false);
                }
                context.LineTo(new Point(leftMargin + plotWidth, 6 + plotHeight), true, false);
            }
            fillGeometry.Freeze();
            var areaFill = new SolidColorBrush(((SolidColorBrush)seriesBrush).Color) { Opacity = 0.15 };
            areaFill.Freeze();
            drawingContext.DrawGeometry(areaFill, null, fillGeometry);
        }

        drawingContext.DrawGeometry(null, pen, geometry);

        // Y 轴三档刻度（等宽小字，次要色）
        DrawAxisLabel(drawingContext, max.ToString("0", CultureInfo.InvariantCulture), 4, 6);
        DrawAxisLabel(drawingContext, ((min + max) / 2).ToString("0", CultureInfo.InvariantCulture),
            4, 6 + plotHeight / 2);
        DrawAxisLabel(drawingContext, min.ToString("0", CultureInfo.InvariantCulture), 4, 6 + plotHeight);
    }

    private void DrawAxisLabel(DrawingContext drawingContext, string text, double x, double y)
    {
        drawingContext.DrawText(new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                10,
                new SolidColorBrush(Color.FromRgb(0x9B, 0xA8, 0xB4)),
                1.25),
            new Point(x, y - 5));
    }
}
