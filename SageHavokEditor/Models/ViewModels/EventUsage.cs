using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SageHavokEditor.Models.ViewModels
{
    public class EventUsageEntry
    {
        public string UsageType { get; set; } = "";   // "Transition", "Trigger", "Modifier", etc.
        public string Description { get; set; } = ""; // human-readable summary
        public string ObjectId { get; set; } = "";    // for navigation on click
        public string ClassName { get; set; } = "";
        public string EventId { get; set; } = "";      // firing event index — set for transitions so the graph can reveal the edge
        public string ToStateObjectId { get; set; } = ""; // destination state object id — lets the graph reveal the exact edge (stateIds repeat across SMs, so the event id alone is ambiguous)
    }
}
