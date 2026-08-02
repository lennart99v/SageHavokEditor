using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using SageHavokEditor.Models;

namespace SageHavokEditor.UI.Converters
{
    /// <summary>
    /// Per-element ✕ button: visible only for inline (anonymous) elements of
    /// array params. Cached resolved-ref children (which also render nested)
    /// and single inline struct members (no numelements) get no ✕ — removing
    /// a mandatory struct or a live reference is not an element deletion.
    /// Takes (child.Id, parent HkParam.NumElements).
    /// </summary>
    public class InlineElementRemoveVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var childId = values.ElementAtOrDefault(0) as string;
            var numElements = values.ElementAtOrDefault(1) as string;
            return string.IsNullOrEmpty(childId) && !string.IsNullOrWhiteSpace(numElements)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// "+ Add element" button: visible for inline-struct array params (sticky
    /// IsInlineStructArray flag, or live inline children). Ref arrays edit as
    /// text; string arrays live in their dedicated tabs.
    /// </summary>
    public class InlineArrayAddVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not HkParam p) return Visibility.Collapsed;
            bool isArray = !string.IsNullOrWhiteSpace(p.NumElements);
            bool inline = p.IsInlineStructArray || p.Children.Any(c => string.IsNullOrEmpty(c.Id));
            return isArray && inline ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
