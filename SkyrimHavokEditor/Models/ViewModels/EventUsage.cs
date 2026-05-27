using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class EventUsageEntry
    {
        public string UsageType { get; set; }   // "Transition", "Trigger", "Modifier", etc.
        public string Description { get; set; } // human-readable summary
        public string ObjectId { get; set; }    // for navigation on click
        public string ClassName { get; set; }
    }
}
