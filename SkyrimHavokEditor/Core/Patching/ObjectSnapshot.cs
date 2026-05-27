using System.Collections.Generic;

namespace SkyrimHavokEditor.Core.Patching
{
    /// <summary>
    /// Deep snapshot of a single HkObject at load time.
    /// Captures params, their children, and NumElements.
    /// </summary>
    public class ObjectSnapshot
    {
        public string ClassName { get; set; } = "";
        public string Name { get; set; } = "";

        /// paramName → ParamSnapshot
        public Dictionary<string, ParamSnapshot> Params { get; set; } = new();
    }

    public class ParamSnapshot
    {
        public string Value { get; set; } = "";
        public string NumElements { get; set; } = "";

        /// Flat list of strings (for eventNames / variableNames list params)
        public List<string> Strings { get; set; } = new();

        /// Inline child objects (e.g. triggerInterval, variableInfos children)
        public List<ChildSnapshot> Children { get; set; } = new();
    }

    public class ChildSnapshot
    {
        public string ClassName { get; set; } = "";
        /// paramName → value
        public Dictionary<string, string> Params { get; set; } = new();
    }
}
