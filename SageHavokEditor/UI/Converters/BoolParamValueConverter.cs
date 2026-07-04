using System;
using System.Globalization;
using System.Windows.Data;

namespace SageHavokEditor.UI.Converters
{
    /// <summary>
    /// Two-way bridge between a Havok param's string Value ("true"/"false") and a
    /// CheckBox's bool IsChecked. Writing goes back through the same HkParam.Value
    /// setter as the text editor, so undo/persistence are unchanged.
    /// </summary>
    public class BoolParamValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.Equals((value as string)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? "true" : "false";
    }
}
