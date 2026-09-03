using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SageHavokEditor.Core.Validation;
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

        Check("Save anyway means save", Decide("BtnSaveAnyway", gateReport, shoot: true) == true);
        Check("Cancel save means stop", Decide("BtnCancelSave", gateReport) == false);
        // Closing the window with the X is a cancel too — a dismissed warning must
        // never be read as consent to write the file.
        Check("closing the window means stop", Decide(null, gateReport) != true);
    }

    /// <summary>
    /// Opens the gate modally, presses one of its buttons (or the window's X when
    /// <paramref name="button"/> is null) and returns the DialogResult the save
    /// path would branch on.
    /// </summary>
    private static bool? Decide(string button, GraphDoctorReport report, bool shoot = false)
    {
        var dlg = new ValidationDialog(report, "dragonbehavior.hkx");
        var driver = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        driver.Tick += (_, _) =>
        {
            if (!dlg.IsVisible) return;
            driver.Stop();
            dlg.UpdateLayout();
            if (shoot)
            {
                Check("the gate is titled for the file being saved",
                    dlg.Title == "Graph doctor — before saving dragonbehavior.hkx", dlg.Title);
                Check("the gate offers the decision, not Close",
                    ((Button)dlg.FindName("BtnSaveAnyway")).Visibility == Visibility.Visible
                    && ((Button)dlg.FindName("BtnCancelSave")).Visibility == Visibility.Visible
                    && ((Button)dlg.FindName("BtnCloseReport")).Visibility == Visibility.Collapsed);
                Check("Cancel is the default, so Enter doesn't write the file",
                    ((Button)dlg.FindName("BtnCancelSave")).IsDefault);
                Shoot(dlg, "presave-gate");
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
