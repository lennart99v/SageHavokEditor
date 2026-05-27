using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using SkyrimHavokEditor.Models;
using SkyrimHavokEditor.Models.ViewModels;

namespace SkyrimHavokEditor.Core
{
    public enum HkFileType
    {
        Project, Character, Behavior, Animation, Skeleton, Unknown
    }

    public class HkLoadedFile
    {
        public string XmlPath { get; set; }
        public string OriginalPath { get; set; }
        public string HkxPath { get; set; }
        public bool WasHkx { get; set; }
        public HkFileType FileType { get; set; }
        public HavokManager Manager { get; set; }
        public string DisplayName =>
            Path.GetFileName(OriginalPath ?? XmlPath ?? "unknown");
    }

    public class HavokWorkspace
    {
        private readonly HkxConversionService _conv;

        public HkLoadedFile? BehaviorFile { get; private set; }
        public HkLoadedFile? CharacterFile { get; private set; }
        public HkLoadedFile? ProjectFile { get; private set; }
        public ProjectViewModel? Project { get; private set; }
        public CharacterViewModel? Character { get; private set; }

        public HavokManager? ActiveBehavior => BehaviorFile?.Manager;

        public HavokWorkspace(HkxConversionService conv) { _conv = conv; }

        // ── Public auto-detect entry point ────────────────────────────────────

        public async Task<HkFileType> LoadAutoAsync(string path)
        {
            var file = await LoadHkFileAsync(path);

            switch (file.FileType)
            {
                case HkFileType.Project:
                    ProjectFile = file;
                    Project = BuildProjectViewModel(file);
                    // Attempt to load children — but don't throw if they fail
                    await TryLoadProjectChildrenAsync(path, Project);
                    return HkFileType.Project;

                case HkFileType.Character:
                    CharacterFile = file;
                    Character = BuildCharacterViewModel(file);
                    // Do NOT auto-load behavior — caller decides
                    return HkFileType.Character;

                default:
                    BehaviorFile = file;
                    return file.FileType;
            }
        }

        // ── Explicit loaders ──────────────────────────────────────────────────

        public async Task LoadBehaviorExplicitAsync(string path)
        {
            BehaviorFile = await LoadHkFileAsync(path);
        }

        // ── Save helpers ──────────────────────────────────────────────────────

        public void SaveCharacterXml(string outPath)
        {
            if (CharacterFile == null) return;
            SerializeManager(CharacterFile.Manager, outPath);
        }

        public void SaveProjectXml(string outPath)
        {
            if (ProjectFile == null) return;
            SerializeManager(ProjectFile.Manager, outPath);
        }

        // ── Core file loader ──────────────────────────────────────────────────

        private async Task<HkLoadedFile> LoadHkFileAsync(string path)
        {
            string xmlPath; bool wasHkx = false; string hkxPath = path;

            var fmt = HkxConversionService.DetectFormat(path);
            if (fmt == HkxFormat.HKX)
            {
                var result = await _conv.PrepareXmlAsync(path);
                if (!result.Success)
                    throw new Exception($"HKX conversion failed for {Path.GetFileName(path)}:\n{result.Error}");
                xmlPath = result.XmlPath;
                wasHkx = true;
            }
            else { xmlPath = path; }

            var mgr = new HavokManager();
            var ser = new XmlSerializer(typeof(HkPackfile));
            using var fs = new FileStream(xmlPath, FileMode.Open, FileAccess.Read);
            var packfile = (HkPackfile)ser.Deserialize(fs);
            mgr.BuildGraph(packfile);

            return new HkLoadedFile
            {
                XmlPath = xmlPath,
                OriginalPath = path,
                HkxPath = hkxPath,
                WasHkx = wasHkx,
                FileType = DetectType(mgr),
                Manager = mgr
            };
        }

        // ── Project children loading ───────────────────────────────────────────

        private async Task TryLoadProjectChildrenAsync(string projectPath,
            ProjectViewModel pvm)
        {
            var projectDir = Path.GetDirectoryName(projectPath) ?? "";

            foreach (var charVm in pvm.Characters)
            {
                var relPath = charVm.File.OriginalPath;
                if (string.IsNullOrEmpty(relPath)) continue;

                // The project stores paths like "Characters\DragonTEST.hkx"
                // relative to the project file's own directory.
                var candidates = new[]
                {
                    TryCombine(projectDir, relPath),
                    TryCombine(Path.GetDirectoryName(projectDir) ?? projectDir, relPath),
                    Path.IsPathRooted(relPath) ? relPath : null
                };

                var fullPath = candidates
                    .Where(c => c != null)
                    .Select(c => FindFileCaseInsensitive(c!))
                    .FirstOrDefault(c => c != null);

                if (fullPath == null)
                {
                    charVm.Name = Path.GetFileNameWithoutExtension(relPath) + " (not found)";
                    continue;
                }

                try
                {
                    charVm.File = await LoadHkFileAsync(fullPath);
                    PopulateCharacterViewModel(charVm, charVm.File);
                    CharacterFile = charVm.File;
                    Character = charVm;

                    // Behavior path in character is relative to the character file's
                    // PARENT directory (e.g. character is in dragon/characters/,
                    // behavior is in dragon/behaviors/ — so resolve from dragon/)
                    if (!string.IsNullOrEmpty(charVm.BehaviorPath))
                    {
                        var charDir = Path.GetDirectoryName(fullPath) ?? "";
                        var parentDir = Path.GetDirectoryName(charDir) ?? charDir;

                        var behavCandidates = new[]
                        {
                            TryCombine(parentDir, charVm.BehaviorPath),  // dragon/behaviors/
                            TryCombine(charDir,   charVm.BehaviorPath),  // dragon/characters/behaviors/
                            TryCombine(projectDir, charVm.BehaviorPath), // dragon/behaviors/
                        };

                        var behavFull = behavCandidates
                            .Where(c => c != null)
                            .Select(c => FindFileCaseInsensitive(c!))
                            .FirstOrDefault(c => c != null);

                        if (behavFull != null)
                        {
                            charVm.BehaviorFile = await LoadHkFileAsync(behavFull);
                            BehaviorFile = charVm.BehaviorFile;
                        }
                    }
                }
                catch (Exception ex)
                {
                    charVm.Name = Path.GetFileNameWithoutExtension(relPath)
                                + $" (error: {ex.Message})";
                }
            }
        }

        /// <summary>Walk path segments case-insensitively. Returns real path or null.</summary>
        private static string? FindFileCaseInsensitive(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) return path; // fast path on Windows

            try
            {
                var parts = path.Replace('/', '\\').Split('\\');
                var current = parts[0] + "\\"; // "C:\"

                for (int i = 1; i < parts.Length; i++)
                {
                    if (!Directory.Exists(current)) return null;
                    var target = parts[i];
                    var isLast = i == parts.Length - 1;

                    var entries = isLast
                        ? Directory.GetFiles(current).Select(Path.GetFileName).ToArray()
                        : Directory.GetDirectories(current).Select(Path.GetFileName).ToArray();

                    var match = entries.FirstOrDefault(e =>
                        string.Equals(e, target, StringComparison.OrdinalIgnoreCase));

                    if (match == null) return null;
                    current = Path.Combine(current, match);
                }
                return File.Exists(current) ? current : null;
            }
            catch { return null; }
        }

        private static string? TryCombine(string dir, string rel)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(rel)) return null;
            try { return Path.GetFullPath(Path.Combine(dir, rel)); }
            catch { return null; }
        }

        // ── View model builders ───────────────────────────────────────────────

        private ProjectViewModel BuildProjectViewModel(HkLoadedFile file)
        {
            var mgr = file.Manager;
            var pvm = new ProjectViewModel { File = file };

            var projData = mgr.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbProjectData");
            var strData = mgr.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbProjectStringData");

            pvm.ProjectDataObj = projData;
            pvm.StringDataObj = strData;

            if (projData != null)
            {
                pvm.WorldUpWS = Get(projData, "worldUpWS");
                pvm.DefaultEventMode = Get(projData, "defaultEventMode");
            }

            if (strData != null)
            {
                // characterFilenames can be a <hkcstring> list (Strings) or
                // a space/newline-separated value string
                var charParam = strData.Params.FirstOrDefault(p =>
                    p.Name == "characterFilenames");

                List<string> charPaths;
                if (charParam?.Strings?.Count > 0)
                    charPaths = charParam.Strings;
                else
                    charPaths = (charParam?.Value ?? "")
                        .Split(new[] { '\n', '\r' },
                            StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                foreach (var cp in charPaths)
                    pvm.Characters.Add(new CharacterViewModel
                    {
                        File = new HkLoadedFile { OriginalPath = cp },
                        // Show filename while loading — will be updated after load
                        Name = Path.GetFileNameWithoutExtension(cp)
                    });
            }

            return pvm;
        }

        private CharacterViewModel BuildCharacterViewModel(HkLoadedFile file)
        {
            var vm = new CharacterViewModel { File = file };
            PopulateCharacterViewModel(vm, file);
            return vm;
        }

        private void PopulateCharacterViewModel(CharacterViewModel vm,
            HkLoadedFile file)
        {
            var mgr = file.Manager;

            var charData = mgr.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbCharacterData");
            var strData = mgr.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbCharacterStringData");

            vm.CharacterDataObj = charData;
            vm.CharacterStringDataObj = strData;

            if (charData != null)
            {
                var ccInfo = charData.Params
                    .FirstOrDefault(p => p.Name == "characterControllerInfo");
                if (ccInfo?.Children?.Count > 0)
                {
                    var child = ccInfo.Children[0];
                    if (float.TryParse(Get(child, "capsuleHeight"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float h)) vm.CapsuleHeight = h;
                    if (float.TryParse(Get(child, "capsuleRadius"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float r)) vm.CapsuleRadius = r;
                }
            }

            if (strData != null)
            {
                vm.Name = Get(strData, "name");
                vm.SkeletonPath = Get(strData, "rigName");
                vm.RagdollPath = Get(strData, "ragdollName");
                vm.BehaviorPath = Get(strData, "behaviorFilename");

                var animParam = strData.Params
                    .FirstOrDefault(p => p.Name == "animationNames");
                // Mutate the existing ObservableCollection so bound UI updates.
                // Never replace the reference — CharacterViewModel has no INPC on AnimationNames.
                vm.AnimationNames.Clear();
                if (animParam?.Strings?.Count > 0)
                {
                    foreach (var s in animParam.Strings) vm.AnimationNames.Add(s);
                }
                else if (!string.IsNullOrEmpty(animParam?.Value))
                {
                    foreach (var s in animParam.Value.Split(
                        new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        vm.AnimationNames.Add(s);
                }
            }
        }

        // ── Static helpers ────────────────────────────────────────────────────

        public static HkFileType DetectType(HavokManager mgr)
        {
            var classes = mgr.ObjectMap.Values
                .Select(o => o.ClassName).ToHashSet();

            if (classes.Contains("hkbProjectData")) return HkFileType.Project;
            if (classes.Contains("hkbCharacterData")) return HkFileType.Character;
            if (classes.Contains("hkbBehaviorGraph")) return HkFileType.Behavior;
            if (classes.Contains("hkaSkeleton")) return HkFileType.Skeleton;
            if (classes.Contains("hkaAnimationContainer")) return HkFileType.Animation;
            return HkFileType.Unknown;
        }

        private static string Get(HkObject obj, string name)
            => obj?.Params.FirstOrDefault(p => p.Name == name)?.Value ?? "";

        private static void SerializeManager(HavokManager mgr, string outPath)
        {
            var packfile = new HkPackfile
            {
                Sections = new List<HkSection>
                {
                    new HkSection
                    {
                        Name    = "__data__",
                        Objects = mgr.ObjectMap.Values.OrderBy(o => o.Id).ToList()
                    }
                }
            };
            var ser = new XmlSerializer(typeof(HkPackfile));
            var tmp = outPath + ".tmp";
            using (var w = new StreamWriter(tmp, false, System.Text.Encoding.UTF8))
                ser.Serialize(w, packfile);
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
        }
    }
}
