using System;
using System.Globalization;
using System.Windows.Data;

namespace SageHavokEditor.UI.Converters
{
    /// <summary>
    /// Classifies a Havok param value so the Object Data editor can pick a control:
    /// "bool" for true/false toggles, "text" for everything else. Used by DataTriggers
    /// to swap a CheckBox in for boolean params (e.g. bAnimationDriven, enable).
    /// </summary>
    public class ParamKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string)?.Trim();
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)
                ? "bool" : "text";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
