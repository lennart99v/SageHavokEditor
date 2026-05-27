using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using SkyrimHavokEditor.Core.Services;

namespace SkyrimHavokEditor.Core.Services
{
    public class BookmarkService
    {
        private static readonly string BookmarksPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkyrimHavokEditor", "bookmarks.txt");

        public ObservableCollection<BookmarkEntry> Bookmarks { get; } = new();

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BookmarksPath));
                var lines = Bookmarks.Select(b => $"{b.Id}|{b.Name}|{b.ClassName}|{b.Label ?? ""}");
                File.WriteAllLines(BookmarksPath, lines);
            }
            catch { }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(BookmarksPath)) return;
                foreach (var line in File.ReadAllLines(BookmarksPath))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 3) continue;
                    Bookmarks.Add(new BookmarkEntry
                    {
                        Id = parts[0],
                        Name = parts[1],
                        ClassName = parts[2],
                        Label = parts.Length > 3 ? parts[3] : ""
                    });
                }
            }
            catch { }
        }

        public bool IsBookmarked(string id) => Bookmarks.Any(b => b.Id == id);

        public void Add(string id, string name, string className, string label = "")
        {
            if (IsBookmarked(id)) return;
            Bookmarks.Add(new BookmarkEntry { Id = id, Name = name, ClassName = className, Label = label });
            Save();
        }

        public void Remove(string id)
        {
            var existing = Bookmarks.FirstOrDefault(b => b.Id == id);
            if (existing != null)
            {
                Bookmarks.Remove(existing);
                Save();
            }
        }
    }

    public class BookmarkEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public string Label { get; set; }
        public string Display => string.IsNullOrEmpty(Label) ? Name : $"{Label} ({Name})";
    }
}
