using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>One entry of the Guide, as the view added it.</summary>
    public sealed class GuideItem
    {
        /// <summary>A sidebar group heading ("Overview", "Editing") rather than a
        /// section — <see cref="Key"/> and <see cref="Body"/> are empty.</summary>
        public bool IsGroup { get; init; }

        /// <summary>The anchor the sidebar scrolls to. Empty for a group.</summary>
        public string Key { get; init; } = "";

        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
    }

    /// <summary>
    /// The in-app Guide, written out as Markdown.
    ///
    /// <para>The Guide has never had a source document — it is built at runtime
    /// from <c>AddSection</c> calls in <see cref="DocumentationView"/>, so the
    /// repo's <c>README.md</c> is a different and much shorter thing. That left
    /// anyone who wanted to read it at their own text size, or keep a copy, with
    /// nothing to take: selecting the text copies it without any of the
    /// formatting. This renders the same items the view renders, so the two
    /// cannot drift.</para>
    ///
    /// <para>The body text uses two conventions and they are all that has to be
    /// recognised: a line starting with <c>•</c> is a bullet, and a short line
    /// above a run of bullets is a sub-heading. Everything else is a paragraph.</para>
    /// </summary>
    public static class GuideMarkdown
    {
        /// <summary>
        /// Whether a line inside a section body is a sub-heading for the bullets
        /// under it, rather than prose. Both the rendered Guide and the exported
        /// Markdown ask this, so the two cannot disagree about what is a heading.
        ///
        /// <para>The original rule was "any non-bullet line in a block that has
        /// bullets", which is right for a heading and wrong for a paragraph that
        /// happens to sit above a list — it bolded five whole paragraphs of prose
        /// across the Guide, some of them hundreds of characters long. A heading
        /// is short, so the length is the discriminator.</para>
        /// </summary>
        public const int MaxSubHeadingLength = 80;

        public static bool IsSubHeading(string line, string paragraph)
        {
            if (line.Length == 0 || line.Length > MaxSubHeadingLength) return false;
            if (line.TrimStart().StartsWith("•", StringComparison.Ordinal)) return false;
            if (char.IsDigit(line[0])) return false;
            return paragraph.Contains('•');
        }

        public static string Render(IEnumerable<GuideItem> items, string? version = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# Sage Havok Editor — Guide");
            sb.AppendLine();
            sb.Append("This is the same Guide as the in-app **📖 Guide** tab");
            sb.AppendLine(version is { Length: > 0 } ? $", from version {version}." : ".");
            sb.AppendLine("It is generated from the app, so it cannot drift from what the app shows.");
            sb.AppendLine();

            var list = items.ToList();

            // Contents, grouped the way the sidebar groups them.
            sb.AppendLine("## Contents");
            sb.AppendLine();
            foreach (var item in list)
            {
                if (item.IsGroup)
                {
                    sb.AppendLine();
                    sb.AppendLine($"**{item.Title}**");
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine($"- [{item.Title}](#{Slug(item.Title)})");
                }
            }
            sb.AppendLine();

            string currentGroup = "";
            foreach (var item in list)
            {
                if (item.IsGroup) { currentGroup = item.Title; continue; }

                if (currentGroup.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"# {currentGroup}");
                    currentGroup = "";
                }

                sb.AppendLine();
                sb.AppendLine($"## {item.Title}");
                sb.AppendLine();
                sb.Append(RenderBody(item.Body));
            }

            return sb.ToString();
        }

        private static string RenderBody(string body)
        {
            var sb = new StringBuilder();

            foreach (var para in body.Split("\n\n"))
            {
                var lines = para.Split('\n');
                var pending = new List<string>();

                void FlushParagraph()
                {
                    if (pending.Count == 0) return;
                    // Lines inside one paragraph are soft-wrapped in the app; join
                    // them so Markdown reflows them at the reader's width instead
                    // of at ours.
                    sb.AppendLine(Escape(string.Join(" ", pending)));
                    sb.AppendLine();
                    pending.Clear();
                }

                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd();
                    if (line.Length == 0) continue;

                    if (line.TrimStart().StartsWith("•", StringComparison.Ordinal))
                    {
                        FlushParagraph();
                        var text = line.TrimStart().TrimStart('•').Trim();
                        sb.AppendLine($"- {Escape(text)}");
                        continue;
                    }

                    if (IsSubHeading(line, para))
                    {
                        FlushParagraph();
                        sb.AppendLine();
                        sb.AppendLine($"**{Escape(line.Trim())}**");
                        sb.AppendLine();
                        continue;
                    }

                    pending.Add(line.Trim());
                }

                FlushParagraph();
                if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n", StringComparison.Ordinal))
                    sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escape only what would otherwise change the rendering. The Guide is
        /// prose full of things like <c>*.hkx</c>, <c>#0379</c> and
        /// <c>hkbClipGenerator</c>, and over-escaping it would be worse to read
        /// than under-escaping.
        /// </summary>
        private static string Escape(string s)
            => s.Replace("\\", "\\\\")
                .Replace("*", "\\*")
                .Replace("_", "\\_")
                .Replace("`", "\\`");

        /// <summary>GitHub's heading anchor rule, near enough for a contents list.</summary>
        private static string Slug(string title)
        {
            var sb = new StringBuilder();
            foreach (var ch in title.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (ch is ' ' or '-') sb.Append('-');
                // everything else (punctuation, emoji) is dropped, as GitHub does
            }
            return sb.ToString();
        }
    }
}
