using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SageHavokEditor.Models.ViewModels
{
    public class EventUsageEntry
    {
        public string UsageType { get; set; } = "";   // "Transition", "Wildcard", "Trigger", "Notify", "Interval", "Property"
        public string Direction { get; set; } = "";   // "Listens" (reacts to it) or "Sends" (emits it)
        public string Description { get; set; } = ""; // human-readable summary
        public string Detail { get; set; } = "";      // secondary line: owning machine, nested target, class
        public string ObjectId { get; set; } = "";    // for navigation on click
        public string ClassName { get; set; } = "";
        public string EventId { get; set; } = "";      // firing event index — set for transitions so the graph can reveal the edge
        public string ToStateObjectId { get; set; } = ""; // destination state object id — lets the graph reveal the exact edge (stateIds repeat across SMs, so the event id alone is ambiguous)

        /// <summary>Second line under the description; falls back to the class name.</summary>
        public string SubText => string.IsNullOrEmpty(Detail) ? ClassName : Detail;
    }

    /// <summary>One row of the all-events overview: an event plus how often it is referenced.</summary>
    public class EventXrefSummary
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Listens { get; set; }
        public int Sends { get; set; }
        public int Total => Listens + Sends;

        /// <summary>
        /// True when nothing in THIS behaviour file references the event. That is not
        /// the same as unused: annotation events (HitFrame, SoundPlay.*, and the
        /// spell-fire events dragons use) are emitted from annotation tracks inside the
        /// animation .hkx files, and cross-behaviour events are matched by name in
        /// another file's table. Neither is visible from here, so an unreferenced event
        /// is a lead to check, never a licence to delete.
        /// </summary>
        public bool IsOrphan => Total == 0;

        public string Counts => Total == 0 ? "no refs in this file" : $"{Listens} listen / {Sends} send";
    }
}
