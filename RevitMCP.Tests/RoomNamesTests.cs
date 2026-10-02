using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using Xunit;

namespace RevitMCP.Tests;

public class RoomNamesTests
{
    [Theory]
    [InlineData("Väikeklass 08", "08", "Väikeklass")]          // #58: Revit's display name
    [InlineData("Klass 1.12", "1.12", "Klass")]
    [InlineData("Väikeklass", "08", "Väikeklass")]             // already a plain name
    [InlineData("Ruum 108", "08", "Ruum 108")]                  // number is not a separate trailing token
    [InlineData("08", "08", "08")]                             // name equal to the number is kept
    [InlineData("  WC 3  ", "3", "WC")]
    [InlineData("Ladu", "", "Ladu")]
    [InlineData(null, "08", "")]
    public void FromDisplayName_StripsTheTrailingNumber(string? display, string? number, string expected) =>
        Assert.Equal(expected, RoomNames.FromDisplayName(display, number));
}
