using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PositionMonitor.App.Controls;

public sealed class HeartbeatBadge : Grid
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(nameof(Count), typeof(long), typeof(HeartbeatBadge), new PropertyMetadata(0L, CountChanged));
    public long Count { get => (long)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _rise = new();
    private readonly TextBlock _plus = new() { Text = "+1", Foreground = new SolidColorBrush(Color.FromRgb(220, 66, 90)), FontWeight = FontWeights.Bold, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    public HeartbeatBadge()
    {
        Width = 66; Height = 62;
        Children.Add(new TextBlock { Text = "♥", FontFamily = new("Segoe UI Symbol"), FontSize = 48, Foreground = new SolidColorBrush(Color.FromRgb(230, 72, 98)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new(.5, .5), RenderTransform = _scale });
        _plus.RenderTransform = _rise;
        Children.Add(_plus);
        ToolTip = "每收到一条实时网关心跳 +1";
        System.Windows.Automation.AutomationProperties.SetName(this, "网关心跳红心");
    }
    private static void CountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (HeartbeatBadge)d;
        var delta = (long)e.NewValue - (long)e.OldValue;
        if (delta <= 0) return;
        control._plus.Text = $"+{delta}";
        if (!SystemParameters.ClientAreaAnimation) return;
        control._scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.2, TimeSpan.FromMilliseconds(180)) { AutoReverse = true });
        control._scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.2, TimeSpan.FromMilliseconds(180)) { AutoReverse = true });
        control._plus.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1000)));
        control._rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, -8, TimeSpan.FromMilliseconds(1000)));
    }
}
