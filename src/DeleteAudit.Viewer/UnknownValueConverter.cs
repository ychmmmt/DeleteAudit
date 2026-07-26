using System.Globalization;
using System.Windows.Data;
using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Viewer;

public sealed class UnknownValueConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value switch
        {
            null => ViewerDisplay.Unknown,
            string text => ViewerDisplay.Value(text),
            _ => value
        };

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
