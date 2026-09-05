using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SageHavokEditor.Core.Animation;

/// <summary>
/// An FBX file is a tree of named records, each carrying typed properties and
/// child records. Both the binary and the ASCII form are that same tree — so it
/// is built once here and serialised twice, which is the only way the readable
/// form stays a truthful view of the one Blender actually loads.
///
/// Binary is the shipping format: <b>Blender refuses ASCII FBX outright</b>
/// ("ASCII FBX files are not supported"), and Blender is the target. The ASCII
/// serialiser is kept for eyeballing a file and diffing two exports.
/// </summary>
public sealed class FbxNode
{
    public string Name;
    public List<FbxProp> Props = new();
    public List<FbxNode> Children = new();

    public FbxNode(string name, params FbxProp[] props)
    {
        Name = name;
        Props.AddRange(props);
    }

    public FbxNode Add(FbxNode child) { Children.Add(child); return child; }

    public FbxNode Add(string name, params FbxProp[] props)
    {
        var n = new FbxNode(name, props);
        Children.Add(n);
        return n;
    }

    /// <summary>A Properties70 "P:" entry.</summary>
    public FbxNode P(string name, string type, string sub, string flags, params FbxProp[] values)
    {
        var props = new List<FbxProp> { FbxProp.Str(name), FbxProp.Str(type), FbxProp.Str(sub), FbxProp.Str(flags) };
        props.AddRange(values);
        var n = new FbxNode("P");
        n.Props = props;
        Children.Add(n);
        return n;
    }
}

public readonly struct FbxProp
{
    /// <summary>
    /// FBX type code: Y=int16 C=bool I=int32 F=float D=double L=int64 S=string,
    /// lowercase = array of that type. 'N' is ours: an object's name+class pair,
    /// which the two forms spell differently and so cannot be a plain string.
    /// </summary>
    public readonly char Code;
    public readonly object Value;

    private FbxProp(char code, object value) { Code = code; Value = value; }

    public static FbxProp I32(int v) => new('I', v);
    public static FbxProp I64(long v) => new('L', v);
    public static FbxProp F64(double v) => new('D', v);
    public static FbxProp Str(string v) => new('S', v);
    public static FbxProp ArrI32(int[] v) => new('i', v);
    public static FbxProp ArrI64(long[] v) => new('l', v);
    public static FbxProp ArrF32(float[] v) => new('f', v);

    /// <summary>Binary spells it "name\0\x01class"; ASCII spells it "class::name".</summary>
    public static FbxProp NameClass(string name, string cls) => new('N', (name, cls));
}

// ── binary ───────────────────────────────────────────────────────────────────

public static class FbxBinarySerializer
{
    private static readonly byte[] HeaderMagic =
        Encoding.ASCII.GetBytes("Kaydara FBX Binary  ").Concat(new byte[] { 0x00, 0x1A, 0x00 }).ToArray();

    /// <summary>The 16 bytes Autodesk closes the file with. Blender never looks; other readers do.</summary>
    private static readonly byte[] FooterMagic =
    {
        0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E,
        0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B
    };

    public const uint Version = 7400;

    public static void Write(string path, IEnumerable<FbxNode> roots)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

        w.Write(HeaderMagic);
        w.Write(Version);

        foreach (var n in roots) WriteNode(w, n);
        w.Write(new byte[13]);                       // top-level null record

        // footer: id, pad to 16, zero, version, 120 zeros, magic
        w.Write(new byte[16]);
        long pad = 16 - (fs.Position % 16);
        w.Write(new byte[pad == 16 ? 16 : pad]);
        w.Write((uint)0);
        w.Write(Version);
        w.Write(new byte[120]);
        w.Write(FooterMagic);
    }

    private static void WriteNode(BinaryWriter w, FbxNode n)
    {
        var s = w.BaseStream;
        long start = s.Position;

        w.Write((uint)0);                            // EndOffset, patched below
        w.Write((uint)n.Props.Count);
        long propLenAt = s.Position;
        w.Write((uint)0);                            // PropertyListLen, patched below

        var nameBytes = Encoding.ASCII.GetBytes(n.Name);
        w.Write((byte)nameBytes.Length);
        w.Write(nameBytes);

        long propStart = s.Position;
        foreach (var p in n.Props) WriteProp(w, p);
        long propEnd = s.Position;

        if (n.Children.Count > 0)
        {
            foreach (var c in n.Children) WriteNode(w, c);
            w.Write(new byte[13]);                   // a node with children ends in a null record
        }

        long end = s.Position;
        s.Position = start; w.Write((uint)end);
        s.Position = propLenAt; w.Write((uint)(propEnd - propStart));
        s.Position = end;
    }

    private static void WriteProp(BinaryWriter w, FbxProp p)
    {
        switch (p.Code)
        {
            case 'I': w.Write((byte)'I'); w.Write((int)p.Value); break;
            case 'L': w.Write((byte)'L'); w.Write((long)p.Value); break;
            case 'D': w.Write((byte)'D'); w.Write((double)p.Value); break;

            case 'S':
            {
                var b = Encoding.UTF8.GetBytes((string)p.Value);
                w.Write((byte)'S'); w.Write((uint)b.Length); w.Write(b);
                break;
            }

            case 'N':
            {
                var (name, cls) = ((string, string))p.Value;
                var b = Encoding.UTF8.GetBytes(name)
                    .Concat(new byte[] { 0x00, 0x01 })
                    .Concat(Encoding.UTF8.GetBytes(cls)).ToArray();
                w.Write((byte)'S'); w.Write((uint)b.Length); w.Write(b);
                break;
            }

            // Arrays are written uncompressed (encoding 0): the largest clip this
            // produces is a couple of MB, and a deflate stream is one more thing
            // that can be subtly wrong in a file nobody can read by eye.
            case 'i':
            {
                var v = (int[])p.Value;
                w.Write((byte)'i'); w.Write((uint)v.Length); w.Write((uint)0); w.Write((uint)(v.Length * 4));
                foreach (var x in v) w.Write(x);
                break;
            }
            case 'l':
            {
                var v = (long[])p.Value;
                w.Write((byte)'l'); w.Write((uint)v.Length); w.Write((uint)0); w.Write((uint)(v.Length * 8));
                foreach (var x in v) w.Write(x);
                break;
            }
            case 'f':
            {
                var v = (float[])p.Value;
                w.Write((byte)'f'); w.Write((uint)v.Length); w.Write((uint)0); w.Write((uint)(v.Length * 4));
                foreach (var x in v) w.Write(x);
                break;
            }

            default: throw new NotSupportedException($"property code '{p.Code}'");
        }
    }
}
