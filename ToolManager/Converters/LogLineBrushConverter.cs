using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ToolManager.Services;

namespace ToolManager.Converters;

/// <summary>Tô màu dòng log theo từ khóa (bảng màu cho nền sáng).</summary>
public class LogLineBrushConverter : IValueConverter
{
    private static readonly Brush Normal = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)));
    private static readonly Brush System = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)));
    private static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string line) return Normal;
        if (line.Contains(">>> ")) return System;
        if (ToolRunner.IsErrorLine(line) || line.Contains("fail:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)) return Error;
        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase)) return Warn;
        return Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

/// <summary>true nếu dòng log là dòng lỗi (cùng quy tắc với bộ đếm lỗi của ToolRunner).</summary>
public class LogIsErrorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string line && ToolRunner.IsErrorLine(line);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
