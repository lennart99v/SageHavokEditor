using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using HKX2;

// ── Credits ────────────────────────────────────────────────────────────────────
// HKX2Library — MIT License
// Copyright (c) 2021 kreny  Copyright (c) 2023 ret2end
// https://github.com/ret2end/HKX2Library
// ──────────────────────────────────────────────────────────────────────────────

namespace SageHavokEditor.Core
{
    public enum HkxFormat { HKX, XML }

    /// <summary>Which Skyrim edition's packfile layout a file uses.</summary>
    public enum HkxPlatform
    {
        /// <summary>Havok XML, or a binary whose platform isn't known yet.</summary>
        Unknown = 0,
        /// <summary>Skyrim Legendary Edition / Oldrim: 32-bit pointers.</summary>
        SkyrimLE,
        /// <summary>Skyrim Special Edition: 64-bit pointers.</summary>
        SkyrimSE,
    }

    public static class HkxPlatformExtensions
    {
        public static string DisplayName(this HkxPlatform p) => p switch
        {
            HkxPlatform.SkyrimLE => "Skyrim LE (32-bit)",
            HkxPlatform.SkyrimSE => "Skyrim SE (64-bit)",
            _ => "Havok XML",
        };

        public static HKXHeader Header(this HkxPlatform p) =>
            p == HkxPlatform.SkyrimLE ? HKXHeader.SkyrimLE() : HKXHeader.SkyrimSE();
    }

    public class HkxConversionResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? XmlPath { get; init; }   // set on successful HKX→XML

        /// <summary>Platform the source binary used, when it was a binary.</summary>
        public HkxPlatform Platform { get; init; }
    }

    /// <summary>
    /// In-process conversion between Skyrim .hkx binaries and Havok XML.
    /// Uses HKX2Library (MIT) — no external executables required.
    ///
    /// Supports:
    ///   .hkx (SE amd64)  →  Havok XML    via PackFileDeserializer + XmlSerializer
    ///   .hkx (LE x86)    →  Havok XML    (same path; pointer size read from header)
    ///   Havok XML        →  .hkx (SE/LE) via XmlDeserializer + PackFileSerializer
    ///   .hkx (LE) ⇄ .hkx (SE)            via <see cref="ConvertAsync"/>
    ///
    /// LE and SE share the same Havok schema (hk_2010.2.0-r1, class version 8);
    /// only the packfile pointer size differs, so conversion between them is a
    /// pure repack that preserves content exactly.
    /// </summary>
    public class HkxConversionService
    {
        // ── Detect file format from first 4 bytes ──────────────────────────────

        public static HkxFormat DetectFormat(string path)
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            fs.Read(magic);

            // Havok packfile magic: 57 E0 E0 57
            if (magic[0] == 0x57 && magic[1] == 0xE0 &&
                magic[2] == 0xE0 && magic[3] == 0x57)
                return HkxFormat.HKX;

            // XML starts with '<' or BOM
            return HkxFormat.XML;
        }

        /// <summary>
        /// Reads the packfile header's pointer size (offset 0x10) to tell a
        /// Skyrim LE binary from a Skyrim SE one. Returns Unknown for XML.
        /// </summary>
        public static HkxPlatform DetectPlatform(string path)
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[0x11];
            if (fs.Read(head) < head.Length) return HkxPlatform.Unknown;

            if (head[0] != 0x57 || head[1] != 0xE0 ||
                head[2] != 0xE0 || head[3] != 0x57)
                return HkxPlatform.Unknown;             // XML or not a packfile

            return head[0x10] == 4 ? HkxPlatform.SkyrimLE : HkxPlatform.SkyrimSE;
        }

        // ── HKX binary → Havok XML string ─────────────────────────────────────

        /// <summary>
        /// Reads a Skyrim SE .hkx binary file and returns its Havok XML as a string.
        /// </summary>
        public async Task<string> HkxToXmlAsync(string hkxPath)
        {
            return await Task.Run(() =>
            {
                using var fs = File.OpenRead(hkxPath);
                var br = new BinaryReaderEx(fs);
                var des = new PackFileDeserializer();
                var root = (hkRootLevelContainer)des.Deserialize(br);

                using var ms = new MemoryStream();
                var xs = new HKX2.XmlSerializer();
                // Serialize with the source's own header. LE and SE agree on
                // every attribute the XML carries, so this only matters for
                // files from other Havok versions.
                xs.Serialize(root, des._header, ms);

                ms.Position = 0;
                return new StreamReader(ms, Encoding.UTF8).ReadToEnd();
            });
        }

        /// <summary>
        /// Reads a Skyrim SE .hkx file and writes Havok XML to outXmlPath.
        /// Returns the path written.
        /// </summary>
        public async Task<string> HkxToXmlFileAsync(string hkxPath, string outXmlPath)
        {
            var xml = await HkxToXmlAsync(hkxPath);
            await File.WriteAllTextAsync(outXmlPath, xml, Encoding.UTF8);
            return outXmlPath;
        }

        // ── Havok XML → HKX binary ─────────────────────────────────────────────

        /// <summary>
        /// Reads a Havok XML file and writes a .hkx binary to outHkxPath,
        /// defaulting to Skyrim SE's 64-bit layout.
        /// </summary>
        public async Task XmlToHkxAsync(string xmlPath, string outHkxPath,
            HkxPlatform target = HkxPlatform.SkyrimSE)
        {
            await Task.Run(() =>
            {
                var header = target.Header();

                using var rs = File.OpenRead(xmlPath);
                var xdes = new XmlDeserializer();
                // ignoreNonFatalError=true matches hkxconv --ignore-cast-error behavior
                var root = (hkRootLevelContainer)xdes.Deserialize(rs, header,
                                    ignoreNonFatalError: true);

                GuardUnsupportedClasses(root, target);

                using var ws = File.Create(outHkxPath);
                var bw = new BinaryWriterEx(ws);
                var ser = new PackFileSerializer();
                ser.Serialize(root, bw, header);
            });
        }

        // ── LE ⇄ SE binary conversion ──────────────────────────────────────────

        /// <summary>
        /// Repacks a .hkx from one Skyrim edition's pointer size to the other.
        /// Content is preserved exactly — only the binary layout changes.
        /// </summary>
        public async Task<HkxConversionResult> ConvertAsync(
            string inputPath, string outputPath, HkxPlatform target)
        {
            try
            {
                var source = DetectPlatform(inputPath);
                await Task.Run(() =>
                {
                    hkRootLevelContainer root;
                    using (var fs = File.OpenRead(inputPath))
                    {
                        var des = new PackFileDeserializer();
                        root = (hkRootLevelContainer)des.Deserialize(new BinaryReaderEx(fs));
                    }

                    GuardUnsupportedClasses(root, target);

                    // Write to a temp file first so a failure can't leave a
                    // half-written .hkx where the original was.
                    var tmp = outputPath + ".tmp";
                    try
                    {
                        using (var ws = File.Create(tmp))
                            new PackFileSerializer().Serialize(
                                root, new BinaryWriterEx(ws), target.Header());
                        File.Move(tmp, outputPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                });

                return new HkxConversionResult { Success = true, Platform = source };
            }
            catch (Exception ex)
            {
                return new HkxConversionResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Refuses to write a 32-bit packfile containing classes whose layout is
        /// still 64-bit only, rather than emitting a silently corrupt file.
        /// </summary>
        private static void GuardUnsupportedClasses(IHavokObject root, HkxPlatform target)
        {
            if (target != HkxPlatform.SkyrimLE) return;

            var bad = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<IHavokObject>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<IHavokObject>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var obj = stack.Pop();
                if (obj is null || !seen.Add(obj)) continue;

                var name = obj.GetType().Name;
                if (!PointerSizeSupport.Supports(name, 4)) bad.Add(name);

                foreach (var prop in obj.GetType().GetProperties())
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object? value;
                    try { value = prop.GetValue(obj); }
                    catch { continue; }

                    if (value is IHavokObject child) stack.Push(child);
                    else if (value is System.Collections.IEnumerable list and not string)
                        foreach (var item in list)
                            if (item is IHavokObject c) stack.Push(c);
                }
            }

            if (bad.Count > 0)
                throw new NotSupportedException(
                    "This file uses Havok classes whose 32-bit layout isn't supported, " +
                    "so it can't be written as a Skyrim LE file:\n\n  " +
                    string.Join("\n  ", bad) +
                    "\n\nThese are physics/ragdoll classes; behaviour, character, " +
                    "project, skeleton and animation files convert fine.");
        }

        // ── Smart open: auto-detect format, convert if needed ─────────────────

        /// <summary>
        /// Given any .hkx or .xml path, returns a path to a Havok XML file
        /// ready for your existing LoadFile pipeline.
        ///
        /// If input is already XML → returns the path as-is (no copy).
        /// If input is HKX binary  → converts to a temp XML and returns that path.
        /// </summary>
        public async Task<HkxConversionResult> PrepareXmlAsync(string inputPath)
        {
            try
            {
                var fmt = DetectFormat(inputPath);

                if (fmt == HkxFormat.XML)
                {
                    // Already XML — pass straight through
                    return new HkxConversionResult { Success = true, XmlPath = inputPath };
                }

                // Binary HKX (either edition) → convert to temp XML
                var platform = DetectPlatform(inputPath);
                var tmpDir = Path.Combine(Path.GetTempPath(), "SageHavokEditor");
                Directory.CreateDirectory(tmpDir);
                var tmpXml = Path.Combine(tmpDir,
                    Path.GetFileNameWithoutExtension(inputPath) +
                    (platform == HkxPlatform.SkyrimLE ? "_le.xml" : "_se.xml"));

                await HkxToXmlFileAsync(inputPath, tmpXml);

                return new HkxConversionResult
                {
                    Success = true, XmlPath = tmpXml, Platform = platform
                };
            }
            catch (Exception ex)
            {
                return new HkxConversionResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }
}
