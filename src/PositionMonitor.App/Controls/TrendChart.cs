using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PositionMonitor.Core.Protocol;

namespace PositionMonitor.App.Controls;

public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty RecordsProperty = DependencyProperty.Register(nameof(Records), typeof(IReadOnlyList<SampleRecord>), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinutesProperty = DependencyProperty.Register(nameof(Minutes), typeof(int), typeof(TrendChart), new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<SampleRecord>? Records { get => (IReadOnlyList<SampleRecord>?)GetValue(RecordsProperty); set => SetValue(RecordsProperty, value); }
    public int Minutes { get => (int)GetValue(MinutesProperty); set => SetValue(MinutesProperty, value); }
    private SampleRecord[] _visible = [];
    private DateTimeOffset _start, _end;
    private readonly Brush[] _colors = [Brush("#2679C8"), Brush("#C47B14"), Brush("#8557BE")];
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 120 || ActualHeight < 80) return;
        dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
        var left = 48.0; var top = 14.0; var width = ActualWidth - 64; var height = ActualHeight - 48;
        var records = Records?.OrderBy(r => r.PlotTime).ToArray() ?? [];
        var last = records.LastOrDefault()?.PlotTime ?? DateTimeOffset.UtcNow;
        _visible = records.Where(r => Minutes == 0 || r.PlotTime >= last.AddMinutes(-Minutes)).ToArray();
        var values = _visible.SelectMany(r => new[] { r.Sample.AngleX, r.Sample.AngleY, r.Sample.AngleZ }).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        var minimum = values.Length > 0 ? Math.Min(0, values.Min()) : -15;
        var maximum = values.Length > 0 ? Math.Max(0, values.Max()) : 15;
        var step = Math.Max(1, Math.Ceiling((maximum - minimum) / 4));
        minimum = Math.Floor(minimum / step) * step - step * 0.25;
        maximum = Math.Ceiling(maximum / step) * step + step * 0.25;
        var pen = new Pen(Brush("#C5DAD4"), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = top + height * i / 4;
            dc.DrawLine(pen, new(left, y), new(left + width, y));
            Text(dc, $"{maximum - (maximum - minimum) * i / 4:0.#}°", left - 10, y - 8, "#607780", alignRight: true);
        }
        if (_visible.Length == 0) return;
        _start = _visible[0].PlotTime; _end = last;
        if (_start == _end) _end = _start.AddSeconds(1);
        for (var i = 0; i <= 4; i++)
        {
            var x = left + width * i / 4;
            var time = _start + (_end - _start) * (i / 4.0);
            var format = (_end - _start).TotalHours >= 24 ? "MM-dd HH:mm" : "HH:mm:ss";
            Text(dc, time.ToOffset(TimeSpan.FromHours(8)).ToString(format), x - 24, top + height + 10, "#607780", 11);
        }
        for (var axis = 0; axis < 3; axis++)
        {
            Point? previous = null;
            foreach (var record in _visible)
            {
                var value = axis switch { 0 => record.Sample.AngleX, 1 => record.Sample.AngleY, _ => record.Sample.AngleZ };
                if (value is null) { previous = null; continue; }
                var point = new Point(left + (record.PlotTime - _start).TotalSeconds / (_end - _start).TotalSeconds * width, top + (maximum - value.Value) / (maximum - minimum) * height);
                if (previous is { } p) dc.DrawLine(new(_colors[axis], 2), p, point);
                if (_visible.Length < 30) dc.DrawEllipse(_colors[axis], null, point, 2.5, 2.5);
                previous = point;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_visible.Length == 0) return;
        var ratio = Math.Clamp((e.GetPosition(this).X - 48) / Math.Max(1, ActualWidth - 64), 0, 1);
        var time = _start + (_end - _start) * ratio;
        var record = _visible.MinBy(r => Math.Abs((r.PlotTime - time).TotalSeconds))!;
        static string Value(double? v) => v?.ToString("+0.00;-0.00;0.00") + (v is null ? "无效" : "°");
        ToolTip = $"北京时间 {record.PlotTime.ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm:ss}\nX {Value(record.Sample.AngleX)}   Y {Value(record.Sample.AngleY)}   Z {Value(record.Sample.AngleZ)}";
    }

    private void Text(DrawingContext dc, string text, double x, double y, string color, double size = 12, bool alignRight = false)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new(alignRight ? x - formatted.WidthIncludingTrailingWhitespace : x, y));
    }
}
