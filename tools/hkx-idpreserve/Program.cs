using System.Xml.Serialization;
using SageHavokEditor.Models;

// Checks that Havok XML loaded into the editor's model and written back keeps
// every #NNNN object id verbatim -- the property that lets a modder edit
// hkxcmd/Nemesis-numbered XML here without breaking patch references.
//
//   dotnet run --project tools/hkx-idpreserve -- <in.xml> [more.xml ...]

if (args.Length < 1) { Console.Error.WriteLine("usage: hkx-idpreserve <in.xml> [...]"); return 1; }

var ser = new XmlSerializer(typeof(HkPackfile));
var bad = 0;

foreach (var path in args)
{
    HkPackfile packfile;
    using (var fs = File.OpenRead(path))
        packfile = (HkPackfile)ser.Deserialize(fs)!;

    var before = packfile.Sections.SelectMany(s => s.Objects).Select(o => o.Id).ToList();

    // Same shape the editor's SerializeManager builds before writing.
    var map = new Dictionary<string, HkObject>();
    foreach (var o in packfile.Sections.SelectMany(s => s.Objects)) map[o.Id] = o;
    var round = new HkPackfile
    {
        TopLevelObject = packfile.TopLevelObject,
        Sections = new List<HkSection>
        {
            new HkSection { Name = "__data__", Objects = map.Values.OrderBy(o => o.Id).ToList() }
        }
    };

    using var ms = new MemoryStream();
    using (var w = new StreamWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        ser.Serialize(w, round);
    ms.Position = 0;
    var reread = (HkPackfile)ser.Deserialize(ms)!;
    var after = reread.Sections.SelectMany(s => s.Objects).Select(o => o.Id).ToList();

    var ok = before.OrderBy(x => x, StringComparer.Ordinal)
                   .SequenceEqual(after.OrderBy(x => x, StringComparer.Ordinal));
    var top = reread.TopLevelObject == packfile.TopLevelObject;
    Console.WriteLine($"{Path.GetFileName(path),-26} objects={before.Count,5}  " +
                      $"ids preserved={ok}  toplevelobject preserved={top} ({packfile.TopLevelObject})");
    if (!ok || !top) bad++;
}
return bad == 0 ? 0 : 1;
