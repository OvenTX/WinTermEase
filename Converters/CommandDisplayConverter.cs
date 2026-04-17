using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace WinTermEase.Converters;

/// <summary>Converts a command string to a human-readable form for tooltips (shows escape sequences).</summary>
[ValueConversion(typeof(string), typeof(string))]
public class CommandDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0) return "(空)";

        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '\r':   sb.Append("\\r");     break;
                case '\n':   sb.Append("\\n");     break;
                case '\t':   sb.Append("\\t");     break;
                case '\x01': sb.Append("Ctrl+A");  break;
                case '\x02': sb.Append("Ctrl+B");  break;
                case '\x03': sb.Append("Ctrl+C");  break;
                case '\x04': sb.Append("Ctrl+D");  break;
                case '\x1a': sb.Append("Ctrl+Z");  break;
                case '\x1b': sb.Append("ESC");     break;
                default:
                    if (c < ' ')
                        sb.Append($"\\x{(int)c:X2}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
