using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class CompareResult
    {
        public string Category { get; set; } // "Variable", "Event", "Clip", "Object"
        public string Name { get; set; }
        public string Id { get; set; }
        public string ValueA { get; set; }
        public string ValueB { get; set; }
        public string DiffType { get; set; } // "Added", "Removed", "Modified", "Same"
        public bool IsAdded => DiffType == "Added";
        public bool IsRemoved => DiffType == "Removed";
        public bool IsModified => DiffType == "Modified";
        public bool IsSame => DiffType == "Same";
    }
}
