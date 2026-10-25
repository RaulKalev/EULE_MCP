using RevitMCP.Addin.Tagging;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>directionKeyword found no variant among "Paneel, Paremal" / "Paneel, Vasakul" (2026-10-25 Revit check).</summary>
public class TagDirectionNamesTests
{
    [Theory]
    [InlineData("Paneel, Vasakul", "Paneel", "Left", true)]
    [InlineData("Paneel, Paremal", "Paneel", "Right", true)]
    [InlineData("Paneel, Paremal", "Paneel", "Left", false)]
    [InlineData("Comments - Vasakul", "Paneel", "Left", false)]      // the keyword is required
    [InlineData("Kaamera tüüp + Aadress, Vasakul", "", "Left", true)]
    [InlineData("Tag Left 2.5", "tag", "Left", true)]
    [InlineData("Up arrow", null, "Up", true)]
    [InlineData("Paneel all", "Paneel", "Down", true)]
    [InlineData("Ballast", null, "Down", false)]                     // "all" only as a whole word
    [InlineData("Paigaldus + Kõrgus, Paremal", "Kõrgus", "Right", true)]
    public void Matches(string name, string? keyword, string direction, bool expected) =>
        Assert.Equal(expected, TagDirectionNames.Matches(name, keyword, direction));
}
