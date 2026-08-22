using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using SageHavokEditor.Core.Services;
using SageHavokEditor.Models;
using SageHavokEditor.Models.ViewModels;

namespace SageHavokEditor.UI.Converters
{
    /// <summary>
    /// Builds the choice list for an event-id / variable-index param: (none) first,
    /// then the file's event or variable table as "name (#index)".
    ///
    /// The current value is bound in as well, and an unrecognised one is added as
    /// its own entry (‹unknown #495›). That is not cosmetic — a ComboBox whose
    /// SelectedValue isn't in its ItemsSource reports no selection, and a TwoWay
    /// binding would then write that emptiness back over a value the editor simply
    /// didn't recognise. Keeping the current value present makes the picker
    /// incapable of losing data, whatever the file contains.
    /// </summary>
    public class IdPickerItemsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var current = (values.ElementAtOrDefault(0) as string)?.Trim() ?? "";
            var info = values.ElementAtOrDefault(1) as HkParamTypeInfo;
            var events = values.ElementAtOrDefault(2) as IEnumerable<IdNamePair>;
            var variables = values.ElementAtOrDefault(3) as IEnumerable<IdNamePair>;

            var entries = new List<PickerEntry> { new("-1", "(none)") };

            switch (info?.Semantic)
            {
                case HkParamSemantic.EventId:
                    // EventResolver owns the "name, or ‹unnamed #id›" convention.
                    var list = (events ?? Enumerable.Empty<IdNamePair>()).ToList();
                    var resolver = new EventResolver(list);
                    foreach (var e in list)
                        entries.Add(new PickerEntry(e.Id, resolver.Label(e.Id)));
                    break;

                case HkParamSemantic.VariableIndex:
                    // A variable's IdNamePair.Id is "<objectId>_<i>", not the index
                    // the param stores — that is Index.
                    foreach (var v in variables ?? Enumerable.Empty<IdNamePair>())
                    {
                        var idx = v.Index.ToString(CultureInfo.InvariantCulture);
                        entries.Add(new PickerEntry(idx,
                            string.IsNullOrEmpty(v.Name) ? $"‹unnamed #{idx}›" : $"{v.Name} (#{idx})"));
                    }
                    break;

                default:
                    return entries;
            }

            if (current.Length > 0 && !entries.Any(e => e.Id == current))
                entries.Insert(1, new PickerEntry(current, $"‹unknown #{current}›"));

            return entries;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
