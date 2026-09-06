using System;
using System.Globalization;
using System.Linq;

namespace SageHavokEditor.Core.Animation
{
    /// <summary>
    /// The translation multiplier offered on FBX export, and the parsing behind it.
    ///
    /// This lives outside the view because it is a string-to-number decision with
    /// real edge cases — a comma decimal, a preset line with its explanation
    /// attached, a user typing nonsense — and none of that is worth only being
    /// exercisable by clicking.
    ///
    /// Nothing in a .hkx says what a Havok unit is worth, which is why this is a
    /// choice at all rather than a constant. Blender reads the file as centimetres
    /// and divides by 100 on import, so the multiplier lands as
    /// <c>havokValue * scale / 100</c> Blender units.
    /// </summary>
    public static class FbxScaleOption
    {
        /// <summary>
        /// Offered scales. The number leads so the line stays parseable after the
        /// user edits it, and the note after says what the number buys. Measured
        /// on a troll skeleton spanning 161.1 Havok units tall: 1 imports at 1.61
        /// Blender units, 100 at 161.12, and 1.428 at 2.30 — a believable troll.
        /// </summary>
        public static readonly string[] Presets =
        {
            "1  — Havok units",
            "100  — 1 unit = 1 Blender unit",
            "1.428  — approx. real-world metres"
        };

        public const double Default = 1.0;

        /// <summary>
        /// The leading number of a preset line, or of whatever the user typed.
        /// Accepts a comma decimal, because this is a European-locale codebase and
        /// "1,428" is what that keyboard produces.
        /// </summary>
        public static bool TryParse(string? text, out double scale)
        {
            scale = Default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Split on whitespace and the em-dash the presets use — NOT on a plain
            // hyphen, or "-5" loses its sign and silently exports at 5.
            var token = text.Trim()
                .Split(new[] { ' ', '\t', '—' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (token == null) return false;

            token = token.Replace(',', '.');
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return false;
            if (v <= 0 || double.IsInfinity(v) || double.IsNaN(v)) return false;

            scale = v;
            return true;
        }

        /// <summary>Render a scale back as the preset line it matches, else as a bare number.</summary>
        public static string Format(double scale)
        {
            foreach (var p in Presets)
                if (TryParse(p, out var v) && Math.Abs(v - scale) < 1e-9) return p;
            return scale.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
