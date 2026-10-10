using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PositionMonitor.App.Services;

internal static class NativeWindowFrame
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    internal static Size? CaptionSize(Window window, FrameworkElement header)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || DwmGetWindowAttribute(handle, 5, out NativeRect buttons, Marshal.SizeOf<NativeRect>()) != 0 || buttons.Right <= buttons.Left || !GetWindowRect(handle, out var bounds)) return null;
        if (header.ActualWidth <= 0 || header.ActualHeight <= 0) return null;
        var middle = bounds.Top + (buttons.Top + buttons.Bottom) / 2;
        var left = bounds.Left + buttons.Left;
        // 包括完整命中区域及边缘像素，不能只按图标范围计算透明区域。
        var limit = left - (buttons.Right - buttons.Left) / 3;
        while (left > limit && HitTest(handle, left - 1, middle) == 8) left--;
        var bottom = bounds.Top + buttons.Bottom;
        var closeCenter = bounds.Left + (int)(buttons.Left + 2.5 * (buttons.Right - buttons.Left) / 3.0);
        var bottomLimit = bottom + 16;
        for (var y = bottom; y < bottomLimit; y++)
        {
            if (HitTest(handle, closeCenter, y) != 20) break;
            bottom = y + 1;
        }
        var point = header.PointFromScreen(new Point(left, bottom));
        return new Size(Math.Max(0, header.ActualWidth - point.X), Math.Max(0, point.Y));
    }

    internal static int[] VerifyCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(handle, 1, out int rendering, sizeof(int)) != 0 || rendering == 0)
            throw new InvalidOperationException("Windows 原生非客户区渲染未启用。");
        if (DwmGetWindowAttribute(handle, 5, out NativeRect buttons, Marshal.SizeOf<NativeRect>()) != 0 || buttons.Right <= buttons.Left || buttons.Bottom <= buttons.Top || !GetWindowRect(handle, out var bounds))
            throw new InvalidOperationException("未取得 Windows 原生窗口按钮范围。");
        var width = (buttons.Right - buttons.Left) / 3.0;
        var tests = Enumerable.Range(0, 3).Select(index => HitTest(handle, bounds.Left + (int)(buttons.Left + (index + .5) * width), bounds.Top + (buttons.Top + buttons.Bottom) / 2)).ToArray();
        if (!tests.SequenceEqual(new[] { 8, 9, 20 })) throw new InvalidOperationException($"原生窗口按钮命中错误：{string.Join(',', tests)}。");
        var fill = (FrameworkElement)window.FindName("NativeCaptionFill");
        var fillOrigin = fill.PointToScreen(new Point());
        var scanHeight = (int)Math.Ceiling(64 * System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY);
        for (var index = 0; index < 3; index++)
        {
            var x = bounds.Left + (int)(buttons.Left + (index + .5) * width);
            for (var y = 0; y < scanHeight; y++)
                if (HitTest(handle, x, bounds.Top + y) == tests[index] && bounds.Top + y >= fillOrigin.Y - .1)
                    throw new InvalidOperationException($"标题背景遮住了原生按钮高亮的下边缘：按钮 {index}，命中 Y={bounds.Top + y}，背景 Y={fillOrigin.Y}，状态 {window.WindowState}。");
        }
        var middle = bounds.Top + (buttons.Top + buttons.Bottom) / 2;
        for (var x = bounds.Left + buttons.Left - (int)width; x <= bounds.Left + buttons.Left; x++)
            if (HitTest(handle, x, middle) == 8 && x < fillOrigin.X - .1)
                throw new InvalidOperationException("标题背景遮住了最小化按钮高亮的左边缘。");
        if (window.WindowState == WindowState.Maximized)
        {
            var work = SystemParameters.WorkArea;
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            if (HitTest(handle, (int)Math.Round(work.Right * scale.DpiScaleX) - 1, (int)Math.Round(work.Top * scale.DpiScaleY)) != 20)
                throw new InvalidOperationException("最大化时屏幕最右上角未命中关闭按钮。");
        }
        return tests;
    }

    private static int HitTest(IntPtr handle, int x, int y) => (int)SendMessage(handle, 0x84, IntPtr.Zero, new IntPtr(unchecked((int)(((uint)(ushort)y << 16) | (ushort)x))));

    internal static void PreviewHover(Window window, int button)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(handle, 5, out NativeRect buttons, Marshal.SizeOf<NativeRect>()) != 0 || !GetWindowRect(handle, out var bounds)) return;
        var x = bounds.Left + (int)(buttons.Left + (button + .5) * (buttons.Right - buttons.Left) / 3.0);
        var y = bounds.Top + (buttons.Top + buttons.Bottom) / 2;
        HitTest(handle, x, y);
        SendMessage(handle, 0xA0, new IntPtr(new[] { 8, 9, 20 }[button]), new IntPtr(unchecked((int)(((uint)(ushort)y << 16) | (ushort)x))));
    }

    // WPF 渲染截图不包含 DWM 按钮和窗外阴影，此模式捕获实际桌面上的窗口及周围边界。
    internal static void Capture(Window window, string path, bool offscreen = false)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) throw new InvalidOperationException("无法读取窗口边界。");
        var handle = new WindowInteropHelper(window).Handle;
        DwmGetWindowAttribute(handle, 5, out NativeRect buttons, Marshal.SizeOf<NativeRect>());
        if (window.FindName("NativeCaptionFill") is FrameworkElement fill)
            File.WriteAllText(Path.ChangeExtension(path, ".边界.json"), System.Text.Json.JsonSerializer.Serialize(new { Window = $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}", Buttons = $"{buttons.Left},{buttons.Top},{buttons.Right},{buttons.Bottom}", Scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY, FillTop = fill.PointToScreen(new Point()).Y - rect.Top }));
        var margin = offscreen || window.WindowState == WindowState.Maximized ? 0 : 24;
        var width = rect.Right - rect.Left + margin * 2;
        var height = rect.Bottom - rect.Top + margin * 2;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            var captured = offscreen ? PrintWindow(handle, memory, 2) : BitBlt(memory, 0, 0, width, height, screen, rect.Left - margin, rect.Top - margin, 0x00CC0020);
            if (!captured) throw new InvalidOperationException("窗口截图失败。");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path); encoder.Save(stream);
        }
        finally { SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}
