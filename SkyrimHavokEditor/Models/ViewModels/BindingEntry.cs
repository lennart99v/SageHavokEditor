using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class BindingEntry
    {
        public string OwnerName { get; set; }
        public string OwnerClass { get; set; }
        public string OwnerId { get; set; }
        public string MemberPath { get; set; }
        public string VariableIndex { get; set; }
        public string VariableName { get; set; }
        public string BindingType { get; set; }
    }
}
