using System;
using System.Collections.Generic;

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

        /// <summary>
        /// The likely reason, in one clause, for someone who has just been told
        /// their save was refused. A message that only restates the check leaves
        /// them nowhere to start; naming the edit that usually causes it does.
        /// Empty where there is no single likely cause worth guessing at.
        /// </summary>
        public string Cause { get; set; } = "";

        /// <summary>True when there is a <see cref="Cause"/> to show.</summary>
        public bool HasCause => Cause.Length > 0;

        /// <summary>
        /// True when this finding means the graph is internally inconsistent —
        /// something references what isn't there, or two records the runtime pairs
        /// by position disagree. These are the findings an <c>.hkx</c> save
        /// refuses over, and the reason is that Havok accepts them: the file
        /// converts, the game loads it, and the actor T-poses or the process hard
        /// -faults with nothing written to any log.
        /// </summary>
        public bool IsStructural => IsError && Structural.Contains(Category);

        /// <summary>
        /// Distinguishes two findings of the same check on the same object, where
        /// there is a *stable* way to tell them apart — the two name/info array
        /// pairs both hang off <c>hkbBehaviorGraphData</c>, for instance. Left
        /// empty where the only discriminator would be a param path: those carry
        /// array indices, which shift when an unrelated element is removed, and a
        /// fingerprint that moves is worse than one that is a little coarse.
        /// </summary>
        public string Subject { get; set; } = "";

        /// <summary>
        /// Identity for baseline comparison: which check, on which object, about
        /// what. The description is deliberately excluded — it carries context
        /// that moves when unrelated things change (a machine's list of valid
        /// stateIds, say), and a defect that was already in the file must not read
        /// as new because an edit elsewhere reworded it. Erring coarse is
        /// deliberate: the cost of a fingerprint that drifts is a save refused
        /// over somebody else's bug, while the cost of one too coarse is a second
        /// fault on an already-faulty object going to the advisory report instead
        /// of the refusal — where it is still shown, and still clickable.
        /// </summary>
        public string Fingerprint => $"{Category}|{ObjectId}|{Subject}";

        /// <summary>A value that doesn't parse as its declared Havok type.</summary>
        public const string CategoryType = "type";
        /// <summary>A <c>#ref</c> pointing at an id the file doesn't contain.</summary>
        public const string CategoryBrokenRef = "broken-ref";
        /// <summary>A generator slot left null — the node drives nothing.</summary>
        public const string CategoryNullGenerator = "null-generator";
        /// <summary>An event id or variable index outside the file's own table.</summary>
        public const string CategoryIndexRange = "index-range";
        /// <summary>A clip's animation: unset, or not registered in the character project.</summary>
        public const string CategoryAnimation = "animation";
        /// <summary>A state nothing can transition into.</summary>
        public const string CategoryUnreachableState = "unreachable-state";
        /// <summary>An object a .hkx save would drop, being unreachable from the root.</summary>
        public const string CategoryPruned = "pruned";
        /// <summary>The file header's <c>toplevelobject</c> names an object the file doesn't hold.</summary>
        public const string CategoryMissingRoot = "missing-root";
        /// <summary>A machine whose <c>startStateId</c> matches none of its states.</summary>
        public const string CategoryStartState = "start-state";
        /// <summary>Two states in one machine sharing a <c>stateId</c>.</summary>
        public const string CategoryDuplicateStateId = "duplicate-state-id";
        /// <summary>A transition whose <c>toStateId</c> is in no state of its machine.</summary>
        public const string CategoryToStateId = "to-state-id";
        /// <summary>Two arrays the runtime pairs by position disagreeing on length.</summary>
        public const string CategoryArrayPairing = "array-pairing";
        /// <summary>A state machine with no states at all.</summary>
        public const string CategoryEmptyStateMachine = "empty-state-machine";

        private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
        {
            CategoryBrokenRef, CategoryNullGenerator, CategoryIndexRange,
            CategoryStartState, CategoryDuplicateStateId, CategoryToStateId,
            CategoryArrayPairing, CategoryMissingRoot,
        };
    }
}
