using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using SageHavokEditor.Models;

namespace SageHavokEditor.UI.Converters
{
    /// <summary>
    /// Classifies a Havok param so the Object Data editor can pick a control:
    /// "bool" (CheckBox), "enum" (ComboBox), or "text" (TextBox). Takes
    /// (Value, TypeInfo) as a MultiBinding: the declared HKX2 type decides when
    /// known; otherwise falls back to sniffing the value for true/false, which
    /// keeps classes outside the HKX2 type set behaving as before.
    /// </summary>
    public class ParamKindConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (values.ElementAtOrDefault(0) as string)?.Trim();

            if (values.ElementAtOrDefault(1) is HkParamTypeInfo info)
            {
                // An int that indexes the event or variable table edits as a name
                // picker rather than a number (see IdPickerItemsConverter).
                if (info.Kind == HkParamKind.Int)
                    switch (info.Semantic)
                    {
                        case HkParamSemantic.EventId: return "event";
                        case HkParamSemantic.VariableIndex: return "variable";
                    }

                return info.Kind switch
                {
                    HkParamKind.Bool => "bool",
                    // A pipe-joined value is a flags combination — a single-select
                    // ComboBox can't represent it, so those edit as text.
                    HkParamKind.Enum => s != null && s.Contains('|') ? "text" : "enum",
                    _ => "text"
                };
            }

            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)
                ? "bool" : "text";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
