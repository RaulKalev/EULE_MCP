using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Parameters;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#147 phases 2 and 3: family and shared parameters, and labels through nested Generic Annotation families.</summary>
public class DetailFamilyExtrasTests
{
    private static (DetailFamilySpec Spec, List<string> Errors) Parse(params (string Key, string Json)[] values)
    {
        var args = new Dictionary<string, object?>
        {
            ["savePath"] = @"C:\Families\Detail",
            ["familyName"] = "EL_Kilp",
            ["lines"] = JToken.Parse("[{\"start\":[0,0],\"end\":[10,0]}]")
        };
        foreach (var (key, json) in values) args[key] = JToken.Parse(json);
        var errors = new List<string>();
        return (DetailFamilySpecParser.Parse(args, errors), errors);
    }

    // ── Parameters ───────────────────────────────────────────────────────────

    [Fact]
    public void ParsesFamilyParametersWithDefaults()
    {
        var (spec, errors) = Parse(("parameters",
            "[{\"name\":\"Kilbi tähis\",\"dataType\":\"text\",\"value\":\"K1\"}," +
            "{\"name\":\"Laius\",\"dataType\":\"length\",\"value\":120,\"instance\":true}," +
            "{\"name\":\"Näita raami\",\"dataType\":\"yesNo\",\"value\":\"jah\"}," +
            "{\"name\":\"Kõrgus\",\"dataType\":\"length\",\"formula\":\"Laius / 2\",\"group\":\"Identity Data\"}]"));
        Assert.Empty(errors);
        Assert.Equal(4, spec.Parameters.Count);

        var text = spec.Parameters[0];
        Assert.Equal("Text", text.GroupKey);
        Assert.False(text.IsInstance);
        Assert.False(text.Shared);

        Assert.Equal("Geometry", spec.Parameters[1].GroupKey);
        Assert.True(spec.Parameters[1].IsInstance);
        Assert.Equal("Graphics", spec.Parameters[2].GroupKey);
        Assert.Equal("IdentityData", spec.Parameters[3].GroupKey);
        Assert.Equal("Laius / 2", spec.Parameters[3].Formula);
    }

    [Fact]
    public void FamilyParametersNeedADataTypeAndAValidName()
    {
        var (_, errors) = Parse(("parameters",
            "[{\"name\":\"Ilma tüübita\"},{\"name\":\"Halb[nimi]\",\"dataType\":\"text\"},{\"name\":\"X\",\"dataType\":\"kaal\"}," +
            "{\"dataType\":\"text\"},{\"name\":\"X2\",\"dataType\":\"text\"},{\"name\":\"x2\",\"dataType\":\"text\"}]"));
        Assert.Contains(errors, e => e.Contains("needs a dataType"));
        Assert.Contains(errors, e => e.Contains("characters Revit refuses"));
        Assert.Contains(errors, e => e.Contains("'kaal' is not known"));
        Assert.Contains(errors, e => e.Contains("parameters[3] needs a name"));
        Assert.Contains(errors, e => e.Contains("listed twice"));
    }

    [Fact]
    public void ValueAndFormulaTogetherAreRefused()
    {
        var (_, errors) = Parse(("parameters", "[{\"name\":\"A\",\"dataType\":\"number\",\"value\":1,\"formula\":\"2\"}]"));
        Assert.Contains(errors, e => e.Contains("not both"));
    }

    [Fact]
    public void SharedParametersNeedNoDataType()
    {
        var (spec, errors) = Parse(("parameters",
            "[{\"name\":\"EULE_Tähis\",\"shared\":true},{\"guid\":\"6f1c2d3e-0000-4000-8000-000000000001\"},{\"guid\":\"not-a-guid\"}]"));
        Assert.Single(errors);
        Assert.Contains("is not a GUID", errors[0]);
        Assert.True(spec.Parameters[0].Shared);
        Assert.True(spec.Parameters[1].Shared);
        Assert.Null(spec.Parameters[0].Spec);
        Assert.Equal("Data", spec.Parameters[0].GroupKey);
    }

    [Theory]
    [InlineData("length", "1000", 1000 / 304.8)]
    [InlineData("area", "1", 10.763910416709722)]
    [InlineData("angle", "180", Math.PI)]
    [InlineData("number", "\"2.5\"", 2.5)]
    public void ValuesBecomeInternalUnits(string friendly, string json, double expected)
    {
        var (value, error) = DetailFamilySpecParser.ConvertValue(friendly, JToken.Parse(json));
        Assert.Null(error);
        Assert.Equal(expected, (double)value!, 9);
    }

    [Fact]
    public void IntegersTextAndYesNoKeepTheirKinds()
    {
        Assert.Equal(3, DetailFamilySpecParser.ConvertValue("integer", JToken.Parse("3")).Value);
        Assert.NotNull(DetailFamilySpecParser.ConvertValue("integer", JToken.Parse("3.5")).Error);
        Assert.Equal(1, DetailFamilySpecParser.ConvertValue("yesNo", JToken.Parse("true")).Value);
        Assert.Equal(0, DetailFamilySpecParser.ConvertValue("yesNo", JToken.Parse("\"ei\"")).Value);
        Assert.NotNull(DetailFamilySpecParser.ConvertValue("yesNo", JToken.Parse("\"maybe\"")).Error);
        Assert.Equal("12", DetailFamilySpecParser.ConvertValue("text", JToken.Parse("12")).Value);
        Assert.NotNull(DetailFamilySpecParser.ConvertValue("electricalPotential", JToken.Parse("230")).Error);
    }

    [Fact]
    public void ABadValueIsAProblemInThePreview()
    {
        var (_, errors) = Parse(("parameters", "[{\"name\":\"Laius\",\"dataType\":\"length\",\"value\":\"lai\"}]"));
        Assert.Contains(errors, e => e.Contains("must be a number (mm)"));
    }

    // ── Shared definitions ───────────────────────────────────────────────────

    private static readonly List<SharedDefinitionInfo> Definitions = new()
    {
        new() { Group = "EULE", Name = "EULE_Tähis", Guid = "11111111-1111-1111-1111-111111111111", DataTypeId = "autodesk.spec:spec.string-2.0.0" },
        new() { Group = "EL", Name = "Kilp", Guid = "22222222-2222-2222-2222-222222222222", DataTypeId = "autodesk.spec:spec.string-2.0.0" },
        new() { Group = "EN", Name = "Kilp", Guid = "33333333-3333-3333-3333-333333333333", DataTypeId = "autodesk.spec:spec.string-2.0.0" }
    };

    private static FamilyParameterSpec Shared(string name = "", string guid = "", string group = "") =>
        new() { Source = "parameters[0]", Name = name, SharedGuid = guid, SharedGroup = group, Shared = true };

    [Fact]
    public void FindsSharedDefinitionsByNameOrGuid()
    {
        Assert.Equal("11111111-1111-1111-1111-111111111111", DetailFamilySpecParser.MatchSharedDefinition(Shared("EULE_Tähis"), Definitions, "f").Definition!.Guid);
        Assert.Equal("EN", DetailFamilySpecParser.MatchSharedDefinition(Shared("Kilp", group: "en"), Definitions, "f").Definition!.Group);
        Assert.Equal("Kilp", DetailFamilySpecParser.MatchSharedDefinition(Shared(guid: "22222222-2222-2222-2222-222222222222"), Definitions, "f").Definition!.Name);
    }

    [Fact]
    public void NeverCreatesAMissingSharedDefinition()
    {
        var (definition, error) = DetailFamilySpecParser.MatchSharedDefinition(Shared("EULE_Tähis2"), Definitions, "'C:\\p.txt'");
        Assert.Null(definition);
        Assert.Contains("never creates shared parameters", error);
        Assert.Contains("'EULE_Tähis' (EULE)", error);
    }

    [Fact]
    public void AmbiguousOrMismatchedSharedDefinitionsAreRefused()
    {
        Assert.Contains("several groups", DetailFamilySpecParser.MatchSharedDefinition(Shared("Kilp"), Definitions, "f").Error);
        Assert.Contains("not 'Muu'", DetailFamilySpecParser.MatchSharedDefinition(
            Shared("Muu", guid: "22222222-2222-2222-2222-222222222222"), Definitions, "f").Error);
        Assert.Contains("no definition with guid", DetailFamilySpecParser.MatchSharedDefinition(
            Shared(guid: "44444444-4444-4444-4444-444444444444"), Definitions, "f").Error);
    }

    // ── Labels ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesLabels()
    {
        var (spec, errors) = Parse(("labels",
            "[{\"text\":\"K1\",\"position\":[0,5]},{\"text\":\"230 V\",\"position\":{\"x\":-3,\"y\":0},\"size\":1.8,\"alignment\":\"centre\",\"rotation\":90,\"textType\":\"2.5mm Arial\"}]"));
        Assert.Empty(errors);
        Assert.Equal(2, spec.Labels.Count);
        Assert.Equal("left", spec.Labels[0].Alignment);
        Assert.Null(spec.Labels[0].SizeMm);
        Assert.Equal("center", spec.Labels[1].Alignment);
        Assert.Equal(1.8, spec.Labels[1].SizeMm);
        Assert.Equal(90, spec.Labels[1].RotationDeg);
        Assert.Equal(new[] { "2.5mm Arial" }, DetailFamilySpecParser.LabelTextTypeNames(spec));
    }

    [Fact]
    public void BadLabelsAreRefused()
    {
        var (_, errors) = Parse(("labels",
            "[{\"text\":\" \",\"position\":[0,0]},{\"text\":\"A\"},{\"text\":\"A\",\"position\":[0,0],\"size\":80},{\"text\":\"A\",\"position\":[0,0],\"alignment\":\"top\"}]"));
        Assert.Contains(errors, e => e.Contains("labels[0].text is empty"));
        Assert.Contains(errors, e => e.Contains("labels[1].position"));
        Assert.Contains(errors, e => e.Contains("labels[2].size"));
        Assert.Contains(errors, e => e.Contains("labels[3].alignment"));
    }

    [Fact]
    public void LabelsAloneAreEnoughToDraw()
    {
        var args = new Dictionary<string, object?>
        {
            ["savePath"] = @"C:\Families\Detail",
            ["familyName"] = "EL_Tekst",
            ["labels"] = JToken.Parse("[{\"text\":\"K1\",\"position\":[0,0]}]")
        };
        var errors = new List<string>();
        DetailFamilySpecParser.Parse(args, errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void IdenticalLabelsShareOneAnnotationFamily()
    {
        var (spec, _) = Parse(("labels",
            "[{\"text\":\"K1\",\"position\":[0,0]},{\"text\":\"K2\",\"position\":[5,0]},{\"text\":\"K1\",\"position\":[10,0],\"rotation\":90}," +
            "{\"text\":\"K1\",\"position\":[0,9],\"size\":5}]"));
        var errors = new List<string>();
        var groups = DetailFamilySpecParser.GroupLabels(spec, errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "EL_Kilp_Text1", "EL_Kilp_Text2", "EL_Kilp_Text3" }, groups.Select(g => g.FamilyName));
        Assert.Equal(2, groups[0].Instances.Count);
        Assert.Single(groups[2].Instances);
    }

    [Fact]
    public void FindsTheAnnotationTemplateInTheAnnotationsFolder()
    {
        var files = new HashSet<string> { @"C:\T\English\Annotations\Metric Generic Annotation.rft" };
        var (path, error) = DetailFamilySpecParser.ResolveTemplate("", @"C:\T", files.Contains, _ => [],
            DetailFamilySpecParser.DefaultAnnotationTemplateFileName);
        Assert.Null(error);
        Assert.Equal(@"C:\T\English\Annotations\Metric Generic Annotation.rft", path);
        Assert.Contains("pass annotationTemplatePath", DetailFamilySpecParser.ResolveTemplate("", @"C:\none", _ => false, _ => [],
            DetailFamilySpecParser.DefaultAnnotationTemplateFileName).Error);
    }
}
