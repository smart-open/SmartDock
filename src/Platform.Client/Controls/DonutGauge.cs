namespace Platform.Client.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// 环形仪表（大屏「点位可用率」）：底环 + 数值弧 + 中心等宽百分数。
/// Value 0~100；弧从 12 点方向顺时针。
/// </summary>
public sealed class DonutGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DonutGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(DonutGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? ValueBrush
    {
        get => (Brush?)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = Math.Min(width, height) / 2 - 8;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(width / 2, height / 2);
        const double startAngle = -90;   // 12 点方向

        // 底环：完整圆，低调描边
        var trackPen = new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x31, 0x3C)), 10);
        drawingContext.DrawEllipse(null, trackPen, center, radius - 5, radius - 5);

        // 数值弧：0~100% 映射 0~360°；100% 画满圆（ArcSegment 对 360° 是零长弧，需特判）
        var clamped = Math.Clamp(Value, 0, 100);
        var valueBrush = ValueBrush ?? Brushes.Transparent;
        var valuePen = new Pen(valueBrush, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (clamped >= 100)
        {
            drawingContext.DrawEllipse(null, valuePen, center, radius - 5, radius - 5);
        }
        else if (clamped > 0)
        {
            var sweepAngle = 360 * clamped / 100;
            var endRadians = (startAngle + sweepAngle) * Math.PI / 180;
            var endPoint = new Point(
                center.X + (radius - 5) * Math.Cos(endRadians),
                center.Y + (radius - 5) * Math.Sin(endRadians));
            var startRadians = startAngle * Math.PI / 180;
            var startPoint = new Point(
                center.X + (radius - 5) * Math.Cos(startRadians),
                center.Y + (radius - 5) * Math.Sin(startRadians));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(startPoint, false, false);
                context.ArcTo(endPoint, new Size(radius - 5, radius - 5),
                    sweepAngle, sweepAngle > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, valuePen, geometry);
        }

        // 中心百分数
        var text = new FormattedText(
            $"{Value:0.0}%",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Math.Max(radius / 2.6, 12),
            valueBrush,
            1.25);
        drawingContext.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
