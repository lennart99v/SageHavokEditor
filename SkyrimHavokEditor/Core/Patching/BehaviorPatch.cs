using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace SkyrimHavokEditor.Core.Patching
{
    [XmlRoot("BehaviorPatch")]
    public class BehaviorPatch
    {
        [XmlAttribute] public string Version { get; set; } = "1.0";
        [XmlAttribute] public string Author { get; set; } = "";
        [XmlAttribute] public string Description { get; set; } = "";
        [XmlAttribute] public string BaseFile { get; set; } = "";
        [XmlAttribute] public string Created { get; set; } = "";

        [XmlArray("Operations")]
        [XmlArrayItem("AddObject", typeof(AddObjectOp))]
        [XmlArrayItem("DeleteObject", typeof(DeleteObjectOp))]
        [XmlArrayItem("AddChild", typeof(AddChildOp))]
        [XmlArrayItem("ModifyParam", typeof(ModifyParamOp))]
        [XmlArrayItem("AppendParam", typeof(AppendParamOp))]
        [XmlArrayItem("ModifyChild", typeof(ModifyChildOp))]
        [XmlArrayItem("AddEvent", typeof(AddEventOp))]
        [XmlArrayItem("RenameEvent", typeof(RenameEventOp))]
        [XmlArrayItem("AddVariable", typeof(AddVariableOp))]
        [XmlArrayItem("RenameVariable", typeof(RenameVariableOp))]
        public List<PatchOperation> Operations { get; set; } = new();

        [XmlIgnore] public int AddCount => Operations.Count(o => o is AddObjectOp);
        [XmlIgnore] public int DeleteCount => Operations.Count(o => o is DeleteObjectOp);
        [XmlIgnore]
        public int ModifyCount => Operations.Count(o => o is ModifyParamOp
                                                                      or AppendParamOp
                                                                      or ModifyChildOp);
        [XmlIgnore] public int EventCount => Operations.Count(o => o is AddEventOp or RenameEventOp);
        [XmlIgnore] public int VarCount => Operations.Count(o => o is AddVariableOp or RenameVariableOp);
    }

    // ── Base ──────────────────────────────────────────────────────────────────
    public abstract class PatchOperation
    {
        [XmlAttribute] public string Note { get; set; } = "";
    }

    // ── Object-level ops ──────────────────────────────────────────────────────
    public class AddObjectOp : PatchOperation
    {
        [XmlAttribute] public string LocalId { get; set; } = "";
        [XmlAttribute] public string ClassName { get; set; } = "";
        [XmlAttribute] public string Signature { get; set; } = "";
        [XmlElement("Param")] public List<PatchParam> Params { get; set; } = new();
        [XmlElement("Child")] public List<AddObjectOp> Children { get; set; } = new();
    }

    public class DeleteObjectOp : PatchOperation
    {
        /// "id:#0052" or "name:BHR_Master"
        [XmlAttribute] public string Anchor { get; set; } = "";
    }

    // ── Param-level ops ───────────────────────────────────────────────────────
    public class ModifyParamOp : PatchOperation
    {
        [XmlAttribute] public string Anchor { get; set; } = "";
        [XmlAttribute] public string ParamName { get; set; } = "";
        [XmlAttribute] public string OldValue { get; set; } = "";
        [XmlAttribute] public string NewValue { get; set; } = "";
    }

    public class AppendParamOp : PatchOperation
    {
        [XmlAttribute] public string Anchor { get; set; } = "";
        [XmlAttribute] public string ParamName { get; set; } = "";
        [XmlAttribute] public string LocalRef { get; set; } = "";
        [XmlAttribute] public string Value { get; set; } = "";
    }

    public class AddChildOp : PatchOperation
    {
        [XmlAttribute] public string Anchor { get; set; } = "";
        [XmlAttribute] public string ParamName { get; set; } = "";
        [XmlAttribute] public string ClassName { get; set; } = "";
        [XmlElement("Param")] public List<PatchParam> Params { get; set; } = new();
    }

    /// Modify a param inside an inline child object
    public class ModifyChildOp : PatchOperation
    {
        /// Anchor for the parent object
        [XmlAttribute] public string Anchor { get; set; } = "";
        /// Name of the param that holds the children (e.g. "triggerInterval")
        [XmlAttribute] public string ParamName { get; set; } = "";
        /// Zero-based index of the child to modify
        [XmlAttribute] public int ChildIndex { get; set; } = 0;
        /// Param name inside the child
        [XmlAttribute] public string ChildParam { get; set; } = "";
        [XmlAttribute] public string OldValue { get; set; } = "";
        [XmlAttribute] public string NewValue { get; set; } = "";
    }

    // ── Event ops ─────────────────────────────────────────────────────────────
    public class AddEventOp : PatchOperation
    {
        [XmlAttribute] public string Name { get; set; } = "";
    }

    public class RenameEventOp : PatchOperation
    {
        [XmlAttribute] public int Index { get; set; }
        [XmlAttribute] public string OldName { get; set; } = "";
        [XmlAttribute] public string NewName { get; set; } = "";
    }

    // ── Variable ops ──────────────────────────────────────────────────────────
    public class AddVariableOp : PatchOperation
    {
        [XmlAttribute] public string Name { get; set; } = "";
        [XmlAttribute] public string Type { get; set; } = "VARIABLE_TYPE_REAL";
        [XmlAttribute] public string DefaultValue { get; set; } = "0";
    }

    public class RenameVariableOp : PatchOperation
    {
        [XmlAttribute] public int Index { get; set; }
        [XmlAttribute] public string OldName { get; set; } = "";
        [XmlAttribute] public string NewName { get; set; } = "";
    }

    // ── Param entry ───────────────────────────────────────────────────────────
    public class PatchParam
    {
        [XmlAttribute] public string Name { get; set; } = "";
        [XmlAttribute] public string Value { get; set; } = "";
        [XmlAttribute] public string LocalRef { get; set; } = "";
    }
}