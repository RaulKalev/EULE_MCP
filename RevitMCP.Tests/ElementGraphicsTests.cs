using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ElementGraphics;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Services;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Per-element view graphics (#160): argument parsing, the per-element judgement and the tool surface.</summary>
public class ElementGraphicsTests
{
    private static Dictionary<string, object?> Args(string json) =>
        JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);

    private static ElementGraphicsFacts Visible() => new() { InViewCollector = true };

    // ── Parsing ──

    [Fact]
    public void ReadNeedsAViewAndElements()
    {
        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"elementIds\":[1]}"), false, out var e1));
        Assert.Contains("view", e1!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5}"), false, out var e2));
        Assert.Contains("elements", e2!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5,\"useActiveView\":true,\"elementIds\":[1]}"), false, out _));
        var ok = ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5,\"elementIds\":[1,2,2]}"), false, out _);
        Assert.Equal(new long[] { 1, 2 }, ok!.ElementIds);
    }

    [Fact]
    public void SelectionAndIdsAreExclusive()
    {
        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"useActiveView\":true,\"useSelection\":true,\"elementIds\":[1],\"operation\":\"hide\"}"), true, out _));
        var ok = ElementGraphicsRequestParser.Parse(Args("{\"useActiveView\":true,\"useSelection\":true,\"operation\":\"hide\"}"), true, out _);
        Assert.True(ok!.UseSelection);
        Assert.True(ok.UseActiveView);
    }

    [Theory]
    [InlineData("hide", "hide")]
    [InlineData("Unhide", "unhide")]
    [InlineData("reset_overrides", "resetOverrides")]
    [InlineData("clear", "resetOverrides")]
    [InlineData("override", "override")]
    [InlineData("explode", "")]
    public void OperationsResolve(string text, string expected) => Assert.Equal(expected, ElementGraphicsRequestParser.ResolveOperation(text));

    [Fact]
    public void OverrideNeedsValidGraphics()
    {
        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5,\"elementIds\":[1],\"operation\":\"override\"}"), true, out var missing));
        Assert.Contains("graphics", missing!);

        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5,\"elementIds\":[1],\"operation\":\"override\",\"graphics\":{\"projectionLines\":{\"color\":\"red\"}}}"), true, out var bad));
        Assert.Contains("color", bad!);

        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5,\"elementIds\":[1],\"operation\":\"override\",\"graphics\":{\"transparency\":150}}"), true, out _));

        var ok = ElementGraphicsRequestParser.Parse(Args(
            "{\"viewId\":5,\"elementIds\":[1],\"operation\":\"override\",\"graphics\":{\"halftone\":true,\"transparency\":40,\"projectionLines\":{\"color\":\"#FF0000\",\"weight\":5,\"pattern\":\"Dash\"},\"surfaceForeground\":{\"color\":[0,128,255],\"pattern\":\"solid\"}}}"), true, out var error);
        Assert.Null(error);
        Assert.True(ok!.Graphics!.Halftone);
        Assert.Equal(40, ok.Graphics.Transparency);
        Assert.Equal("#FF0000", ok.Graphics.ProjectionLines!.Color!.ToHex());
        Assert.Equal(5, ok.Graphics.ProjectionLines.Weight);
        Assert.Equal("#0080FF", ok.Graphics.SurfaceForeground!.Color!.ToHex());
    }

    [Fact]
    public void GraphicsOnlyBelongToOverride()
    {
        Assert.Null(ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5,\"elementIds\":[1],\"operation\":\"hide\",\"graphics\":{\"halftone\":true}}"), true, out var error));
        Assert.Contains("override", error!);
        Assert.NotNull(ElementGraphicsRequestParser.Parse(Args("{\"viewId\":5,\"elementIds\":[1],\"operation\":\"resetOverrides\"}"), true, out _));
    }

    [Fact]
    public void TooManyElementsAreRefused()
    {
        var ids = string.Join(",", Enumerable.Range(1, ElementGraphicsRequest.MaxElements + 1));
        Assert.Null(ElementGraphicsRequestParser.Parse(Args($"{{\"viewId\":5,\"elementIds\":[{ids}],\"operation\":\"hide\"}}"), true, out var error));
        Assert.Contains("limit", error!);
    }

    // ── Judgement ──

    [Fact]
    public void HideIsEligibleForAVisibleElement()
    {
        var d = ElementGraphicsPlanner.Decide("hide", Visible());
        Assert.Equal(ElementGraphicsOutcome.WillChange, d.Outcome);
    }

    [Fact]
    public void HideSkipsAlreadyHiddenAndRefusesWhatCannotBeHiddenOrIsNotInTheView()
    {
        var hidden = new ElementGraphicsFacts { HiddenPermanently = true };
        Assert.Equal(ElementGraphicsCodes.AlreadyHidden, ElementGraphicsPlanner.Decide("hide", hidden).Code);
        Assert.Equal(ElementGraphicsOutcome.Unchanged, ElementGraphicsPlanner.Decide("hide", hidden).Outcome);

        var fixedElement = new ElementGraphicsFacts { InViewCollector = true, CanBeHidden = false };
        Assert.Equal(ElementGraphicsCodes.CannotBeHidden, ElementGraphicsPlanner.Decide("hide", fixedElement).Code);

        var elsewhere = new ElementGraphicsFacts { InViewCollector = false };
        var d = ElementGraphicsPlanner.Decide("hide", elsewhere);
        Assert.Equal(ElementGraphicsOutcome.Ineligible, d.Outcome);
        Assert.Equal(ElementGraphicsCodes.NotInView, d.Code);

        Assert.Equal(ElementGraphicsCodes.NotFound, ElementGraphicsPlanner.Decide("hide", new ElementGraphicsFacts { Exists = false }).Code);
    }

    [Fact]
    public void UnhideOnlyTouchesPermanentlyHiddenElementsAndNamesWhatStillSuppresses()
    {
        Assert.Equal(ElementGraphicsCodes.NotHidden, ElementGraphicsPlanner.Decide("unhide", Visible()).Code);

        var facts = new ElementGraphicsFacts { HiddenPermanently = true, CategoryHidden = true, WorksetHidden = true };
        facts.HidingFilters.Add("Old walls");
        var d = ElementGraphicsPlanner.Decide("unhide", facts);
        Assert.Equal(ElementGraphicsOutcome.WillChange, d.Outcome);
        var codes = d.StillSuppressed.Select(r => r.Code).ToList();
        Assert.Contains(ElementGraphicsCodes.CategoryHidden, codes);
        Assert.Contains(ElementGraphicsCodes.FilterHidden, codes);
        Assert.Contains(ElementGraphicsCodes.WorksetHidden, codes);
        Assert.DoesNotContain(ElementGraphicsCodes.HiddenInView, codes);
    }

    [Fact]
    public void TemporaryHideIsolateIsAReasonOnlyWhenTheElementIsNotShownByIt()
    {
        var shown = new ElementGraphicsFacts { InViewCollector = true, HiddenByTemporaryMode = false };
        Assert.Empty(ElementGraphicsPlanner.AllReasons(shown));
        var notShown = new ElementGraphicsFacts { InViewCollector = true, HiddenByTemporaryMode = true };
        Assert.Equal(ElementGraphicsCodes.TemporaryHideIsolate, Assert.Single(ElementGraphicsPlanner.AllReasons(notShown)).Code);
    }

    [Fact]
    public void OverrideOnAHiddenElementIsAllowedButFlaggedInvisible()
    {
        var facts = new ElementGraphicsFacts { HiddenPermanently = true };
        var d = ElementGraphicsPlanner.Decide("override", facts);
        Assert.Equal(ElementGraphicsOutcome.WillChange, d.Outcome);
        Assert.Contains(d.StillSuppressed, r => r.Code == ElementGraphicsCodes.HiddenInView);
    }

    [Fact]
    public void OverrideSkipsAnElementThatAlreadyHasTheSettingsAndRefusesOneOutsideTheView()
    {
        Assert.Equal(ElementGraphicsCodes.SameOverrides, ElementGraphicsPlanner.Decide("override", Visible(), overridesDiffer: false).Code);
        Assert.Equal(ElementGraphicsCodes.NotInView, ElementGraphicsPlanner.Decide("override", new ElementGraphicsFacts()).Code);
    }

    [Fact]
    public void ResetOnlyChangesElementsThatHaveOverrides()
    {
        Assert.Equal(ElementGraphicsCodes.NoOverrides, ElementGraphicsPlanner.Decide("resetOverrides", Visible()).Code);
        var withOverrides = new ElementGraphicsFacts { InViewCollector = true, HasOverrides = true };
        Assert.Equal(ElementGraphicsOutcome.WillChange, ElementGraphicsPlanner.Decide("resetOverrides", withOverrides).Outcome);
        // A reset works even when the element is currently hidden or elsewhere.
        var hiddenWithOverrides = new ElementGraphicsFacts { HiddenPermanently = true, HasOverrides = true };
        Assert.Equal(ElementGraphicsOutcome.WillChange, ElementGraphicsPlanner.Decide("resetOverrides", hiddenWithOverrides).Outcome);
    }

    [Fact]
    public void AnElementOutsideTheViewWithNoOtherReasonIsStillReported()
    {
        var reasons = ElementGraphicsPlanner.AllReasons(new ElementGraphicsFacts { InViewCollector = false });
        Assert.Equal(ElementGraphicsCodes.NotInView, Assert.Single(reasons).Code);
    }

    // ── Surface ──

    [Theory]
    [InlineData("revit_get_element_graphics", "ReadOnly")]
    [InlineData("revit_preview_set_element_graphics", "ReadOnly")]
    [InlineData("revit_set_element_graphics", "RequiresApproval")]
    public void ToolsAreInTheViewsGroupWithTheRightPermissions(string tool, string permission)
    {
        Assert.Equal("views", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void TheWriteModifiesForPlansAndItsIdsAndViewAreInScope()
    {
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_set_element_graphics"));
        Assert.NotEqual(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_get_element_graphics"));
        var args = Args("{\"viewId\":9,\"elementIds\":[6,7],\"operation\":\"hide\"}");
        var scope = PlanArgumentScope.Extract(args);
        Assert.Equal(new long[] { 6, 7 }, scope.ElementIds.OrderBy(i => i));
        Assert.Contains(9L, scope.ViewIds);
    }
}
