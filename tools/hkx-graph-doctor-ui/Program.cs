using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SageHavokEditor.Core.Validation;
using SageHavokEditor.Models;
using SageHavokEditor.UI;
using SageHavokEditor.UI.Dialogs;

// Drives the graph doctor's two faces through the real MainWindow and the real
// ValidationDialog: the read-out that ✓ Validate opens, and the pre-save gate
// that decides whether the save goes ahead.
//
//   dotnet run --project tools/hkx-graph-doctor-ui -- <behavior.xml> [--shot <dir>]
//
// The checks in tools/hkx-graph-doctor prove what the pass *finds*; these prove
// that what it finds reaches the screen and that the buttons mean what they say.
// The one thing not driven here is BtnSave_Click itself: it opens an OS
// SaveFileDialog before the doctor runs, and nothing can dismiss that from
// inside the process — so the gate dialog is opened the same way the save path
// opens it, and its DialogResult (which is the save path's whole decision) is
// checked on both buttons.
//
// A "splines=curved not supported in dot" line from the graph renderer is noise.

namespace SageHavokEditor.Tools.GraphDoctorUi;

internal static class Program
{
    private static int _failed;
    private static string _shotDir;

    private static void Check(string what, bool ok, string detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
        if (!ok) _failed++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: hkx-graph-doctor-ui <behavior.xml> [--shot <dir>]");
            return 2;
        }
        var path = args[0];
        var shotIdx = Array.IndexOf(args, "--shot");
        if (shotIdx >= 0 && shotIdx + 1 < args.Length) _shotDir = args[shotIdx + 1];

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SageHavokEditor;component/UI/Themes/DarkTheme.xaml")
        });

        var mw = new SageHavokEditor.MainWindow();
        mw.Show();

        mw.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () => await RunAsync(mw, path));
        app.Run();
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAsync(SageHavokEditor.MainWindow mw, string path)
    {
        try
        {
            await (Task)Invoke(mw, "LoadFileAsync", path);
            var manager = (SageHavokEditor.Core.HavokManager)Member(mw, "manager");
            Console.WriteLine($"loaded {Path.GetFileName(path)} — {manager.ObjectMap.Count} objects");

            var report = (GraphDoctorReport)Invoke(mw, "RunGraphDoctor");
            Console.WriteLine($"  {report.Headline}");
            // Fewer errors than tools/hkx-graph-doctor reports on the same file is
            // expected: loading through MainWindow pads a short eventInfos array
            // back out to the eventNames count (vanilla dragonbehavior ships 486
            // names against 482 infos), so one desync error is repaired on the way in.

            ReportPath(mw, report);
            GatePath(report);
            BaselinePath(mw, manager);
            RefusalPath(report);
            BehaviorReferencePath(mw, manager);
        }
        catch (Exception ex)
        {
            Check("ran without throwing", false, ex.ToString());
        }

        Console.WriteLine(_failed == 0 ? "\nall checks passed" : $"\n{_failed} check(s) FAILED");
        Environment.Exit(_failed == 0 ? 0 : 1);
    }

    // -- ✓ Validate: the read-out ---------------------------------------------
    private static void ReportPath(SageHavokEditor.MainWindow mw, GraphDoctorReport report)
    {
        Console.WriteLine("the ✓ Validate read-out:");

        Invoke(mw, "BtnValidate_Click", null, new RoutedEventArgs());
        var dlg = Application.Current.Windows.OfType<ValidationDialog>().Single();
        dlg.UpdateLayout();
        Shoot(dlg, "validate-report");

        Check("shows every issue the doctor found",
            List(dlg).Items.Count == report.Issues.Count,
            $"{List(dlg).Items.Count} vs {report.Issues.Count}");
        Check("headline says what was found",
            ((TextBlock)dlg.FindName("HeadlineText")).Text == report.Headline);
        Check("counts match the report",
            ((TextBlock)dlg.FindName("ErrorCountText")).Text == report.ErrorCount.ToString()
            && ((TextBlock)dlg.FindName("WarningCountText")).Text == report.WarningCount.ToString());
        Check("errors are listed before warnings",
            !report.Issues.SkipWhile(i => i.IsError).Any(i => i.IsError));

        // Not decoration: this dialog is the only route from "an object is broken"
        // to "the object is in front of you", and the wiring moved to a shared
        // helper when the save path started opening the same dialog.
        // The item has to come out of the dialog's own list: BtnValidate_Click ran
        // its own pass, so the issues here are equal-looking but different objects,
        // and a SelectedItem the ListBox doesn't contain selects nothing.
        var first = List(dlg).Items.Cast<ValidationIssue>()
            .FirstOrDefault(i => !string.IsNullOrEmpty(i.ObjectId));
        List(dlg).SelectedItem = first;
        var shown = ((TextBlock)Member(mw, "SelectedClassName")).Text;
        Check("clicking an issue opens its object in the editor",
            first != null && shown == $"Class: {first.ObjectClass}", shown);

        // Filtering is what makes a long report usable, and it reads IsError.
        ((RadioButton)dlg.FindName("FilterErrors")).IsChecked = true;
        Check("the Errors-only filter shows exactly the errors",
            List(dlg).Items.Count == report.ErrorCount,
            $"{List(dlg).Items.Count} vs {report.ErrorCount}");

        Check("the read-out offers Close, not a save decision",
            ((Button)dlg.FindName("BtnCloseReport")).Visibility == Visibility.Visible
            && ((Button)dlg.FindName("BtnSaveAnyway")).Visibility == Visibility.Collapsed);

        dlg.Close();
    }

    // -- The pre-save gate ----------------------------------------------------
    private static void GatePath(GraphDoctorReport report)
    {
        Console.WriteLine("the pre-save gate:");

        // Opened exactly as BtnSave_Click opens it: the type-check issues are
        // already handled by then, so the gate gets the rest.
        var rest = report.Issues
            .Where(i => i.Category != ValidationIssue.CategoryType).ToList();
        var gateReport = new GraphDoctorReport { Issues = rest, PrunedCount = report.PrunedCount };

        Check("this file has something for the gate to say",
            rest.Any(i => i.IsError) || report.PrunedCount > 0,
            $"{rest.Count(i => i.IsError)} errors, {report.PrunedCount} pruned");

        Check("Save anyway means save", Decide("BtnSaveAnyway", gateReport, shoot: "presave-gate") == true);
        Check("Cancel save means stop", Decide("BtnCancelSave", gateReport) == false);
        // Closing the window with the X is a cancel too — a dismissed warning must
        // never be read as consent to write the file.
        Check("closing the window means stop", Decide(null, gateReport) != true);
    }

    // -- The load-time baseline ----------------------------------------------
    // The refusal is baseline-relative, so a load that forgets to take one would
    // quietly disarm it — or, if the field defaulted the other way, refuse every
    // save of a vanilla file. Neither shows up anywhere except here.
    private static void BaselinePath(SageHavokEditor.MainWindow mw, SageHavokEditor.Core.HavokManager manager)
    {
        Console.WriteLine("the load-time structural baseline:");

        var baseline = (HashSet<string>)Member(mw, "_structuralBaseline");
        Check("loading the file took one", baseline != null);
        if (baseline == null) return;

        var report = (GraphDoctorReport)Invoke(mw, "RunGraphDoctor");
        Check("it holds exactly the structural errors the file arrived with",
            baseline.SetEquals(report.StructuralFingerprints()),
            $"{baseline.Count} vs {report.StructuralErrors.Count}");
        Check("so an untouched file has nothing newly broken to refuse over",
            report.StructuralErrors.All(i => baseline.Contains(i.Fingerprint)));

        // Break a generator the way the property editor would, and check the save
        // path's own expression now sees exactly one thing to refuse over.
        var state = manager.ObjectMap.Values.First(o =>
            o.ClassName == "hkbStateMachineStateInfo"
            && (o.Params.FirstOrDefault(p => p.Name == "generator")?.Value ?? "").StartsWith("#"));
        var param = state.Params.First(p => p.Name == "generator");
        var oldValue = param.Value;
        var oldChildren = param.Children.ToList();
        param.Children.Clear();
        param.Value = "null";
        try
        {
            var after = (GraphDoctorReport)Invoke(mw, "RunGraphDoctor");
            var newlyBroken = after.StructuralErrors
                .Where(i => !baseline.Contains(i.Fingerprint)).ToList();
            Check("nulling a generator is one newly broken thing",
                newlyBroken.Count == 1 && newlyBroken[0].ObjectId == state.Id,
                string.Join("; ", newlyBroken.Select(i => i.Fingerprint)));
            if (newlyBroken.Count > 0)
                Console.WriteLine($"  → {after.RefusalLine(newlyBroken[0])}");
        }
        finally
        {
            param.Children.Clear();
            foreach (var c in oldChildren) param.Children.Add(c);
            param.Value = oldValue;
        }
    }

    // -- The refusal ----------------------------------------------------------
    private static void RefusalPath(GraphDoctorReport report)
    {
        Console.WriteLine("the refusal:");

        // Whatever this file's own structural errors are, the refusal shows the
        // ones the save would have been refused over — here, all of them.
        var refused = new GraphDoctorReport
        {
            Issues = report.StructuralErrors,
            GraphName = report.GraphName,
        };
        Check("this file has structural errors to refuse over", refused.Issues.Count > 0);

        var dlg = new ValidationDialog(refused, ValidationDialogMode.SaveRefused, "dragonbehavior.hkx",
            $"This graph contradicts itself in {refused.Issues.Count} places that the file didn't when " +
            "you opened it, so it was not written as SE HKX. Havok would accept it and say nothing.");
        dlg.Show();
        dlg.UpdateLayout();
        Shoot(dlg, "save-refused");

        Check("titled as a refusal, naming the file",
            dlg.Title == "Save refused — dragonbehavior.hkx", dlg.Title);
        // The whole point of this mode: there is no way to overrule it from here.
        Check("offers no way to save anyway",
            ((Button)dlg.FindName("BtnSaveAnyway")).Visibility != Visibility.Visible
            && ((Button)dlg.FindName("BtnCancelSave")).Visibility != Visibility.Visible
            && ((Button)dlg.FindName("BtnCloseReport")).Visibility == Visibility.Visible);
        Check("every row it lists names a likely cause",
            List(dlg).Items.Cast<ValidationIssue>().All(i => i.HasCause));

        dlg.Close();
    }

    // -- Following a behaviour reference --------------------------------------
    // The one node in a graph whose subject is a different file. Double-clicking
    // it has to reach the window, which is the only thing that knows where the
    // project sits on disk — the graph view holds a manager, not a folder.
    private static void BehaviorReferencePath(SageHavokEditor.MainWindow mw,
        SageHavokEditor.Core.HavokManager manager)
    {
        Console.WriteLine("following a behaviour reference:");

        Check("loading the file built a reference index", Member(mw, "_behaviorRefs") != null);

        var graph = (StateMachineGraphView)Member(mw, "GraphView");
        var wiring = typeof(StateMachineGraphView).GetField(
            "OpenBehaviorReferenceRequested", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("the window is listening for the open request",
            wiring?.GetValue(graph) != null);

        // Put a reference into the graph and drill into it the way a double-click
        // does. Nothing in these sample files has one.
        var host = manager.ObjectMap.Values.First(o =>
            o.ClassName == "hkbStateMachineStateInfo"
            && (o.Params.FirstOrDefault(p => p.Name == "generator")?.Value ?? "").StartsWith("#"));
        var generator = host.Params.First(p => p.Name == "generator");
        var oldValue = generator.Value;
        var oldChildren = generator.Children.ToList();

        const string wanted = @"Behaviors\UiTest.hkx";
        var reference = new HkObject { Id = "#9401", ClassName = "hkbBehaviorReferenceGenerator" };
        reference.Params.Add(new HkParam { Name = "name", Value = "UiTest_Reference" });
        reference.Params.Add(new HkParam { Name = "behaviorName", Value = wanted });
        manager.ObjectMap[reference.Id] = reference;
        generator.Children.Clear();
        generator.Value = reference.Id;

        // Take the window's own handler off first. It is the thing being proved
        // present a few lines up — and if it runs here it resolves the made-up
        // path, fails, and opens a modal MessageBox nothing in this process can
        // dismiss. Restored in the finally.
        var windowHandler = wiring?.GetValue(graph);
        wiring?.SetValue(graph, null);

        string asked = null;
        Action<string> listener = name => asked = name;
        graph.OpenBehaviorReferenceRequested += listener;

        void Drill(GraphNode n) => typeof(StateMachineGraphView)
            .GetMethod("DrillInto", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(graph, new object[] { n });

        try
        {
            var node = new GraphNode { Id = reference.Id, Name = "UiTest_Reference" };
            Drill(node);
            Check("drilling in asks the window to open the file it names",
                asked == wanted, asked ?? "(nothing asked)");

            // A reference with no path has nothing to open, and must not send the
            // window off to resolve an empty string.
            asked = null;
            reference.Params[1].Value = "";
            Drill(node);
            Check("a reference with no path asks for nothing", asked == null, asked);
        }
        finally
        {
            wiring?.SetValue(graph, windowHandler);
            manager.ObjectMap.Remove(reference.Id);
            generator.Children.Clear();
            foreach (var c in oldChildren) generator.Children.Add(c);
            generator.Value = oldValue;
        }
    }

    /// <summary>
    /// Opens the gate modally, presses one of its buttons (or the window's X when
    /// <paramref name="button"/> is null) and returns the DialogResult the save
    /// path would branch on.
    /// </summary>
    private static bool? Decide(string button, GraphDoctorReport report, string shoot = null)
    {
        var dlg = new ValidationDialog(report, ValidationDialogMode.PreSaveDecision, "dragonbehavior.hkx");
        var driver = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        driver.Tick += (_, _) =>
        {
            if (!dlg.IsVisible) return;
            driver.Stop();
            dlg.UpdateLayout();
            if (shoot != null)
            {
                Check("the gate is titled for the file being saved",
                    dlg.Title == "Graph doctor — before saving dragonbehavior.hkx", dlg.Title);
                Check("the gate offers the decision, not Close",
                    ((Button)dlg.FindName("BtnSaveAnyway")).Visibility == Visibility.Visible
                    && ((Button)dlg.FindName("BtnCancelSave")).Visibility == Visibility.Visible
                    && ((Button)dlg.FindName("BtnCloseReport")).Visibility == Visibility.Collapsed);
                Check("Cancel is the default, so Enter doesn't write the file",
                    ((Button)dlg.FindName("BtnCancelSave")).IsDefault);
                Shoot(dlg, shoot);
            }
            if (button == null) dlg.Close();
            else ((Button)dlg.FindName(button)).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };
        driver.Start();
        return dlg.ShowDialog();
    }

    // -- Helpers --------------------------------------------------------------
    private static ListBox List(ValidationDialog d) => (ListBox)d.FindName("IssueList");

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

    /// <summary>--shot writes a PNG of the dialog, for eyeballing the layout.</summary>
    private static void Shoot(Window w, string tag)
    {
        if (_shotDir == null) return;
        Directory.CreateDirectory(_shotDir);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth),
            (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var file = Path.Combine(_shotDir, $"{tag}.png");
        using var fs = File.Create(file);
        enc.Save(fs);
        Console.WriteLine($"  wrote {file}");
    }
}
