namespace Optimus.Shell.ViewModels;

using System;
using System.Globalization;
using System.Windows.Data;

/// <summary>Inverts a boolean. Used to drive <c>IsReadOnly</c> from <c>IsDraftEditable</c>.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : true;
}
