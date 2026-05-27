using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class PathChange
    {
        public string ClipId { get; set; }
        public string ClipName { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
    }
}
