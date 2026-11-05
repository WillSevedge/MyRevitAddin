using System.Globalization;
using System.Windows.Data;

namespace Nexus.Hub;

/// <summary>true → false and back (e.g. IsEnabled = not IsDone).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
