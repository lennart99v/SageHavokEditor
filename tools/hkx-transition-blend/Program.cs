using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SageHavokEditor.Core;
using SageHavokEditor.Models;
using SageHavokEditor.UI.Dialogs;

// Checks the Add / Edit Transition dialog's Blend row against a real behaviour
// file, by driving the real MainWindow rather than a copy of its logic: load the
// file, open the dialog on an actual state machine, choose "New blending
// effect", confirm, and assert what landed in the object graph - then undo.
//
//   dotnet run --project tools/hkx-transition-blend -- <behavior.xml> [--shot <dir>]
//
// Unlike the other harnesses, this one cannot compile the code under test into
// itself: the logic lives in MainWindow.OpenTransitionDialog, which only exists
// as part of the WPF app. So it references the editor, hosts a real Application,
// and reaches the private members it needs by reflection. That is also why it is
// a WinExe with an explicit STAThread Main rather than top-level statements.
//
// What it guards, all of which is silent when it breaks:
//   * the created hkbBlendingTransitionEffect keeps Havok's defaults and gets
//     the duration that was typed
//   * the new effect and the transition array do not collide on an id -
//     GenerateNewObjectId() reads ObjectMap, so an object created but not yet
//     registered hands its id to the next caller, and the second write wins
//   * the transition actually points at the effect, on the right state
//   * the Edit path repoints an existing transition and keeps Children in sync
//   * undo removes both, and restores the previous effect ref
//
// A "splines=curved not supported in dot" line from the graph renderer is noise.

namespace SageHavokEditor.Tools.TransitionBlend;

internal static class Program
{
    private const string TypedDuration = "0.25";
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
            Console.Error.WriteLine("usage: hkx-transition-blend <behavior.xml> [--shot <dir>]");
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

        StartDialogDriver();
        mw.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () => await RunAsync(mw, path));
        app.Run();
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Fills in and confirms any SmTransitionDialog that appears. Each dialog is
    /// handled once: the timer keeps ticking while one is up, and a second Confirm
    /// dispatched after it closed throws ("DialogResult can be set only after...").
    /// </summary>
    private static void StartDialogDriver()
    {
        var handled = new HashSet<Window>();
        var driver = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        driver.Tick += (_, _) =>
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w is not SmTransitionDialog d || !d.IsVisible || !handled.Add(d)) continue;

                ((ComboBox)d.FindName("EffectCombo")).SelectedValue = SmTransitionDialog.NewBlendEffectKey;
                ((TextBox)d.FindName("BlendDurationBox")).Text = TypedDuration;
                d.UpdateLayout();
                Shoot(d, $"dialog-{handled.Count}");

                d.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    FindConfirm(d)?.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
                return;
            }
        };
        driver.Start();
    }

    private static async Task RunAsync(SageHavokEditor.MainWindow mw, string path)
    {
        try
        {
            await (Task)Invoke(mw, "LoadFileAsync", path);
            var manager = (HavokManager)Member(mw, "manager");
            Console.WriteLine($"loaded {Path.GetFileName(path)} - {manager.ObjectMap.Count} objects");

            AddPath(mw, manager);
            EditPath(mw, manager);
        }
        catch (Exception ex)
        {
            Check("ran without throwing", false, ex.ToString());
        }

        Console.WriteLine(_failed == 0 ? "\nall checks passed" : $"\n{_failed} check(s) FAILED");
        Environment.Exit(_failed == 0 ? 0 : 1);
    }

    // -- Add a transition, creating its blend --------------------------------
    private static void AddPath(SageHavokEditor.MainWindow mw, HavokManager manager)
    {
        Console.WriteLine("adding a transition with a new blend:");

        var sm = manager.ObjectMap.Values.First(o => o.ClassName == "hkbStateMachine"
            && HkRefList.Tokens(o.Params.FirstOrDefault(p => p.Name == "states")?.Value).Length > 1);
        var state = manager.ObjectMap[HkRefList.Tokens(sm.Params.First(p => p.Name == "states").Value)[0]];
        mw.SelectedSM = sm;
        Console.WriteLine($"  machine '{sm.DisplayName}', from state '{state.DisplayName}'");

        var before = manager.ObjectMap.Keys.ToHashSet();
        Invoke(mw, "OpenTransitionDialog", true, state);   // modal; the driver confirms it

        var added = manager.ObjectMap.Values.Where(o => !before.Contains(o.Id)).ToList();
        var effect = added.FirstOrDefault(o => o.ClassName == "hkbBlendingTransitionEffect");
        Check("exactly one new hkbBlendingTransitionEffect",
            added.Count(o => o.ClassName == "hkbBlendingTransitionEffect") == 1,
            string.Join(", ", added.Select(o => $"{o.Id} {o.ClassName}")));
        // The id collision itself shows up in the two checks above and below — the
        // effect overwrote the array in ObjectMap, so the array stopped existing and
        // nothing pointed at the effect. This one catches the other half of the same
        // mistake: two new objects both landing, under one id.
        Check("no two new objects share an id",
            added.Select(o => o.Id).Distinct().Count() == added.Count,
            string.Join(", ", added.Select(o => o.Id)));

        if (effect != null)
        {
            Check("duration is what was typed", Param(effect, "duration") == "0.250000",
                Param(effect, "duration"));
            Check("named after its duration", Param(effect, "name") == "Blend_250ms",
                Param(effect, "name"));
            Check("Havok's own defaults carried over from HKX2",
                Param(effect, "blendCurve") == "BLEND_CURVE_SMOOTH"
                && Param(effect, "endMode") == "END_MODE_NONE"
                && Param(effect, "selfTransitionMode")
                   == "SELF_TRANSITION_MODE_CONTINUE_IF_CYCLIC_BLEND_IF_ACYCLIC");
        }

        var (owner, transition) = FindTransitionPointingAt(manager, effect?.Id);
        Check("a transition points at the new effect", transition != null,
            owner == null ? null : $"in {owner.Id}");
        Check("on the array hanging off the state we transitioned from",
            owner != null && state.Params.Any(p => p.Name == "transitions" && p.Value == owner.Id),
            state.Params.FirstOrDefault(p => p.Name == "transitions")?.Value);

        Undo(mw);
        Check("undo removes the effect", effect != null && !manager.ObjectMap.ContainsKey(effect.Id));
        Check("undo removes the transition",
            FindTransitionPointingAt(manager, effect?.Id).Transition == null);
    }

    // -- Give an existing transition a new blend -----------------------------
    private static void EditPath(SageHavokEditor.MainWindow mw, HavokManager manager)
    {
        Console.WriteLine("editing an existing transition onto a new blend:");

        object row = null;
        foreach (var r in (System.Collections.IEnumerable)Member(mw, "SmTransitionRows"))
        {
            var child = (HkObject)r.GetType().GetProperty("TransitionChild")!.GetValue(r);
            if (child?.Params.Any(p => p.Name == "transition") == true) { row = r; break; }
        }
        Check("found a transition row to edit", row != null);
        if (row == null) return;

        var editChild = (HkObject)row.GetType().GetProperty("TransitionChild")!.GetValue(row);
        var editParam = editChild.Params.First(p => p.Name == "transition");
        var oldRef = editParam.Value;
        typeof(SageHavokEditor.MainWindow)
            .GetField("_selectedSmRow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mw, row);

        var before = manager.ObjectMap.Keys.ToHashSet();
        Invoke(mw, "OpenTransitionDialog", false, null);

        var effect = manager.ObjectMap.Values.FirstOrDefault(
            o => !before.Contains(o.Id) && o.ClassName == "hkbBlendingTransitionEffect");
        Check("a new effect was created for the edit", effect != null);
        Check("the edited transition points at it", editParam.Value == effect?.Id,
            $"{oldRef} -> {editParam.Value}");
        // Value reads the Children join whenever resolved refs are cached there, so a
        // stale cache would silently out-vote the new text.
        Check("the resolved-ref cache agrees with the text",
            editParam.Children.Count == 0 || editParam.Children[0].Id == effect?.Id);

        Undo(mw);
        Check("undo restores the previous effect ref", editParam.Value == oldRef, editParam.Value);
        Check("undo removes the created effect",
            effect != null && !manager.ObjectMap.ContainsKey(effect.Id));
    }

    // -- Helpers -------------------------------------------------------------
    private static (HkObject Owner, HkParam Transition) FindTransitionPointingAt(
        HavokManager manager, string effectId)
    {
        if (effectId == null) return (null, null);
        foreach (var arr in manager.ObjectMap.Values
                     .Where(o => o.ClassName == "hkbStateMachineTransitionInfoArray"))
            foreach (var tr in arr.Params.First(p => p.Name == "transitions").Children)
            {
                var tp = tr.Params.FirstOrDefault(p => p.Name == "transition");
                if (tp?.Value == effectId) return (arr, tp);
            }
        return (null, null);
    }

    private static string Param(HkObject o, string name) =>
        o.Params.FirstOrDefault(p => p.Name == name)?.Value;

    private static void Undo(SageHavokEditor.MainWindow mw)
    {
        var undo = Member(mw, "_undoRedo");
        undo.GetType().GetMethod("Undo")!.Invoke(undo, null);
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

    private static Button FindConfirm(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Button b && (b.Content as string) == "Confirm") return b;
            var found = FindConfirm(c);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>--shot writes a PNG of the filled-in dialog, for eyeballing the layout.</summary>
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
