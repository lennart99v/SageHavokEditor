namespace SageHavokEditor.Core.Validation
{
    public class ValidationIssue
    {
        public string Severity { get; set; } = "";
        public string ObjectId { get; set; } = "";
        public string ObjectClass { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsError => Severity == "Error";
        public bool IsWarning => Severity == "Warning";

        /// <summary>
        /// Which check raised this, so a caller can act on one kind without
        /// re-running the pass. The save path needs exactly that: a bad *value*
        /// makes the HKX conversion die and is refused outright, while everything
        /// else the doctor finds is reported and left to the user.
        /// </summary>
        public string Category { get; set; } = "";

        /// <summary>A value that doesn't parse as its declared Havok type.</summary>
        public const string CategoryType = "type";
        /// <summary>A <c>#ref</c> pointing at an id the file doesn't contain.</summary>
        public const string CategoryBrokenRef = "broken-ref";
        /// <summary>A generator slot left null — the node drives nothing.</summary>
        public const string CategoryNullGenerator = "null-generator";
        /// <summary>An event id or variable index outside the file's own table.</summary>
        public const string CategoryIndexRange = "index-range";
        /// <summary>A clip whose animation isn't registered in the character project.</summary>
        public const string CategoryAnimation = "animation";
        /// <summary>A state nothing can transition into.</summary>
        public const string CategoryUnreachableState = "unreachable-state";
        /// <summary>An object a .hkx save would drop, being unreachable from the root.</summary>
        public const string CategoryPruned = "pruned";
    }
}
