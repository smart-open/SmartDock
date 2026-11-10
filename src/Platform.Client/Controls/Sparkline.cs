namespace Platform.Client.Controls;

using System.Windows;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 行内迷你趋势线（点位实时监控图每行右列）：无坐标轴、无交互，
/// 只画归一化折线——数据列来自客户端环形缓冲（最多 ~120 点）。
/// 用 OnRender 而非 Shape 组合：一行一个控件 × 8000 点位，必须零可视化树开销。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        // IList<double> 而非 double[]：XAML 模板内对数组型 DP 用 Binding 属性语法会触发
        // MC4102 PropertyArrayStart 编译错误（数组 DP 在 BAML 模板段只能用元素语法）
        nameof(Values), typeof(IList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesBrushProperty = DependencyProperty.Register(
        nameof(SeriesBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bad/缺失样本数（渲染为断线提示：尾部红点）；0 = 全 Good。</summary>
    public static readonly DependencyProperty BadCountProperty = DependencyProperty.Register(
        nameof(BadCount), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double PenThickness = 1.4;

    public IList<double>? Values
    {
        get => (IList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? SeriesBrush
    {
        get => (Brush?)GetValue(SeriesBrushProperty);
        set => SetValue(SeriesBrushProperty, value);
    }

    public int BadCount
    {
        get => (int)GetValue(BadCountProperty);
        set => SetValue(BadCountProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var values = Values;
        var seriesBrush = SeriesBrush ?? Brushes.Transparent;
        if (values is not { Count: >= 2 })
        {
            // 无数据：画一条中线占位，行高对齐不塌陷
            var placeholderPen = new Pen(seriesBrush, 0.8) { DashStyle = DashStyles.Dash };
            var midY = height / 2;
            drawingContext.DrawLine(placeholderPen, new Point(0, midY), new Point(width, midY));
            return;
        }

        double min = values.Min();
        double max = values.Max();
        var span = Math.Max(max - min, 1e-9);   // 全相等值：除零保护，线画在中部

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var stepX = width / (values.Count - 1);
            context.BeginFigure(new Point(0, height - 2 - ((values[0] - min) / span * (height - 4))), false, false);
            for (var index = 1; index < values.Count; index++)
            {
                var x = index * stepX;
                var y = height - 2 - ((values[index] - min) / span * (height - 4));
                context.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();

        drawingContext.DrawGeometry(null, new Pen(seriesBrush, PenThickness), geometry);

        // 缓冲中有 Bad 样本：末尾画红点提示（红色只用于真实异常，§0.5 铁律）
        if (BadCount > 0)
        {
            var lastX = width;
            var lastY = height - 2 - ((values[^1] - min) / span * (height - 4));
            drawingContext.DrawEllipse(Brushes.Transparent, new Pen(Brushes.IndianRed, 1.2),
                new Point(lastX - 2, lastY), 2.2, 2.2);
        }
    }
}
