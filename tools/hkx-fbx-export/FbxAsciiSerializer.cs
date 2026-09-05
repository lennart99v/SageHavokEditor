using System.Globalization;
using System.Text;
using SageHavokEditor.Core.Animation;

namespace SageHavokEditor.Tools.FbxExport;

/// <summary>
/// The same record tree as the binary writer, spelled as FBX ASCII.
///
/// Debug view only, and it lives in the harness rather than the app because
/// Blender refuses ASCII FBX outright ("ASCII FBX files are not supported").
/// It earns its place when two exports disagree and you need to read why.
/// </summary>
public static class FbxAsciiSerializer
{
    public static void Write(string path, IEnumerable<FbxNode> roots)
    {
        var sb = new StringBuilder(1 << 20);
        sb.AppendLine("; FBX 7.4.0 project file");
        sb.AppendLine("; Debug view only — Blender does not import ASCII FBX.");
        sb.AppendLine("; ----------------------------------------------------");
        sb.AppendLine();
        foreach (var n in roots) WriteNode(sb, n, 0);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteNode(StringBuilder sb, FbxNode n, int depth)
    {
        string pad = new('\t', depth);

        // An array property owns the whole record body: "KeyTime: *61 { a: ... }".
        if (n.Props.Count == 1 && char.IsLower(n.Props[0].Code) && n.Children.Count == 0)
        {
            sb.Append(pad).Append(n.Name).Append(": ").AppendLine(ArrayBody(n.Props[0], depth));
            return;
        }

        sb.Append(pad).Append(n.Name).Append(':');
        for (int i = 0; i < n.Props.Count; i++)
            sb.Append(i == 0 ? " " : ", ").Append(Scalar(n.Props[i]));

        if (n.Children.Count == 0) { sb.AppendLine(); return; }

        sb.AppendLine(" {");
        foreach (var c in n.Children) WriteNode(sb, c, depth + 1);
        sb.Append(pad).AppendLine("}");
    }

    private static string Scalar(FbxProp p) => p.Code switch
    {
        'I' => ((int)p.Value).ToString(CultureInfo.InvariantCulture),
        'L' => ((long)p.Value).ToString(CultureInfo.InvariantCulture),
        'D' => Num((double)p.Value),
        'S' => "\"" + (string)p.Value + "\"",
        'N' => "\"" + ((ValueTuple<string, string>)p.Value).Item2 + "::" + ((ValueTuple<string, string>)p.Value).Item1 + "\"",
        _ => "?"
    };

    private static string ArrayBody(FbxProp p, int depth)
    {
        string pad = new('\t', depth);
        var sb = new StringBuilder();
        int n; Func<int, string> at;

        switch (p.Code)
        {
            case 'i': { var v = (int[])p.Value; n = v.Length; at = i => v[i].ToString(CultureInfo.InvariantCulture); break; }
            case 'l': { var v = (long[])p.Value; n = v.Length; at = i => v[i].ToString(CultureInfo.InvariantCulture); break; }
            case 'f': { var v = (float[])p.Value; n = v.Length; at = i => Num(v[i]); break; }
            default: throw new NotSupportedException($"array code '{p.Code}'");
        }

        sb.Append('*').Append(n).Append(" {\n").Append(pad).Append("\ta: ");
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(at(i)); }
        sb.Append('\n').Append(pad).Append('}');
        return sb.ToString();
    }

    private static string Num(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "0" : v.ToString("G9", CultureInfo.InvariantCulture);
}
