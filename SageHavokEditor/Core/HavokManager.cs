using System.Collections.Generic;
using System.Linq;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core
{
    public class HavokManager
    {
        public Dictionary<string, HkObject> ObjectMap { get; private set; } = new();

        public HkObject? RootObject { get; private set; }

        /// <summary>
        /// The loaded packfile's header, kept so a save can reproduce it. Saves
        /// rebuild the HkPackfile from ObjectMap rather than holding the original,
        /// so without this the header attributes were written empty
        /// (classversion="" contentsversion="" toplevelobject="") — see
        /// HkPackfile.Skyrim*Version for the from-scratch fallbacks.
        /// </summary>
        public string ClassVersion { get; private set; } = "";
        public string ContentsVersion { get; private set; } = "";
        public string TopLevelObjectId { get; private set; } = "";

        /// <summary>
        /// The header to write for this manager: what was loaded, else Skyrim's
        /// schema, and the real root id rather than a hard-coded #0050 — the root
        /// is only #0050 by convention, and a wrong toplevelobject points the
        /// runtime at the wrong object.
        /// </summary>
        public HkPackfile NewPackfile(string sectionName = "__data__") => new()
        {
            ClassVersion = string.IsNullOrEmpty(ClassVersion)
                ? HkPackfile.SkyrimClassVersion : ClassVersion,
            ContentsVersion = string.IsNullOrEmpty(ContentsVersion)
                ? HkPackfile.SkyrimContentsVersion : ContentsVersion,
            TopLevelObject = !string.IsNullOrEmpty(TopLevelObjectId) ? TopLevelObjectId
                : !string.IsNullOrEmpty(RootObject?.Id) ? RootObject!.Id
                : "#0050",
            Sections = new List<HkSection>
            {
                new HkSection
                {
                    Name    = sectionName,
                    Objects = ObjectMap.Values.OrderBy(o => o.Id).ToList()
                }
            }
        };

        public void BuildGraph(HkPackfile packfile)
        {
            ObjectMap.Clear();
            ClassVersion     = packfile.ClassVersion;
            ContentsVersion  = packfile.ContentsVersion;
            TopLevelObjectId = packfile.TopLevelObject;
            var dataSection = packfile.Sections.FirstOrDefault(s => s.Name == "__data__");
            if (dataSection == null) return;

            foreach (var obj in dataSection.Objects)
                ObjectMap[obj.Id] = obj;

            RootObject = ObjectMap.TryGetValue(packfile.TopLevelObject, out var root) ? root : null;

            // Recursively resolve #ID references in all params
            foreach (var obj in ObjectMap.Values)
                ResolveParams(obj);

            // Attach declared-type metadata (drives the type-aware property editor)
            Services.HavokTypeCatalog.AnnotateAll(ObjectMap.Values);
        }

        private void ResolveParams(HkObject obj)
        {
            if (obj.Params == null)
                return;

            foreach (var param in obj.Params)
            {
                if (!string.IsNullOrWhiteSpace(param.Value) &&
                    param.Value.StartsWith("#") &&
                    ObjectMap.TryGetValue(param.Value, out var child))
                {
                    param.InnerObject = child;
                }
            }
        }

        public HkObject? Resolve(string? id)
        {
            if (id == null)
                return null;

            if (ObjectMap.TryGetValue(id, out var obj))
                return obj;

            return null;
        }

        public bool TryResolve(string? id, out HkObject? obj)
        {
            obj = null;

            if (string.IsNullOrWhiteSpace(id))
                return false;

            return ObjectMap.TryGetValue(id, out obj);
        }
    }
}
