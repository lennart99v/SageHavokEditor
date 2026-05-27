using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Core.Validation
{
    public class ValidationIssue
    {
        public string Severity { get; set; } = "";
        public string ObjectId { get; set; } = "";
        public string ObjectClass { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsError => Severity == "Error";
        public bool IsWarning => Severity == "Warning";
    }
}
