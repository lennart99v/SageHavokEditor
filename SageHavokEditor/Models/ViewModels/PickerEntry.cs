namespace SageHavokEditor.Models.ViewModels
{
    /// <summary>
    /// One row of an id ↔ name dropdown in the property editor: the raw value that
    /// gets written to the param, and the label the user reads.
    ///
    /// Deliberately flat and immutable — the list is rebuilt from the live event /
    /// variable table whenever the edited value changes, so nothing here is a
    /// second copy of state that could drift.
    /// </summary>
    public sealed class PickerEntry
    {
        public PickerEntry(string id, string display)
        {
            Id = id;
            Display = display;
        }

        /// <summary>Value written into the param (the positional index, or -1).</summary>
        public string Id { get; }

        /// <summary>What the dropdown shows, e.g. "attackStart (#12)".</summary>
        public string Display { get; }

        public override string ToString() => Display;   // ComboBox type-ahead
    }
}
