using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Animation;
using SageHavokEditor.UI;

namespace SageHavokEditor.Core.Animation
{
    /// <summary>
    /// Resolves a clip's animation + skeleton paths against search roots, converts
    /// both HKX→XML (session-cached, full-path keyed), parses, and returns a clip
    /// ready to sample. SE only for now.
    /// </summary>
    public class ClipPreviewService
    {
        private readonly HkxConversionService _conv;
        private readonly ConcurrentDictionary<string, string> _xmlCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        private Skeleton? _skeleton;
        private string? _skeletonSource;

        public ClipPreviewService(HkxConversionService conv) => _conv = conv;

        public void Reset()
        {
            _xmlCache.Clear();
            _skeleton = null;
            _skeletonSource = null;
        }

        public sealed class PreviewResult
        {
            public bool Success { get; init; }
            public string? Error { get; init; }
            public AnimationClip? Clip { get; init; }
            public Skeleton? Skeleton { get; init; }
            public bool TrackCountMismatch { get; init; }
        }

        public async Task<PreviewResult> LoadClipAsync(
            string animPath, string skeletonPath, params string[] searchRoots)
        {
            try
            {
                var animFull = ResolvePath(animPath, searchRoots);
                if (animFull == null) return Fail($"Animation not found: {animPath}");

                var skeleFull = ResolvePath(skeletonPath, searchRoots);
                if (skeleFull == null) return Fail($"Skeleton not found: {skeletonPath}");

                if (!await IsSkyrimSeAsync(animFull))
                    return Fail("Not Skyrim SE (64-bit) HKX. LE support is planned.");

                var skeleton = await GetSkeletonAsync(skeleFull);
                var animXml = await GetXmlAsync(animFull);

                var clip = await Task.Run(() => HavokAnimationParser.Parse(animXml, skeleton));

                return new PreviewResult
                {
                    Success = true,
                    Clip = clip,
                    Skeleton = skeleton,
                    TrackCountMismatch = clip.TrackCountExceedsBones
                };
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private async Task<Skeleton> GetSkeletonAsync(string skeleFull)
        {
            if (_skeleton != null &&
                string.Equals(_skeletonSource, skeleFull, StringComparison.OrdinalIgnoreCase))
                return _skeleton;

            var xml = await GetXmlAsync(skeleFull);
            var sk = await Task.Run(() => SkeletonParser.Parse(xml));
            _skeleton = sk;
            _skeletonSource = skeleFull;
            return sk;
        }

        private async Task<string> GetXmlAsync(string sourceFull)
        {
            if (_xmlCache.TryGetValue(sourceFull, out var cached) && File.Exists(cached))
                return cached;

            var gate = _locks.GetOrAdd(sourceFull, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (_xmlCache.TryGetValue(sourceFull, out cached) && File.Exists(cached))
                    return cached;

                var res = await _conv.PrepareXmlAsync(sourceFull);
                if (!res.Success || res.XmlPath == null) throw new Exception(res.Error ?? "Conversion failed");

                if (!string.Equals(res.XmlPath, sourceFull, StringComparison.OrdinalIgnoreCase))
                {
                    var unique = UniqueCachePath(sourceFull);
                    File.Copy(res.XmlPath, unique, overwrite: true);
                    _xmlCache[sourceFull] = unique;
                    return unique;
                }
                _xmlCache[sourceFull] = res.XmlPath;
                return res.XmlPath;
            }
            finally { gate.Release(); }
        }

        private static string UniqueCachePath(string sourceFull)
        {
            var dir = Path.Combine(Path.GetTempPath(), "SageHavokEditor", "clipcache");
            Directory.CreateDirectory(dir);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sourceFull.ToLowerInvariant())))[..12];
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(sourceFull) + "_" + hash + ".xml");
        }

        private async Task<bool> IsSkyrimSeAsync(string hkxFull)
        {
            if (HkxConversionService.DetectFormat(hkxFull) == HkxFormat.XML) return true;
            return await Task.FromResult(true);   // LE seam: read header version here later
        }

        private static string? ResolvePath(string stored, string[] roots)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            if (Path.IsPathRooted(stored) && File.Exists(stored)) return stored;

            var rel = stored.Replace('/', '\\').TrimStart('\\');
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(root, rel));
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private static PreviewResult Fail(string msg) => new() { Success = false, Error = msg };
    }
}

namespace SageHavokEditor.UI
{
    public class ClipPreviewWindow : Window
    {
        public readonly ClipPreviewView View = new();
        public ClipPreviewWindow()
        {
            Title = "Clip Preview";
            Width = 520; Height = 480;
            Background = System.Windows.Media.Brushes.Black;
            Content = View;
        }
    }
}
