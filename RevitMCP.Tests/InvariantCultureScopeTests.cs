using System.Globalization;
using RevitMCP.Addin.Services;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>X1: messages said "45,21 m" and "Point (16,91, 78,62, 3,94)" on an Estonian machine.</summary>
public class InvariantCultureScopeTests
{
    [Fact]
    public void FormatsInvariantInside_AndRestoresAfter()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("et-EE");
        try
        {
            Assert.Equal("45,21", $"{45.21:F2}");
            using (new InvariantCultureScope())
                Assert.Equal("45.21 m", $"{45.21:F2} m");
            Assert.Equal("et-EE", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task AToolsAwaitsKeepTheInvariantCulture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("et-EE");
        try
        {
            static async Task<string> Tool()
            {
                await Task.Yield();
                return $"{0.3:F1} of {1.5:F1} m²";
            }

            Task<string> execution;
            using (new InvariantCultureScope())
                execution = Tool();
            Assert.Equal("et-EE", CultureInfo.CurrentCulture.Name);   // the caller has its culture back at once
            Assert.Equal("0.3 of 1.5 m²", await execution);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
