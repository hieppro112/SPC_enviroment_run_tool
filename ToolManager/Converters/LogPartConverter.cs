using System.Globalization;
using System.Windows.Data;

namespace ToolManager.Converters;

/// <summary>
/// Tách dòng log "HH:mm:ss nội dung" thành 2 phần để hiển thị giờ màu nhạt.
/// ConverterParameter = "time" hoặc "text".
/// </summary>
public class LogPartConverter : IValueConverter
{
    private const int TimeLength = 8; // "HH:mm:ss"

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string line) return "";
        bool hasTime = line.Length > TimeLength && line[2] == ':' && line[5] == ':' && line[TimeLength] == ' ';
        if (parameter as string == "time") return hasTime ? line[..TimeLength] : "";
        return hasTime ? line[(TimeLength + 1)..] : line;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
