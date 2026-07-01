using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SageHavokEditor.Models.ViewModels
{
    public class TransitionDetail
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
        public string ObjectId { get; set; } = "";

        // Flag rows render as a colored badge (like the graph / transition list)
        // instead of a plain Label/Value pair.
        public bool IsFlag { get; set; }
        public System.Windows.Media.Brush? BadgeBg { get; set; }
        public System.Windows.Media.Brush? BadgeFg { get; set; }
    }
}
