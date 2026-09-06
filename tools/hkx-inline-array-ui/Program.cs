using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SageHavokEditor.Core;
using SageHavokEditor.Models;

// Drives the property editor's "＋ Add element" through the real MainWindow.
//
//   dotnet run --project tools/hkx-inline-array-ui -- <behavior.hkx|xml> [--shot <dir>]
//
// tools/hkx-array-kinds proves the metadata — which array params hold inline
// structs and which hold #id refs. What it can't prove is that the answer
// reaches the screen: the button's visibility is decided by a converter inside a
// ListBox item template, so the only honest check is to render the row and look
// at the button. Then press it, on the case that motivated the whole thing — an
// array with no elements, so nothing to clone and no shape to infer from — and
// undo it again.
//
// A "splines=curved not supported in dot" line from the graph renderer is noise.

namespace SageHavokEditor.Tools.InlineArrayUi;

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
            Console.Error.WriteLine("usage: hkx-inline-array-ui <behavior.hkx|xml> [--shot <dir>]");
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
            var manager = (HavokManager)Member(mw, "manager");
            Console.WriteLine($"loaded {Path.GetFileName(path)} — {manager.ObjectMap.Count} objects");

            EmptyArrayPath(mw, manager);
            RefArrayPath(mw, manager);
            PopulatedArrayPath(mw, manager);
        }
        catch (Exception ex)
        {
            Check("ran without throwing", false, ex.ToString());
        }

        Console.WriteLine(_failed == 0 ? "\nall checks passed" : $"\n{_failed} check(s) FAILED");
        Environment.Exit(_failed == 0 ? 0 : 1);
    }

    // -- The case the item is about -------------------------------------------
    // An inline-struct array with zero elements. Before the declared array kind
    // was available there was nothing to key on here: no inline child to notice,
    // and numelements="0" reads the same on a ref array.
    private static void EmptyArrayPath(SageHavokEditor.MainWindow mw, HavokManager manager)
    {
        Console.WriteLine("an array that is empty in the file:");

        var (obj, param) = FindParam(manager,
            p => p.TypeInfo?.ArrayKind == HkArrayKind.InlineStruct
                 && p.Children.Count == 0
                 && p.TypeInfo.ElementClassName != null);
        if (obj == null)
        {
            Check("the file has one", false, "no empty inline-struct array in this file");
            return;
        }
        Console.WriteLine($"  {obj.ClassName}.{param.Name} "
                          + $"(elements are {param.TypeInfo.ElementClassName})");

        var button = AddButtonFor(mw, obj, param);
        Shoot(mw, "empty-array-row");
        Check("the row offers ＋ Add element", button is { Visibility: Visibility.Visible },
            button == null ? "no button in the rendered row" : button.Visibility.ToString());
        if (button == null) return;

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        Check("pressing it adds one element", param.Children.Count == 1,
            $"{param.Children.Count} children");
        Check("numelements moves with it", param.NumElements == "1", param.NumElements);
        var added = param.Children.FirstOrDefault();
        Check("the element is inline, not a reference",
            added != null && string.IsNullOrEmpty(added.Id));
        Check("and it is built from the declared element class, not left blank",
            added is { Params.Count: > 0 },
            added == null ? "none" : $"{added.Params.Count} params: "
                                     + string.Join(", ", added.Params.Take(4).Select(p => p.Name)));

        // The whole edit is one action, so one undo has to put the file back:
        // an array left at numelements="1" with no element is exactly the desync
        // that truncates data on the way to .hkx.
        Invoke(mw, "BtnUndo_Click", null, new RoutedEventArgs());
        Check("undo puts the array back to empty",
            param.Children.Count == 0 && param.NumElements == "0",
            $"{param.Children.Count} children, numelements={param.NumElements}");

        // Sticky by design: having added and removed the last element, the user
        // must still be able to add another.
        var again = AddButtonFor(mw, obj, param);
        Check("and the button is still there afterwards",
            again is { Visibility: Visibility.Visible });
    }

    // -- The other half of the same fact --------------------------------------
    private static void RefArrayPath(SageHavokEditor.MainWindow mw, HavokManager manager)
    {
        Console.WriteLine("a reference array:");

        var (obj, param) = FindParam(manager, p => p.TypeInfo?.ArrayKind == HkArrayKind.Pointer);
        if (obj == null)
        {
            Check("the file has one", false, "no ref array in this file");
            return;
        }
        Console.WriteLine($"  {obj.ClassName}.{param.Name} (numelements={param.NumElements})");

        var button = AddButtonFor(mw, obj, param);
        Check("offers no ＋ Add element — ref arrays edit as text",
            button == null || button.Visibility != Visibility.Visible,
            button?.Visibility.ToString());
    }

    // -- What already worked, still working -----------------------------------
    private static void PopulatedArrayPath(SageHavokEditor.MainWindow mw, HavokManager manager)
    {
        Console.WriteLine("an inline-struct array that already has elements:");

        var (obj, param) = FindParam(manager,
            p => p.TypeInfo?.ArrayKind == HkArrayKind.InlineStruct && p.Children.Count > 0);
        if (obj == null)
        {
            Check("the file has one", false, "none in this file");
            return;
        }
        Console.WriteLine($"  {obj.ClassName}.{param.Name} ({param.Children.Count} elements)");

        var button = AddButtonFor(mw, obj, param);
        Check("still offers ＋ Add element", button is { Visibility: Visibility.Visible },
            button?.Visibility.ToString());
    }

    // -- Helpers ---------------------------------------------------------------

    private static (HkObject, HkParam) FindParam(HavokManager manager, Func<HkParam, bool> want)
    {
        foreach (var obj in manager.ObjectMap.Values)
            foreach (var p in obj.Params)
                if (want(p))
                    return (obj, p);
        return (null, null);
    }

    /// <summary>
    /// Opens the object in the property editor and digs the ＋ button out of the
    /// rendered row. Going through the real template is the point — the
    /// visibility rule lives in a converter the XAML binds, so reading the flag
    /// off the model would prove nothing about what the user sees.
    /// </summary>
    private static Button AddButtonFor(SageHavokEditor.MainWindow mw, HkObject obj, HkParam param)
    {
        Invoke(mw, "LoadObjectIntoEditor", obj);
        var editor = (ListBox)Member(mw, "ParamsEditor");
        editor.UpdateLayout();

        // Also what puts the row on screen for --shot, and what realises it at all
        // when the editor is virtualising a long param list.
        editor.ScrollIntoView(param);
        editor.UpdateLayout();

        var container = editor.ItemContainerGenerator.ContainerFromItem(param) as ListBoxItem;
        if (container == null) return null;
        container.UpdateLayout();

        return Descendants(container).OfType<Button>()
            .FirstOrDefault(b => b.Content as string == "＋ Add element");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
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

    /// <summary>--shot writes a PNG of the window, for eyeballing the row.</summary>
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
