using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#175: the argument parsing, curve and visibility logic of the nested-component tools, and their tool surface.</summary>
public class ModelFamilyComponentTests
{
    private static ModelFamilyComponentPlan Parse(string json, out List<string> problems)
    {
        problems = new List<string>();
        var args = new Dictionary<string, object?>();
        foreach (var property in JObject.Parse(json).Properties()) args[property.Name] = property.Value;
        return ModelFamilyComponentPlan.Parse(args, problems);
    }

    private static JObject Obj(string json) => JObject.Parse(json);

    // ── operation order and describe ──────────────────────────────────────────

    [Fact]
    public void ListsRunInTheDocumentedOrder() =>
        Assert.Equal(new[] { "remove", "subcategories", "nestedFamilies", "instances", "symbolicLines", "modelLines", "setSubcategory", "visibility" },
            ModelFamilyComponentPlan.Order);

    [Fact]
    public void AllEightListsParse()
    {
        var plan = Parse("""
        {
          "remove": [10, 11],
          "subcategories": [{"name":"Plan symbol","lineWeightProjection":3,"lineWeightCut":5,"color":[255,0,0],"linePattern":"Solid","material":"Steel"}],
          "nestedFamilies": [{"path":"C:\\fam\\Symbol.rfa","ifExists":"overwrite"}, {"fromProject":"EN_Symbol"}],
          "instances": [{"family":"Symbol","position":[0,0,0],"view":"plan","name":"s"}],
          "symbolicLines": [{"view":"plan","curves":[{"line":{"start":[0,0],"end":[100,0]}}],"subcategory":"Plan symbol"}],
          "modelLines": [{"workPlane":"level","curves":[{"circle":{"center":[0,0,0],"radius":50}}]}],
          "setSubcategory": [{"elements":[{"created":"symbolicLines[0]"}],"subcategory":null}],
          "visibility": [{"elements":[{"created":"instances[0]"}],"visible":false}]
        }
        """, out var problems);
        Assert.Empty(problems);
        Assert.Equal(new long[] { 10, 11 }, plan.Remove);
        Assert.Equal(3, plan.Subcategories[0].LineWeightProjection);
        Assert.Equal(new[] { 255, 0, 0 }, plan.Subcategories[0].Color);
        Assert.Equal(FamilyIfExists.Overwrite, plan.NestedFamilies[0].IfExists);
        Assert.Equal(FamilyIfExists.Skip, plan.NestedFamilies[1].IfExists);
        Assert.Equal("EN_Symbol", plan.NestedFamilies[1].FromProject);
        Assert.Single(plan.Instances);
        Assert.Single(plan.SymbolicLines[0].Curves);
        Assert.Equal("circle", plan.ModelLines[0].Curves[0].Kind);
        Assert.Null(plan.SetSubcategory[0].Subcategory);
        Assert.Equal(10, plan.OperationCount);
        Assert.Contains("2 nested families", plan.Describe());
    }

    [Fact]
    public void EmptyRequestHasNoOperations()
    {
        var plan = Parse("{}", out var problems);
        Assert.Empty(problems);
        Assert.Equal(0, plan.OperationCount);
        Assert.Equal("nothing to do", plan.Describe());
    }

    // ── subcategories ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"subcategories":[{"name":"A","lineWeightProjection":0}]}""", "lineWeightProjection")]
    [InlineData("""{"subcategories":[{"name":"A","lineWeightCut":17}]}""", "lineWeightCut")]
    [InlineData("""{"subcategories":[{"name":"A","lineWeightProjection":2.5}]}""", "lineWeightProjection")]
    [InlineData("""{"subcategories":[{"name":"A","color":[256,0,0]}]}""", "color")]
    [InlineData("""{"subcategories":[{"name":"A","color":[1,2]}]}""", "color")]
    [InlineData("""{"subcategories":[{"lineWeightProjection":2}]}""", "name")]
    [InlineData("""{"subcategories":[{"name":"A","colour":[1,2,3]}]}""", "unknown key")]
    public void BadSubcategoriesAreProblems(string json, string expected)
    {
        Parse(json, out var problems);
        Assert.Contains(problems, p => p.Contains("subcategories[0]") && p.Contains(expected));
    }

    // ── nested families ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"nestedFamilies":[{}]}""", "exactly one of path")]
    [InlineData("""{"nestedFamilies":[{"path":"C:\\a.rfa","fromProject":"A"}]}""", "exactly one of path")]
    [InlineData("""{"nestedFamilies":[{"path":"C:\\a.txt"}]}""", ".rfa")]
    [InlineData("""{"nestedFamilies":[{"path":"C:\\a.rfa","ifExists":"replace"}]}""", "ifExists")]
    public void BadNestedFamiliesAreProblems(string json, string expected)
    {
        Parse(json, out var problems);
        Assert.Contains(problems, p => p.Contains("nestedFamilies[0]") && p.Contains(expected));
    }

    [Theory]
    [InlineData("", FamilyIfExists.Skip)]
    [InlineData("skip", FamilyIfExists.Skip)]
    [InlineData("overwrite", FamilyIfExists.Overwrite)]
    [InlineData("overwriteParameters", FamilyIfExists.OverwriteParameters)]
    public void IfExistsParses(string text, FamilyIfExists expected)
    {
        var plan = Parse($$"""{"nestedFamilies":[{"fromProject":"A","ifExists":"{{text}}"}]}""", out var problems);
        Assert.Empty(problems);
        Assert.Equal(expected, plan.NestedFamilies[0].IfExists);
    }

    // ── instances ─────────────────────────────────────────────────────────────

    [Fact]
    public void InstanceReadsEverythingItIsGiven()
    {
        var plan = Parse("""
        {"instances":[{
          "family":"Symbol","type":"A","position":[10,20,30],"rotation":90,"view":"plan",
          "parameters":{"Label":"x"},"associate":{"Label":"Family Label","IS_VISIBLE_PARAM":"Show"},
          "typeParameter":"Nested Type","typeParameterInstance":true,
          "visibleParameter":"Show Symbol","visibility":{"views":{"planRcp":false}},
          "lockTo":[{"reference":"Center (Left/Right)","instanceReference":"center_left_right"}]
        }]}
        """, out var problems);
        Assert.Empty(problems);
        var instance = plan.Instances[0];
        Assert.Equal(new double[] { 10, 20, 30 }, instance.Position);
        Assert.Equal(90, instance.RotationDegrees);
        Assert.Equal(2, instance.Associate.Count);
        Assert.True(instance.TypeParameterInstance);
        Assert.Equal("Show Symbol", instance.Visibility!.VisibleParameter);
        Assert.Equal(false, instance.Visibility.PlanRcp);
        Assert.Equal("CenterLeftRight", instance.LockTo[0].InstanceReference);
    }

    [Theory]
    [InlineData("""{"instances":[{"position":[0,0,0]}]}""", "family is required")]
    [InlineData("""{"instances":[{"family":"A","view":"plan","workPlane":"level"}]}""", "not both")]
    [InlineData("""{"instances":[{"family":"A","position":[0]}]}""", "position")]
    [InlineData("""{"instances":[{"family":"A","associate":{"X":""}}]}""", "associate.X")]
    [InlineData("""{"instances":[{"family":"A","lockTo":[{"reference":"P","instanceReference":"middle"}]}]}""", "instanceReference")]
    [InlineData("""{"instances":[{"family":"A","lockTo":[{"instanceReference":"left"}]}]}""", "reference is required")]
    [InlineData("""{"instances":[{"family":"A","rotate":5}]}""", "unknown key")]
    public void BadInstancesAreProblems(string json, string expected)
    {
        Parse(json, out var problems);
        Assert.Contains(problems, p => p.Contains("instances[0]") && p.Contains(expected));
    }

    [Theory]
    [InlineData("centerLeftRight", "CenterLeftRight")]
    [InlineData("center_front_back", "CenterFrontBack")]
    [InlineData("LEFT", "Left")]
    [InlineData("centerElevation", "CenterElevation")]
    [InlineData("nope", null)]
    public void InstanceReferenceNamesNormalize(string text, string? expected) =>
        Assert.Equal(expected, ModelFamilyComponentPlan.NormalizeInstanceReference(text));

    // ── curves ────────────────────────────────────────────────────────────────

    [Fact]
    public void LineArcAndCircleParse()
    {
        var problems = new List<string>();
        var line = ComponentCurveSpec.Parse(Obj("""{"line":{"start":[0,0],"end":[100,0,5]}}"""), "c", problems);
        var arc = ComponentCurveSpec.Parse(Obj("""{"arc":{"start":[0,0],"mid":[50,50],"end":[100,0]}}"""), "c", problems);
        var circle = ComponentCurveSpec.Parse(Obj("""{"circle":{"center":[0,0],"radius":25}}"""), "c", problems);
        Assert.Empty(problems);
        Assert.Equal(new double[] { 100, 0, 5 }, line!.Points[1]);
        Assert.Equal(3, arc!.Points.Count);
        Assert.Equal(25, circle!.Radius);
    }

    [Theory]
    [InlineData("""{"line":{"start":[0,0],"end":[0,0]}}""", "shorter")]
    [InlineData("""{"line":{"start":[0,0],"end":[0.05,0]}}""", "shorter")]
    [InlineData("""{"arc":{"start":[0,0],"mid":[50,0],"end":[100,0]}}""", "one line")]
    [InlineData("""{"arc":{"start":[0,0],"mid":[0,0],"end":[100,0]}}""", "three different points")]
    [InlineData("""{"circle":{"center":[0,0],"radius":0}}""", "radius")]
    [InlineData("""{"circle":{"center":[0,0]}}""", "radius")]
    [InlineData("""{"line":{"start":[0,0]}}""", "end")]
    [InlineData("""{"line":{"start":[0,0],"end":[1,1]},"arc":{}}""", "only one")]
    [InlineData("""{"polyline":{}}""", "unknown key")]
    [InlineData("""{}""", "one of line, arc, circle")]
    public void DegenerateCurvesAreProblems(string json, string expected)
    {
        var problems = new List<string>();
        var curve = ComponentCurveSpec.Parse(Obj(json), "curves[0]", problems);
        Assert.Null(curve);
        Assert.Contains(problems, p => p.Contains(expected));
    }

    [Fact]
    public void ProjectionMovesPointsOntoThePlane()
    {
        var problems = new List<string>();
        var line = ComponentCurveSpec.Parse(Obj("""{"line":{"start":[0,0,40],"end":[100,50,-10]}}"""), "c", problems)!;
        var projected = line.Project(new double[] { 0, 0, 0 }, new double[] { 0, 0, 1 });
        Assert.Equal(new double[] { 0, 0, 0 }, projected.Points[0]);
        Assert.Equal(new double[] { 100, 50, 0 }, projected.Points[1]);
        Assert.Null(projected.Validate());
    }

    [Fact]
    public void ProjectionOntoAnOffsetPlaneHonoursTheOrigin()
    {
        var point = ModelFamilyComponentGeometry.ProjectOntoPlane(new double[] { 10, 20, 30 }, new double[] { 0, 100, 0 }, new double[] { 0, 2, 0 });
        Assert.Equal(new double[] { 10, 100, 30 }, point);
        Assert.Equal(-80, ModelFamilyComponentGeometry.DistanceToPlane(new double[] { 10, 20, 30 }, new double[] { 0, 100, 0 }, new double[] { 0, 1, 0 }), 6);
    }

    [Fact]
    public void ALineAlongThePlaneNormalCollapsesAfterProjection()
    {
        var problems = new List<string>();
        var line = ComponentCurveSpec.Parse(Obj("""{"line":{"start":[10,10,0],"end":[10,10,50]}}"""), "c", problems)!;
        Assert.Empty(problems);
        var projected = line.Project(new double[] { 0, 0, 0 }, new double[] { 0, 0, 1 });
        Assert.Contains("shorter", projected.Validate());
    }

    [Fact]
    public void AnArcBecomesCollinearOnceProjected()
    {
        var problems = new List<string>();
        var arc = ComponentCurveSpec.Parse(Obj("""{"arc":{"start":[0,0,0],"mid":[50,0,50],"end":[100,0,0]}}"""), "c", problems)!;
        Assert.Empty(problems);
        Assert.Contains("one line", arc.Project(new double[] { 0, 0, 0 }, new double[] { 0, 0, 1 }).Validate());
    }

    [Fact]
    public void SymbolicAndModelLineGroupsNeedCurves()
    {
        Parse("""{"symbolicLines":[{"view":"plan"}],"modelLines":[{"curves":[]}]}""", out var problems);
        Assert.Contains(problems, p => p.Contains("symbolicLines[0].curves is required"));
        Assert.Contains(problems, p => p.Contains("modelLines[0].curves is required"));
    }

    [Fact]
    public void ALineGroupTakesItsOwnPlaneKeyOnly()
    {
        Parse("""{"symbolicLines":[{"workPlane":"level","curves":[{"line":{"start":[0,0],"end":[1,0]}}]}]}""", out var problems);
        Assert.Contains(problems, p => p.Contains("symbolicLines[0].workPlane: unknown key"));
    }

    // ── created references ────────────────────────────────────────────────────

    [Theory]
    [InlineData("instances[0]", "instances", 0)]
    [InlineData(" symbolicLines[12] ", "symbolicLines", 12)]
    [InlineData("modelLines[3]", "modelLines", 3)]
    public void CreatedReferencesParse(string text, string section, int index)
    {
        Assert.True(ModelFamilyComponentRefs.TryParseCreated(text, out var s, out var i));
        Assert.Equal(section, s);
        Assert.Equal(index, i);
    }

    [Theory]
    [InlineData("nestedFamilies[0]")]
    [InlineData("instances")]
    [InlineData("instances[-1]")]
    [InlineData("")]
    public void OtherTextIsNotACreatedReference(string text) =>
        Assert.False(ModelFamilyComponentRefs.TryParseCreated(text, out _, out _));

    [Fact]
    public void ACreatedReferenceMustPointAtAnItemOfThisCall()
    {
        Parse("""
        {
          "instances":[{"family":"A"}],
          "visibility":[{"elements":[{"created":"instances[0]"},{"created":"instances[1]"},{"created":"modelLines[0]"}],"visible":true}],
          "setSubcategory":[{"elements":[{"created":"nestedFamilies[0]"}],"subcategory":"X"}]
        }
        """, out var problems);
        Assert.Contains(problems, p => p.Contains("visibility[0].elements[1].created") && p.Contains("no instances[1]"));
        Assert.Contains(problems, p => p.Contains("visibility[0].elements[2].created") && p.Contains("0 item(s) in modelLines"));
        Assert.Contains(problems, p => p.Contains("setSubcategory[0].elements[0].created"));
        Assert.DoesNotContain(problems, p => p.Contains("elements[0].created: this call"));
    }

    [Fact]
    public void ResolvingUsesWhatEarlierListsCreated()
    {
        var plan = Parse("""
        {
          "instances":[{"family":"A"}],
          "visibility":[{"elements":[5,{"created":"instances[0]"},"instances[0]"],"visible":false}]
        }
        """, out var problems);
        Assert.Empty(problems);
        var created = new Dictionary<string, List<long>> { ["instances[0]"] = new() { 42 } };
        var ids = new List<long>();
        foreach (var reference in plan.Visibility[0].Elements)
        {
            Assert.True(ModelFamilyComponentRefs.Resolve(reference, created, out var resolved, out var error), error);
            ids.AddRange(resolved);
        }
        Assert.Equal(new long[] { 5, 42, 42 }, ids);
    }

    [Fact]
    public void AReferenceToAnItemThatCreatedNothingCannotBeResolved()
    {
        var reference = new ComponentElementRef { Created = "instances[0]", Source = "visibility[0].elements[0]" };
        Assert.False(ModelFamilyComponentRefs.Resolve(reference, new Dictionary<string, List<long>>(), out _, out var error));
        Assert.Contains("did not create", error);
    }

    // ── setSubcategory and visibility lists ───────────────────────────────────

    [Fact]
    public void SetSubcategoryNeedsASubcategoryKey()
    {
        Parse("""{"setSubcategory":[{"elements":[1]}]}""", out var problems);
        Assert.Contains(problems, p => p.Contains("setSubcategory[0].subcategory is required"));
    }

    [Fact]
    public void VisibilityEntryNeedsSomethingToChange()
    {
        Parse("""{"visibility":[{"elements":[1]}]}""", out var problems);
        Assert.Contains(problems, p => p.Contains("visibility[0]") && p.Contains("say what to change"));
    }

    [Fact]
    public void VisibilityEntryReadsDetailLevelsViewsAndParameter()
    {
        var plan = Parse("""
        {"visibility":[{"elements":[1,2],"visible":true,"visibleParameter":null,
          "detailLevels":{"coarse":false,"fine":true},"views":{"planRcp":false,"whenCut":true}}]}
        """, out var problems);
        Assert.Empty(problems);
        var request = plan.Visibility[0].Request;
        Assert.True(request.Visible);
        Assert.True(request.VisibleParameterGiven);
        Assert.Null(request.VisibleParameter);
        Assert.Equal(false, request.Coarse);
        Assert.Null(request.Medium);
        Assert.Equal(false, request.PlanRcp);
        Assert.Equal(true, request.WhenCut);
        Assert.True(request.HasDetailLevels && request.HasViews);
    }

    [Theory]
    [InlineData("""{"visibility":[{"elements":[1],"detailLevels":{"huge":true}}]}""", "detailLevels")]
    [InlineData("""{"visibility":[{"elements":[1],"views":{"plan":true}}]}""", "views")]
    [InlineData("""{"visibility":[{"elements":[1],"visible":"maybe"}]}""", "visible")]
    [InlineData("""{"visibility":[{"elements":[1],"visibleParameter":""}]}""", "visibleParameter")]
    public void BadVisibilityEntriesAreProblems(string json, string expected)
    {
        Parse(json, out var problems);
        Assert.Contains(problems, p => p.Contains("visibility[0]") && p.Contains(expected));
    }

    // ── visibility merge ──────────────────────────────────────────────────────

    private static ComponentVisibilityRequest Request(string json)
    {
        var problems = new List<string>();
        var request = ComponentVisibilityRequest.Parse(Obj(json), "v", problems);
        Assert.Empty(problems);
        return request!;
    }

    [Fact]
    public void PlanSymbolScenarioHidesTheFormsInPlanOnly()
    {
        var current = new ComponentVisibilityFlags();   // a form: shown everywhere
        var merged = ComponentVisibilityFlags.Merge(current, Request("""{"views":{"planRcp":false}}"""), "v", new List<string>(), out var error);
        Assert.Null(error);
        Assert.False(merged!.PlanRcp);
        Assert.True(merged.FrontBack);
        Assert.True(merged.LeftRight);
        Assert.True(merged.Coarse && merged.Medium && merged.Fine);
        Assert.False(current.SameAs(merged));
        Assert.True(current.Copy().SameAs(current));
    }

    [Fact]
    public void WhenCutMeansPlanRcpCutForModelAndOnlyWhenCutForViewSpecific()
    {
        var model = ComponentVisibilityFlags.Merge(new ComponentVisibilityFlags(), Request("""{"views":{"whenCut":true}}"""), "v", new List<string>(), out _)!;
        Assert.True(model.PlanRcpCut);
        Assert.False(model.OnlyWhenCut);
        var symbolic = ComponentVisibilityFlags.Merge(new ComponentVisibilityFlags { ViewSpecific = true }, Request("""{"views":{"whenCut":true}}"""), "v", new List<string>(), out _)!;
        Assert.True(symbolic.OnlyWhenCut);
        Assert.False(symbolic.PlanRcpCut);
    }

    [Fact]
    public void ViewSpecificElementsSkipModelViewFlagsWithAWarning()
    {
        var warnings = new List<string>();
        var merged = ComponentVisibilityFlags.Merge(new ComponentVisibilityFlags { ViewSpecific = true },
            Request("""{"views":{"planRcp":false,"frontBack":false}}"""), "v", warnings, out var error)!;
        Assert.Null(error);
        Assert.Equal(2, warnings.Count);
        Assert.True(merged.PlanRcp && merged.FrontBack);
    }

    [Fact]
    public void AllDetailLevelsOffIsRefused()
    {
        var merged = ComponentVisibilityFlags.Merge(new ComponentVisibilityFlags(),
            Request("""{"detailLevels":{"coarse":false,"medium":false,"fine":false}}"""), "v", new List<string>(), out var error);
        Assert.Null(merged);
        Assert.Contains("at least one", error);
    }

    [Fact]
    public void TurningOffTheLastDetailLevelOneByOneIsRefused()
    {
        var current = new ComponentVisibilityFlags { Coarse = false, Medium = false, Fine = true };
        var merged = ComponentVisibilityFlags.Merge(current, Request("""{"detailLevels":{"fine":false}}"""), "v", new List<string>(), out var error);
        Assert.Null(merged);
        Assert.NotNull(error);
    }

    [Fact]
    public void WritesTurnALevelOnBeforeTurningAnotherOff()
    {
        // Revit refuses IsShownInCoarse=false while medium and fine are still off, so medium must be written first.
        var current = new ComponentVisibilityFlags { Coarse = true, Medium = false, Fine = false };
        var target = ComponentVisibilityFlags.Merge(current, Request("""{"detailLevels":{"coarse":false,"medium":true}}"""), "v", new List<string>(), out var error)!;
        Assert.Null(error);
        var writes = ComponentVisibilityFlags.Writes(current, target);
        Assert.Equal(new[] { ("Medium", true), ("Coarse", false) }, writes);
    }

    [Fact]
    public void WritesOnlyTheFlagsThatChange()
    {
        // For categories cut in the instance, Revit ties IsShownInPlanRCPCut to IsShownInTopBottom: writing the old
        // cut value back after planRcp=false would undo it, so unchanged flags are not written at all.
        var current = new ComponentVisibilityFlags { PlanRcpCut = true };
        var target = ComponentVisibilityFlags.Merge(current, Request("""{"views":{"planRcp":false}}"""), "v", new List<string>(), out _)!;
        Assert.Equal(new[] { ("TopBottom", false) }, ComponentVisibilityFlags.Writes(current, target));
        Assert.Empty(ComponentVisibilityFlags.Writes(current, current.Copy()));
    }

    [Fact]
    public void WritesOfAViewSpecificElementUseOnlyWhenCut()
    {
        var current = new ComponentVisibilityFlags { ViewSpecific = true };
        var target = ComponentVisibilityFlags.Merge(current, Request("""{"views":{"whenCut":true},"detailLevels":{"fine":false}}"""), "v", new List<string>(), out _)!;
        Assert.Equal(new[] { ("Fine", false), ("OnlyWhenCut", true) }, ComponentVisibilityFlags.Writes(current, target));
    }

    [Fact]
    public void FlagsDescribeThemselves()
    {
        var model = new ComponentVisibilityFlags { PlanRcp = false }.ToJson();
        Assert.Equal("model", (string?)model["kind"]);
        Assert.False((bool)model["views"]!["planRcp"]!);
        var symbolic = new ComponentVisibilityFlags { ViewSpecific = true, OnlyWhenCut = true }.ToJson();
        Assert.Equal("viewSpecific", (string?)symbolic["kind"]);
        Assert.True((bool)symbolic["views"]!["whenCut"]!);
    }

    [Fact]
    public void InlineVisibilityShortcutSetsTheParameter()
    {
        var plan = Parse("""{"symbolicLines":[{"curves":[{"line":{"start":[0,0],"end":[10,0]}}],"visibleParameter":"Show Symbol"}]}""", out var problems);
        Assert.Empty(problems);
        Assert.Equal("Show Symbol", plan.SymbolicLines[0].Visibility!.VisibleParameter);
    }

    // ── tool surface ──────────────────────────────────────────────────────────

    private static readonly string[] Names =
        { "revit_preview_edit_model_family_components", "revit_edit_model_family_components" };

    [Fact]
    public void BothToolsExistWithTheRightPermissions()
    {
        var addin = ToolSourceScan.Addin.Value;
        Assert.Equal("ReadOnly", addin[Names[0]].Permission);
        Assert.Equal("RequiresApproval", addin[Names[1]].Permission);
        Assert.Contains("IApprovalSummaryTool", addin[Names[1]].Source);
        Assert.Contains("ModelFamilySession.Edit(", addin[Names[0]].Source);
        Assert.Contains("apply: false", addin[Names[0]].Source);
        Assert.Contains("apply: true", addin[Names[1]].Source);
    }

    [Fact]
    public void TheBridgeAdvertisesBothAndOnlyThePreviewIsReadOnly()
    {
        var bridge = ToolSourceScan.Bridge.Value.ToDictionary(t => t.Name);
        Assert.True(bridge[Names[0]].ReadOnly);
        Assert.False(bridge[Names[1]].ReadOnly);
        foreach (var name in Names) Assert.Contains(name, bridge[name].Targets);
    }

    [Fact]
    public void TheBridgeSendsEveryListAndTheTarget()
    {
        var source = ToolSourceScan.Bridge.Value.First(t => t.Name == Names[1]).Source;
        foreach (var key in ModelFamilyComponentPlan.Order) Assert.Contains($"[\"{key}\"]", source);
        Assert.Contains("ModelFamilyArgs(", source);
    }

    [Fact]
    public void BothToolsAreInTheFamilyEditorGroup()
    {
        foreach (var name in Names) Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(name));
    }

    [Fact]
    public void TheEditToolIsPlannedAsCreating()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_edit_model_family_components"));
        Assert.NotEqual(PlanToolKind.Creates, PlanToolClassification.Classify("revit_preview_edit_model_family_components"));
    }

    [Fact]
    public void ARunSendsOnlyKeysThePlanReads()
    {
        // The executor reads the lists through ModelFamilyJson with these literals; the bridge sends the same eight.
        var parser = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin", "ModelFamilies", "ModelFamilyComponentSpecs.cs"));
        foreach (var key in ModelFamilyComponentPlan.Order) Assert.Contains($"\"{key}\"", parser);
    }

    [Fact]
    public void InspectionSectionsAreRegistered()
    {
        var source = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin", "Tools", "ModelFamilies", "ModelFamilyComponentInspect.cs"));
        foreach (var section in new[] { "subcategories", "nestedFamilies", "nestedInstances", "symbolicLines", "modelLines", "visibility" })
            Assert.Contains($"AddSection(\"{section}\"", source.Replace("\n        ", " ").Replace("\r", string.Empty));
    }
}
