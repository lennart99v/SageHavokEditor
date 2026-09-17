using System;
using System.Globalization;

namespace SageHavokEditor.Core
{
    /// <summary>
    /// Turning <c>hkbVariableValueSet.wordVariableValues[i].value</c> into something
    /// a person can edit, and back again without losing it.
    ///
    /// <para>The slot is a single <c>hkInt32</c> for every variable, whatever the
    /// variable's declared type. A <c>VARIABLE_TYPE_REAL</c> keeps its float
    /// <b>bit-cast</b> into that int — <c>1.0f</c> is stored as
    /// <c>1065353216</c>, <c>-2.06f</c> as <c>-1073490166</c> — while bools, ints
    /// and pointer/vector indices are stored as the plain number they look like.
    /// Nothing in the value itself says which it is: <b>the declared type in
    /// <c>hkbVariableInfo.type</c> is the only thing that does</b>, and this is
    /// the single place that reads it.</para>
    ///
    /// <para>It used to be guessed, by two rules that disagreed with each other.
    /// Decoding treated a value as a float bit pattern when it was negative or
    /// bigger than a million; encoding treated a string as a float when it
    /// contained a <c>'.'</c>. Neither looked at the type, and every float whose
    /// text form happens to have no decimal point fell straight through the gap:
    /// <c>1065353216</c> decoded to <c>"1"</c> and re-encoded to the integer
    /// <c>1</c>, a different value, silently, because <c>1</c> is a perfectly
    /// valid <c>hkInt32</c> and nothing downstream had grounds to object. In
    /// SKYBSpiderDaedraBehavior that hit <c>weaponSpeedMult</c>,
    /// <c>turnSpeedMult</c> and <c>fMinTurnDelta</c>, all three of them 1.0.</para>
    ///
    /// <para>Encoding also wrote the bit pattern <b>unsigned</b>
    /// (<c>BitConverter.ToUInt32</c>), so a negative float came back as
    /// <c>3221477130</c> — right bits, wrong type, outside <c>hkInt32</c>, and
    /// the HKX save refused it. That refusal is the only reason any of this was
    /// ever noticed.</para>
    /// </summary>
    public static class HavokVariableValue
    {
        /// <summary>
        /// Whether this variable's word holds a float bit pattern. Everything
        /// that is not a real — bool, the int widths, pointer and vector indices
        /// — is stored as the number it reads as.
        /// </summary>
        public static bool IsReal(string? variableType)
            => variableType is "VARIABLE_TYPE_REAL" or "VARIABLE_TYPE_FLOAT";

        /// <summary>
        /// The stored word as text to show and edit: a float for a real, the
        /// plain integer for everything else.
        /// </summary>
        public static string Decode(string? raw, string? variableType)
        {
            if (!TryReadWord(raw, out int word)) return raw?.Trim() ?? "0";
            if (!IsReal(variableType)) return word.ToString(CultureInfo.InvariantCulture);

            float f = BitConverter.Int32BitsToSingle(word);

            // NaN and the infinities are shown as themselves ("NaN", "Infinity"),
            // which is both the honest reading of those bits and the one that
            // round-trips: the infinities encode back to the same word exactly.
            //
            // Showing the raw integer instead was tried and is wrong — Encode
            // cannot tell such a word from someone typing "-1" for -1.0, so the
            // pair stopped being an identity for every word whose exponent is all
            // ones. The one thing that still does not round-trip is a NaN's
            // payload, since every NaN text parses back to the canonical
            // 0x7FC00000; that is why nothing rewrites a word the user has not
            // edited (see SerializeToFile), which leaves an exotic NaN alone.

            // .NET's default float formatting is the shortest string that parses
            // back to the same float, which is exactly the round-trip property
            // this needs. The old "0.###" was not: it quietly truncated anything
            // past three decimals, so a value could survive a save and still be
            // a different number afterwards.
            return f.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Edited text back into the stored word. For a real that is the float's
        /// bit pattern as a <b>signed</b> <c>hkInt32</c>; for everything else the
        /// integer itself.
        /// </summary>
        public static string Encode(string? text, string? variableType)
        {
            var s = text?.Trim() ?? "";
            if (s.Length == 0) return "0";

            if (IsReal(variableType))
            {
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    return BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture);

                // Not a number we can bit-cast: fall through and keep whatever
                // integer it is rather than replacing it with a guess.
            }

            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return "1";
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return "0";

            return TryReadWord(s, out int word)
                ? word.ToString(CultureInfo.InvariantCulture)
                : s;
        }

        /// <summary>
        /// Read the word as a signed 32-bit value, accepting the unsigned form as
        /// well.
        ///
        /// <para>Accepting it is a repair, not laxity: a file saved by the version
        /// that wrote <c>BitConverter.ToUInt32</c> has <c>3221477130</c> where
        /// <c>-1073490166</c> belongs, and those are the same 32 bits. Wrapping it
        /// back into <c>int</c> means opening such a file shows the right value
        /// and saving it writes the right one, instead of the user having to find
        /// and retype every field the old encoder touched.</para>
        /// </summary>
        private static bool TryReadWord(string? raw, out int word)
        {
            word = 0;
            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s)) return false;

            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out word))
                return true;

            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long wide)
                && wide >= uint.MinValue && wide <= uint.MaxValue)
            {
                word = unchecked((int)(uint)wide);
                return true;
            }

            return false;
        }
    }
}
