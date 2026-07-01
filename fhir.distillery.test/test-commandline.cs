using fhir_distillery;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace fhir.distillery.test;

[TestClass]
public class test_commandline
{
    [TestMethod]
    public async Task TestCommandLineUsage()
    {
        var result = await Program.Main(new string[] { "-h" });
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task TestCommandLineUsageNoArgs()
    {
        var result = await Program.Main(new string[] { });
        Assert.AreEqual(1, result);
    }
}
