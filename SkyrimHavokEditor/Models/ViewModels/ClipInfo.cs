using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{

    public sealed class PreviewTrigger
    {
        public float Time;       // seconds (parsed from localTime)
        public string EventName;
        public bool RelativeToEnd;
    }
    public class ClipInfo : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Mode { get; set; }
        public string PlaybackSpeed { get; set; }

        private string _animationPath;
        public string AnimationPath
        {
            get => _animationPath;
            set
            {
                if (_animationPath != value)
                {
                    var old = _animationPath;
                    _animationPath = value;
                    OnPropertyChanged();
                    PathChanged?.Invoke(this, (old, value));
                }
            }
        }

        public event EventHandler<(string OldValue, string NewValue)> PathChanged;
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ClipTrigger
    {
        public string ClipName { get; set; }
        public string LocalTime { get; set; }
        public string EventId { get; set; }
        public string EventName { get; set; }
        public bool RelativeToEnd { get; set; }
        public bool Acyclic { get; set; }
    }
}
