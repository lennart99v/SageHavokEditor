using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class VariableUsage
    {
        public string VariableName { get; set; } = "";
        public string UsedBy { get; set; } = "";       // object name
        public string UsedById { get; set; } = "";     // object ID
        public string ClassName { get; set; } = "";
        public string Property { get; set; } = "";     // which param is bound
        public string BindingType { get; set; } = "";  // "VariableBinding" or "Direct"
    }
}
