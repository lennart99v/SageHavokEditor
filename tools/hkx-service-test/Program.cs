using SageHavokEditor.Core;

// End-to-end check of the editor's own HkxConversionService — the code the
// Open, Save and "LE <-> SE" commands call.
//
//   dotnet run --project tools/hkx-service-test -- <le-file.hkx> <se-file.hkx>

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: hkx-service-test <le-file.hkx> <se-file.hkx>");
    return 1;
}

var lePath = args[0];
var sePath = args[1];
var conv = new HkxConversionService();
var tmp = Path.Combine(Path.GetTempPath(), "hkx-service-test");
Directory.CreateDirectory(tmp);

var failed = 0;

void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

Console.WriteLine("== format / platform detection ==");
Check("LE file detected as HKX",
    HkxConversionService.DetectFormat(lePath) == HkxFormat.HKX);
Check("LE file detected as Skyrim LE",
    HkxConversionService.DetectPlatform(lePath) == HkxPlatform.SkyrimLE,
    HkxConversionService.DetectPlatform(lePath).DisplayName());
Check("SE file detected as Skyrim SE",
    HkxConversionService.DetectPlatform(sePath) == HkxPlatform.SkyrimSE,
    HkxConversionService.DetectPlatform(sePath).DisplayName());

Console.WriteLine("== opening an LE file (what Load does) ==");
var prep = await conv.PrepareXmlAsync(lePath);
Check("PrepareXmlAsync succeeds on LE", prep.Success, prep.Error);
Check("reports LE platform", prep.Platform == HkxPlatform.SkyrimLE);
Check("produced readable Havok XML",
    prep.XmlPath is not null && File.Exists(prep.XmlPath) &&
    File.ReadAllText(prep.XmlPath).Contains("hkbBehaviorGraph"));

var leXml = await conv.HkxToXmlAsync(lePath);

Console.WriteLine("== LE -> SE conversion ==");
var toSe = Path.Combine(tmp, "converted_se.hkx");
var r1 = await conv.ConvertAsync(lePath, toSe, HkxPlatform.SkyrimSE);
Check("ConvertAsync LE->SE succeeds", r1.Success, r1.Error);
Check("output is a Skyrim SE binary",
    HkxConversionService.DetectPlatform(toSe) == HkxPlatform.SkyrimSE);
Check("content preserved exactly (XML identical)",
    await conv.HkxToXmlAsync(toSe) == leXml);

Console.WriteLine("== SE -> LE conversion ==");
var backToLe = Path.Combine(tmp, "converted_back_le.hkx");
var r2 = await conv.ConvertAsync(toSe, backToLe, HkxPlatform.SkyrimLE);
Check("ConvertAsync SE->LE succeeds", r2.Success, r2.Error);
Check("output is a Skyrim LE binary",
    HkxConversionService.DetectPlatform(backToLe) == HkxPlatform.SkyrimLE);
Check("round-trips back to the original content",
    await conv.HkxToXmlAsync(backToLe) == leXml);

Console.WriteLine("== saving XML as either edition (what Save does) ==");
var xmlPath = Path.Combine(tmp, "roundtrip.xml");
await conv.HkxToXmlFileAsync(lePath, xmlPath);

var savedLe = Path.Combine(tmp, "saved_le.hkx");
await conv.XmlToHkxAsync(xmlPath, savedLe, HkxPlatform.SkyrimLE);
Check("XML -> LE hkx", HkxConversionService.DetectPlatform(savedLe) == HkxPlatform.SkyrimLE);
Check("XML -> LE preserves content", await conv.HkxToXmlAsync(savedLe) == leXml);

var savedSe = Path.Combine(tmp, "saved_se.hkx");
await conv.XmlToHkxAsync(xmlPath, savedSe, HkxPlatform.SkyrimSE);
Check("XML -> SE hkx", HkxConversionService.DetectPlatform(savedSe) == HkxPlatform.SkyrimSE);
Check("XML -> SE preserves content", await conv.HkxToXmlAsync(savedSe) == leXml);

Console.WriteLine();
Console.WriteLine(failed == 0 ? "ALL CHECKS PASSED" : $"{failed} CHECK(S) FAILED");
return failed == 0 ? 0 : 1;
