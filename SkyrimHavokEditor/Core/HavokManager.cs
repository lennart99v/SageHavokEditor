using System.Collections.Generic;
using System.Linq;
using SkyrimHavokEditor.Models;

namespace SkyrimHavokEditor.Core
{
    public class HavokManager
    {
        public Dictionary<string, HkObject> ObjectMap { get; private set; } = new();

        public HkObject RootObject { get; private set; }

        public void BuildGraph(HkPackfile packfile)
        {
            ObjectMap.Clear();
            var dataSection = packfile.Sections.FirstOrDefault(s => s.Name == "__data__");
            if (dataSection == null) return;

            foreach (var obj in dataSection.Objects)
                ObjectMap[obj.Id] = obj;

            RootObject = ObjectMap.TryGetValue(packfile.TopLevelObject, out var root) ? root : null;

            // Recursively resolve #ID references in all params
            foreach (var obj in ObjectMap.Values)
                ResolveParams(obj);
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

        public HkObject Resolve(string id)
        {
            if (id == null)
                return null;

            if (ObjectMap.TryGetValue(id, out var obj))
                return obj;

            return null;
        }

        public bool TryResolve(string id, out HkObject obj)
        {
            obj = null;

            if (string.IsNullOrWhiteSpace(id))
                return false;

            return ObjectMap.TryGetValue(id, out obj);
        }
    }
}
