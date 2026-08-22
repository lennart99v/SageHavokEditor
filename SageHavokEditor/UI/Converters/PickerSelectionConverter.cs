using System;
using System.Globalization;
using System.Windows.Data;

namespace SageHavokEditor.UI.Converters
{
    /// <summary>
    /// Pass-through for a picker ComboBox's SelectedValue, with one job: never let a
    /// *cleared* selection reach the param.
    ///
    /// A Selector drops its selection while its ItemsSource is being replaced, and
    /// the picker's item list is rebuilt whenever the edited value changes — so a
    /// plain TwoWay binding would write that momentary null back and blank the
    /// param. Returning Binding.DoNothing makes the write a no-op; only a real user
    /// selection (a non-null entry id) is committed.
    /// </summary>
    public class PickerSelectionConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value ?? Binding.DoNothing;
    }
}
