using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class SearchResult
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public string MatchedOn { get; set; } // which field matched
        public string Preview { get; set; } // snippet of matching content
    }
}
