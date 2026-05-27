using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using HKX2;

// ── Credits ────────────────────────────────────────────────────────────────────
// HKX2Library — MIT License
// Copyright (c) 2021 kreny  Copyright (c) 2023 ret2end
// https://github.com/ret2end/HKX2Library
// ──────────────────────────────────────────────────────────────────────────────

namespace SkyrimHavokEditor.Core
{
    public enum HkxFormat { HKX, XML }

    public class HkxConversionResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? XmlPath { get; init; }   // set on successful HKX→XML
    }

    /// <summary>
    /// In-process conversion between Skyrim SE 64-bit .hkx binary and Havok XML.
    /// Uses HKX2Library (MIT) — no external executables required.
    ///
    /// Supports:
    ///   .hkx (SE amd64)  →  Havok XML    via PackFileDeserializer + XmlSerializer
    ///   Havok XML         →  .hkx (SE)   via XmlDeserializer + PackFileSerializer
    ///   Havok XML         →  loaded directly (your existing pipeline)
    /// </summary>
    public class HkxConversionService
    {
        // Always target Skyrim SE (amd64, little-endian, 8-byte pointers)
        private static HKXHeader SeHeader => HKXHeader.SkyrimSE();

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
                xs.Serialize(root, SeHeader, ms);

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
        /// Reads a Havok XML file and writes a Skyrim SE .hkx binary to outHkxPath.
        /// </summary>
        public async Task XmlToHkxAsync(string xmlPath, string outHkxPath)
        {
            await Task.Run(() =>
            {
                using var rs = File.OpenRead(xmlPath);
                var xdes = new XmlDeserializer();
                // ignoreNonFatalError=true matches hkxconv --ignore-cast-error behavior
                var root = (hkRootLevelContainer)xdes.Deserialize(rs, SeHeader,
                                    ignoreNonFatalError: true);

                using var ws = File.Create(outHkxPath);
                var bw = new BinaryWriterEx(ws);
                var ser = new PackFileSerializer();
                ser.Serialize(root, bw, SeHeader);
            });
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

                // Binary HKX → convert to temp XML
                var tmpDir = Path.Combine(Path.GetTempPath(), "SkyrimHavokEditor");
                Directory.CreateDirectory(tmpDir);
                var tmpXml = Path.Combine(tmpDir,
                    Path.GetFileNameWithoutExtension(inputPath) + "_se.xml");

                await HkxToXmlFileAsync(inputPath, tmpXml);

                return new HkxConversionResult { Success = true, XmlPath = tmpXml };
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
