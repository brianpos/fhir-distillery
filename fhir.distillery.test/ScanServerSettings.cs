using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fhir.distillery.test
{
    public class ScanServerSettings
    {
        public string baseurl { get; set; }
        public string[] queries { get; set; }
    }
}
