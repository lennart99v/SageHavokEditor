using System.Collections.ObjectModel;
using SageHavokEditor.Core;

namespace SageHavokEditor.Models.ViewModels
{
    public class ProjectViewModel
    {
        public HkLoadedFile? File { get; set; }
        public string WorldUpWS { get; set; } = "";
        public string DefaultEventMode { get; set; } = "";
        public ObservableCollection<CharacterViewModel> Characters { get; set; } = new();
        public HkObject? StringDataObj { get; set; }
        public HkObject? ProjectDataObj { get; set; }
    }
}
