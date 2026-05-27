using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class TransitionInfo
    {
        public string FromState { get; set; } = "";
        public string ToState { get; set; } = "";
        public string EventName { get; set; } = "";
        public string EventId { get; set; } = "";
        public string BlendDuration { get; set; } = "";
        public string Flags { get; set; } = "";
        public string TransitionEffect { get; set; } = "";
    }
}
