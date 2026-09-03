using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using HKX2;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core.Services
{
    /// <summary>What a behaviour reference turned out to point at.</summary>
    public sealed class ReferencedBehavior
    {
        /// <summary>The path as the file writes it, e.g. <c>Behaviors\MyBehavior.hkx</c>.</summary>
        public string BehaviorName { get; init; } = "";

        /// <summary>Where it was found on disk, or null if it wasn't.</summary>
        public string? Path { get; init; }

        /// <summary>Set when the file was found but could not be read.</summary>
        public string? Error { get; init; }

        /// <summary>The referenced graph's whole event table.</summary>
        public IReadOnlyList<string> EventNames { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The subset of that table some node in the referenced graph actually
        /// refers to — a transition listening, a modifier sending. An event only
        /// declared is not evidence of anything.
        /// </summary>
        public IReadOnlyCollection<string> UsedEventNames { get; init; } =
            Array.Empty<string>();

        /// <summary>The folders that were searched, for an error message worth reading.</summary>
        public IReadOnlyList<string> Tried { get; init; } = Array.Empty<string>();

        /// <summary>The loaded graph, when the file was readable. Cached with the rest.</summary>
        public HavokManager? Manager { get; init; }

        /// <summary>
        /// The file's write time when it was read, so a cached entry can tell
        /// whether it still describes the file on disk.
        /// </summary>
        public DateTime LastWriteUtc { get; init; }

        public bool Resolved => Path != null;
        public bool Readable => Path != null && Error == null;

        /// <summary>
        /// True when this entry can no longer be trusted: the file has been
        /// written since, or it was never found and might exist by now. Both
        /// happen in the ordinary way of working — the referenced graph is
        /// usually the one being edited in the other window, and a reference is
        /// often authored before the file it names exists.
        /// </summary>
        public bool Stale
        {
            get
            {
                if (Path == null) return true;   // retry: it may have been created since
                try { return File.GetLastWriteTimeUtc(Path) != LastWriteUtc; }
                catch { return true; }
            }
        }
    }

    /// <summary>
    /// Chases <c>hkbBehaviorReferenceGenerator.behaviorName</c> to a file on disk
    /// and reads what the graph on the other side declares.
    ///
    /// The reference is the bridge node of every Nemesis/Pandora-style patch, and
    /// it is a path — not an id — resolved at runtime relative to a folder the
    /// file never names. A path that doesn't resolve is not an error anywhere: the
    /// graph converts, the game loads it, and the actor T-poses when it reaches
    /// that state. So does a reference whose events don't line up, since the two
    /// graphs link by event *name* and each keeps its own table.
    ///
    /// Reads are cached, and a cached entry is dropped when the file's write time
    /// moves — the referenced graph is usually the one being edited in the other
    /// window, so a session-long cache would answer with a file that no longer
    /// exists in that form. An unresolved entry is always retried, because
    /// authoring the reference before the file it names is the normal order of
    /// doing this. A stat per reference is what that costs.
    /// </summary>
    public sealed class BehaviorReferenceIndex
    {
        private readonly List<string> _anchors;
        private readonly Dictionary<string, ReferencedBehavior> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <param name="anchorDirectories">
        /// Folders a <c>behaviorName</c> may be relative to, best guess first. The
        /// runtime resolves against the character project's root, which is the
        /// parent of the folder holding the character file — the same rule
        /// <see cref="HavokWorkspace"/> uses for <c>behaviorFilename</c>.
        /// </param>
        public BehaviorReferenceIndex(IEnumerable<string?> anchorDirectories)
        {
            _anchors = anchorDirectories
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Every anchor folder this index will search, in order.</summary>
        public IReadOnlyList<string> Anchors => _anchors;

        /// <summary>
        /// Resolve and read one <c>behaviorName</c>. Never throws: a reference that
        /// can't be chased comes back with <see cref="ReferencedBehavior.Path"/>
        /// null or <see cref="ReferencedBehavior.Error"/> set, which is what the
        /// caller reports.
        /// </summary>
        public ReferencedBehavior Lookup(string behaviorName)
        {
            var key = (behaviorName ?? "").Trim();
            if (_cache.TryGetValue(key, out var cached) && !cached.Stale) return cached;

            var result = Read(key);
            _cache[key] = result;
            return result;
        }

        private ReferencedBehavior Read(string behaviorName)
        {
            var tried = new List<string>();
            var path = Resolve(behaviorName, tried);
            if (path == null)
                return new ReferencedBehavior { BehaviorName = behaviorName, Tried = tried };

            try
            {
                var manager = LoadManager(path);
                var stringData = manager.ObjectMap.Values
                    .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
                var names = stringData?.Params
                    .FirstOrDefault(p => p.Name == "eventNames")?.Strings ?? new List<string>();

                return new ReferencedBehavior
                {
                    BehaviorName = behaviorName,
                    Path = path,
                    Tried = tried,
                    EventNames = names,
                    UsedEventNames = UsedEvents(manager, names),
                    Manager = manager,
                    LastWriteUtc = WriteTime(path),
                };
            }
            catch (Exception ex)
            {
                return new ReferencedBehavior
                {
                    BehaviorName = behaviorName,
                    Path = path,
                    Tried = tried,
                    Error = ex.Message,
                    LastWriteUtc = WriteTime(path),
                };
            }
        }

        private static DateTime WriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// The first anchor under which the path exists. A project mid-edit often
        /// holds the referenced graph as Havok XML rather than the <c>.hkx</c> the
        /// path names, so that spelling is tried too — the reference is about which
        /// graph, not which encoding of it.
        /// </summary>
        private string? Resolve(string behaviorName, List<string> tried)
        {
            if (string.IsNullOrWhiteSpace(behaviorName)) return null;

            var spellings = new List<string> { behaviorName };
            if (behaviorName.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                spellings.Add(behaviorName[..^4] + ".xml");

            foreach (var anchor in _anchors)
                foreach (var spelling in spellings)
                {
                    var candidate = HkxPathResolver.TryCombine(anchor, spelling.TrimStart('\\', '/'));
                    if (candidate == null) continue;
                    tried.Add(candidate);
                    var found = HkxPathResolver.FindFileCaseInsensitive(candidate);
                    if (found != null) return found;
                }

            return null;
        }

        /// <summary>
        /// Load a behaviour file without going through the async conversion
        /// service: a reference read is a lookup, not a user action, and the
        /// deserialize is in-memory either way — there is no temp file to write.
        /// </summary>
        private static HavokManager LoadManager(string path)
        {
            HkPackfile packfile;

            if (HkxConversionService.DetectFormat(path) == HkxFormat.HKX)
            {
                using var fs = File.OpenRead(path);
                var des = new PackFileDeserializer();
                var root = (hkRootLevelContainer)des.Deserialize(new BinaryReaderEx(fs));

                using var ms = new MemoryStream();
                new HKX2.XmlSerializer().Serialize(root, des._header, ms);
                ms.Position = 0;
                packfile = (HkPackfile?)HkXml.Packfile.Deserialize(ms)
                           ?? throw new InvalidDataException(path);
            }
            else
            {
                using var fs = File.OpenRead(path);
                packfile = (HkPackfile?)HkXml.Packfile.Deserialize(fs)
                           ?? throw new InvalidDataException(path);
            }

            var manager = new HavokManager();
            manager.BuildGraph(packfile);
            return manager;
        }

        /// <summary>
        /// Which of a graph's declared events some node actually refers to.
        /// HavokTypeCatalog has already marked every int that is an event index —
        /// the annotation behind the property editor's name pickers — so this
        /// catches the nested sites too, and doesn't need a list of param names.
        ///
        /// Public because the comparison dialog needs the same answer about the
        /// file that is open, and "used" has to mean the same thing on both sides
        /// of a reference or the comparison is meaningless.
        /// </summary>
        public static HashSet<string> UsedEvents(HavokManager manager, IReadOnlyList<string> names)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0) return used;

            foreach (var obj in manager.ObjectMap.Values)
                foreach (var param in AllParams(obj))
                {
                    if (param.TypeInfo?.Semantic != HkParamSemantic.EventId) continue;
                    if (!int.TryParse((param.Value ?? "").Trim(), out int i)) continue;
                    if (i >= 0 && i < names.Count) used.Add(names[i]);
                }

            return used;
        }

        private static IEnumerable<HkParam> AllParams(HkObject obj)
        {
            foreach (var p in obj.Params)
            {
                yield return p;
                foreach (var c in p.Children)
                {
                    if (!string.IsNullOrEmpty(c.Id)) continue;   // a cached ref, walked in its own right
                    foreach (var cp in AllParams(c)) yield return cp;
                }
            }
        }
    }
}
