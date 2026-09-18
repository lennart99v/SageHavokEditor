using System;
using System.IO;
using System.Threading;
using SageHavokEditor.UI.Dialogs;

// hkx-guide-export — write the in-app Guide out as Markdown.
//
// The Guide has never had a source document. It is ~40 AddSection calls inside
// DocumentationView, so there was nothing to hand someone who asked for "the
// readme the Guide is made from" — the repo's README.md is a different and much
// shorter thing, and selecting the Guide's text in the app copies it without any
// of the formatting.
//
// This builds the real DocumentationView and renders the items it produced, so
// the published file cannot say something the app does not. It needs an STA
// thread because the control is a WPF UserControl, but it never shows a window.
//
//   dotnet run --project tools/hkx-guide-export -- docs/GUIDE.md

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "docs/GUIDE.md";
        int exit = 1;

        // The control must be constructed on an STA thread with a Dispatcher.
        var t = new Thread(() =>
        {
            try
            {
                var app = new System.Windows.Application();
                var view = new DocumentationView();
                var items = view.GetItems();

                int sections = 0, groups = 0;
                foreach (var i in items) { if (i.IsGroup) groups++; else sections++; }

                if (sections == 0)
                    throw new InvalidOperationException(
                        "the Guide built zero sections — the export would be an empty file");

                var md = GuideMarkdown.Render(items, VersionOf());

                var full = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, md, new System.Text.UTF8Encoding(false));

                Console.WriteLine($"{full}");
                Console.WriteLine($"  {sections} sections in {groups} groups, "
                                + $"{md.Length:N0} characters");
                exit = 0;
                app.Shutdown();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("export failed: " + ex.Message);
                exit = 1;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return exit;
    }

    private static string? VersionOf()
    {
        try
        {
            return typeof(DocumentationView).Assembly.GetName().Version?.ToString(3);
        }
        catch { return null; }
    }
}
