using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;

// Drives the real MainWindow's one load entry point over Community Behaviors
// YAML source, which is the half tools/hkx-yaml-sniff cannot reach: the probe
// says what a path is, this says what the editor then does about it.
//
//   dotnet run --project tools/hkx-yaml-open-ui -- <a behaviour unit>.hkx
//
// The unit argument is one <name>.hkx folder of her source tree — e.g.
//   …\src_behavior\_vanilla\dragon\behaviors\dragonbehavior.hkx
//
// Everything else is synthesised next to it in a temp folder, because the three
// kinds we refuse are refused on their contents and a fixture is a better test
// subject than a file that happens to exist.
//
// LoadFileAsync opens a MessageBox for those three, which nothing inside the
// process can dismiss — so the message is built by MainWindow.UnopenableYamlMessage
// and asserted from there, and only the two paths that actually load a graph are
// driven through LoadFileAsync itself. That split is why the message lives in its
// own method.

namespace SageHavokEditor.Tools.YamlOpenUi;

internal static class Program
{
    private static int _failed;
    private static int _checks;

    private static void Check(string what, bool ok, string detail = null)
    {
        _checks++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
        if (!ok) _failed++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: hkx-yaml-open-ui <a behaviour unit>.hkx");
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // MainWindow.ApplyTheme swaps merged dictionary [0], so one has to be there
        // before the window is constructed.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SageHavokEditor;component/UI/Themes/DarkTheme.xaml")
        });

        var mw = new SageHavokEditor.MainWindow();
        mw.Show();

        mw.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            async () => await RunAsync(mw, args[0]));
        app.Run();
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAsync(SageHavokEditor.MainWindow mw, string unit)
    {
        try
        {
            if (!Directory.Exists(unit))
            {
                Console.Error.WriteLine($"not a folder: {unit}");
                _failed++;
                return;
            }

            // ── The unit opens, as it always did ──────────────────────────────
            Console.WriteLine($"Unit: {Path.GetFileName(unit)}");

            await (Task)Invoke(mw, "LoadFileAsync", unit);
            var objectsFromFolder = Manager(mw).ObjectMap.Count;
            Check($"the unit folder loads ({objectsFromFolder} objects)", objectsFromFolder > 0,
                Status(mw));

            // ── …and so does a document inside it ─────────────────────────────
            // This is new: the path handed over is a file, and a file is what the
            // old test never was. It has to load the unit, not the one document.
            var doc = Path.Combine(unit, "behavior.yaml");
            if (File.Exists(doc))
            {
                await (Task)Invoke(mw, "LoadFileAsync", doc);
                var objectsFromDoc = Manager(mw).ObjectMap.Count;
                Check("behavior.yaml loads the whole unit, not itself",
                    objectsFromDoc == objectsFromFolder,
                    $"{objectsFromDoc} vs {objectsFromFolder}");
            }
            else
            {
                Check("behavior.yaml loads the whole unit, not itself", false, "no behavior.yaml in the unit");
            }

            // ── A YAML animation named .hkx is named, not mis-parsed ──────────
            Console.WriteLine();
            Console.WriteLine("Refusals");

            var scratch = Path.Combine(Path.GetTempPath(), "hkx-yaml-open-ui",
                Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(scratch);

            var anim = Path.Combine(scratch, "WeaponThrow.hkx");
            File.WriteAllText(anim, "name: WeaponThrow\nduration: 1.5\ntracks:\n  - bone: NPC Root\n");

            var kind = YamlSourceProbe.ProbeFile(anim);
            Check("a YAML animation named .hkx reads as an animation",
                kind == YamlSourceKind.Animation, kind.ToString());

            var message = Message(anim, kind);
            Check("its message names the file", message.Contains("WeaponThrow.hkx"));
            Check("its message says what it is", message.Contains("an animation"));
            Check("its message does not say the file is corrupt",
                !message.Contains("corrupt", StringComparison.OrdinalIgnoreCase));
            Check("its message says what to do next", message.Contains("havok-core-cli compile"));

            // A character unit, which is a folder rather than a file, and points
            // the user at the behaviour instead.
            var charUnit = Path.Combine(scratch, "defaultmale.hkx");
            Directory.CreateDirectory(charUnit);
            File.WriteAllText(Path.Combine(charUnit, "character.yaml"),
                "character:\n  name: DefaultMale\n  behavior: \"Behaviors\\\\0_Master.hkx\"\n");

            var charKind = YamlSourceProbe.ProbeUnit(charUnit);
            Check("a character unit reads as a character", charKind == YamlSourceKind.Character,
                charKind.ToString());
            Check("its message points at the behaviour instead",
                Message(charUnit, charKind).Contains("behaviors\\"));

            // ── And the graph the editor already had is still there ───────────
            // A refusal must not half-load: the window keeps whatever was open.
            Check("a refused open leaves the loaded graph alone",
                Manager(mw).ObjectMap.Count == objectsFromFolder,
                $"{Manager(mw).ObjectMap.Count}");

            try { Directory.Delete(scratch, true); } catch { }

            Console.WriteLine();
            Console.WriteLine(_failed == 0
                ? $"All {_checks} checks passed."
                : $"{_failed} of {_checks} checks FAILED.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            _failed++;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static string Message(string path, YamlSourceKind kind) =>
        (string)typeof(SageHavokEditor.MainWindow)
            .GetMethod("UnopenableYamlMessage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { path, kind });

    private static HavokManager Manager(SageHavokEditor.MainWindow mw) =>
        (HavokManager)Member(mw, "manager");

    private static string Status(SageHavokEditor.MainWindow mw)
    {
        var t = Member(mw, "StatusText");
        return (string)t.GetType().GetProperty("Text")!.GetValue(t);
    }

    private static object Invoke(SageHavokEditor.MainWindow mw, string method, params object[] args) =>
        typeof(SageHavokEditor.MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mw, args);

    private static object Member(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p != null) return p.GetValue(o);
        }
        throw new MissingMemberException(o.GetType().Name, name);
    }
}
