using System.Collections.ObjectModel;
using SageHavokEditor.Core;

namespace SageHavokEditor.Models.ViewModels
{
    public class CharacterViewModel
    {
        public HkLoadedFile? File { get; set; }
        public string Name { get; set; } = "";
        public string SkeletonPath { get; set; } = "";
        public string RagdollPath { get; set; } = "";
        public string BehaviorPath { get; set; } = "";
        public float CapsuleHeight { get; set; } = 1.7f;
        public float CapsuleRadius { get; set; } = 0.4f;
        public ObservableCollection<string> AnimationNames { get; set; } = new();
        public HkLoadedFile? BehaviorFile { get; set; }
        public HkObject? CharacterDataObj { get; set; }
        public HkObject? CharacterStringDataObj { get; set; }
        public bool IsLoaded => File?.Manager != null;
        public string StatusColor => IsLoaded ? "#4CAF50" : "#FF9800";
    }
}
