// UpdateNotifier/Views/Converters.cs
// Value converters used in MainWindow.xaml.
// Kept in a separate file so MainWindow.xaml.cs can stay clean.

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UpdateNotifier.Views;

/// <summary>
/// Converts a bool binding to Visibility.
/// True  → Visible
/// False → Collapsed
/// Referenced in MainWindow.xaml as: Converter={StaticResource BoolToVis}
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
