using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PositionMonitor.App.Controls;

public sealed class TiltInstrument : FrameworkElement
{
    public static readonly DependencyProperty XProperty = DependencyProperty.Register(nameof(X), typeof(double?), typeof(TiltInstrument), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YProperty = DependencyProperty.Register(nameof(Y), typeof(double?), typeof(TiltInstrument), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double? X { get => (double?)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double? Y { get => (double?)GetValue(YProperty); set => SetValue(YProperty, value); }
    private static Brush Color(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var radius = Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 20);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(Color("#F3FAF9"), new(Color("#C7E4DF"), 1), center, radius, radius);
        dc.DrawEllipse(null, new(Color("#DFEEEB"), 1), center, radius * .5, radius * .5);
        dc.DrawLine(new(Color("#C7DCD9"), 1), new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        dc.DrawLine(new(Color("#C7DCD9"), 1), new(center.X, center.Y - radius), new(center.X, center.Y + radius));
        for (var i = 0; i < 36; i++)
        {
            var angle = i * Math.PI / 18;
            var r = radius - (i % 9 == 0 ? 10 : 4);
            dc.DrawLine(new(Color("#86B8AF"), 1), new(center.X + Math.Cos(angle) * r, center.Y + Math.Sin(angle) * r), new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius));
        }
        if (X is { } x && Y is { } y)
        {
            var dx = x / 90; var dy = -y / 90;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1) { dx /= length; dy /= length; }
            var point = new Point(center.X + dx * (radius - 14), center.Y + dy * (radius - 14));
            dc.DrawLine(new(Color("#087F78"), 1.5), center, point);
            dc.DrawEllipse(Color("#087F78"), new(Brushes.White, 3), point, 10, 10);
        }
        Label(dc, "X", center.X + radius + 5, center.Y - 8);
        Label(dc, "Y", center.X - 4, center.Y - radius - 18);
    }
    private void Label(DrawingContext dc, string text, double x, double y) => dc.DrawText(new(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, Color("#607780"), VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
}
