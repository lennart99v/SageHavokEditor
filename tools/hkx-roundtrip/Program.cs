using System.Security.Cryptography;
using HKX2;

// Regression harness for the pointer-size-aware layout work.
//
//   dotnet run --project tools/hkx-roundtrip -- [--out <dir>] [--convert] <file.hkx> [...]
//
// Default mode reports the source header, the SHA-256 of the Havok XML the file
// deserialises to, and whether re-serialising with the source's own header
// reproduces the input bytes.  Capture it before changing the layout code and
// diff it afterwards: for Skyrim SE files nothing may move.
//
// --convert additionally repacks each file to BOTH pointer sizes, reads each
// result back, and checks the Havok XML still matches the original.  That is
// the LE<->SE conversion path end to end: same content, different pointer size.
//
// Set HKX_TRACE=1 to print a stack trace for each failure.

static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b))[..16];

static byte[] Repack(hkRootLevelContainer root, HKXHeader header)
{
    using var ms = new MemoryStream();
    new PackFileSerializer().Serialize(root, new BinaryWriterEx(ms), header);
    return ms.ToArray();
}

static (hkRootLevelContainer Root, HKXHeader Header) Load(byte[] bytes)
{
    var des = new PackFileDeserializer();
    using var ms = new MemoryStream(bytes);
    var root = (hkRootLevelContainer)des.Deserialize(new BinaryReaderEx(ms));
    return (root, des._header);
}

static byte[] ToXml(hkRootLevelContainer root, HKXHeader header)
{
    using var ms = new MemoryStream();
    new HKX2.XmlSerializer().Serialize(root, header, ms);
    return ms.ToArray();
}

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "usage: hkx-roundtrip [--out <dir>] [--convert] <file.hkx> [...]");
    return 1;
}

string? outDir = null;
var convert = false;
var inputs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--convert") convert = true;
    else inputs.Add(args[i]);
}
if (outDir is not null) Directory.CreateDirectory(outDir);

var trace = Environment.GetEnvironmentVariable("HKX_TRACE") == "1";
var failures = 0;

foreach (var path in inputs)
{
    Console.Write($"{Path.GetFileName(path),-34} ");
    try
    {
        var input = File.ReadAllBytes(path);
        var (root, header) = Load(input);
        var platform = header.PointerSize == 8 ? "SSE(64)" : "LE(32)";

        var xml = ToXml(root, header);
        var repack = Repack(root, header);

        if (outDir is not null)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            File.WriteAllBytes(Path.Combine(outDir, stem + ".xml"), xml);
            File.WriteAllBytes(Path.Combine(outDir, stem + ".repack.hkx"), repack);
        }

        var identical = repack.AsSpan().SequenceEqual(input);
        Console.Write(
            $"{platform,-8} {header.ContentsVersionString,-16} " +
            $"xml={Sha(xml)} hkx={Sha(repack)} " +
            $"repack={(identical ? "byte-identical" : $"DIFFERS ({input.Length} -> {repack.Length})")}");

        if (convert)
        {
            // Round-trip through each pointer size and require identical XML.
            var se = Load(Repack(root, HKXHeader.SkyrimSE()));
            var le = Load(Repack(root, HKXHeader.SkyrimLE()));
            var seXml = ToXml(se.Root, se.Header);
            var leXml = ToXml(le.Root, le.Header);

            var seOk = seXml.AsSpan().SequenceEqual(xml);
            var leOk = leXml.AsSpan().SequenceEqual(xml);
            Console.Write($"  ->SE={(seOk ? "ok" : "MISMATCH")} ->LE={(leOk ? "ok" : "MISMATCH")}");
            if (!seOk || !leOk) failures++;
        }

        Console.WriteLine();
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
        if (trace)
            foreach (var f in (ex.StackTrace ?? "").Split(Environment.NewLine).Take(16))
                Console.WriteLine("      " + f.Trim());
    }
}

return failures == 0 ? 0 : 1;
