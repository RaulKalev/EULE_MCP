using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#168: the shared pure layer of the model-family tools, the target argument and the JSON readers.</summary>
public class ModelFamilySharedTests
{
    private static (ModelFamilyTargetSpec Spec, List<string> Problems) Parse(object? target, ModelFamilySessionMode mode)
    {
        var problems = new List<string>();
        return (ModelFamilyTargetSpec.Parse(target, mode, problems), problems);
    }

    // ── ModelFamilyTargetSpec: kinds ─────────────────────────────────────────

    [Fact]
    public void LoadedFamilyIsEditedWithoutSaveArguments()
    {
        var (spec, problems) = Parse(new JObject { ["family"] = " ELV_Device ", ["overwriteParameterValues"] = true }, ModelFamilySessionMode.Edit);
        Assert.Empty(problems);
        Assert.Equal(ModelFamilyTargetKind.LoadedFamily, spec.Kind);
        Assert.Equal("ELV_Device", spec.Family);
        Assert.True(spec.OverwriteParameterValues);
    }

    [Fact]
    public void FileNeedsOverwriteOrSavePathToBeEdited()
    {
        var (spec, problems) = Parse(new JObject { ["path"] = @"C:\fam\Device.rfa" }, ModelFamilySessionMode.Edit);
        Assert.Equal(ModelFamilyTargetKind.File, spec.Kind);
        Assert.Contains(problems, p => p.StartsWith("target: pass overwrite=true", StringComparison.Ordinal));

        Assert.Empty(Parse(new JObject { ["path"] = @"C:\fam\Device.rfa", ["overwrite"] = true }, ModelFamilySessionMode.Edit).Problems);
        Assert.Empty(Parse(new JObject { ["path"] = @"C:\fam\Device.rfa", ["savePath"] = @"C:\out" }, ModelFamilySessionMode.Edit).Problems);
        // Reading needs neither.
        Assert.Empty(Parse(new JObject { ["path"] = @"C:\fam\Device.rfa" }, ModelFamilySessionMode.Read).Problems);
    }

    [Fact]
    public void FileMustBeAnAbsoluteRfaPath()
    {
        Assert.Contains(Parse(new JObject { ["path"] = @"C:\fam\Device.rvt" }, ModelFamilySessionMode.Read).Problems,
            p => p.Contains("is not a family file (.rfa)", StringComparison.Ordinal));
        Assert.Contains(Parse(new JObject { ["path"] = @"fam\Device.rfa" }, ModelFamilySessionMode.Read).Problems,
            p => p.Contains("must be an absolute path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("Device.rfa")]
    public void OpenDocumentNeedsNoSaveArguments(string title)
    {
        var (spec, problems) = Parse(new JObject { ["familyDocument"] = title, ["save"] = true, ["load"] = "true" }, ModelFamilySessionMode.Edit);
        Assert.Empty(problems);
        Assert.Equal(ModelFamilyTargetKind.OpenDocument, spec.Kind);
        Assert.True(spec.Save);
        Assert.True(spec.Load);
    }

    [Fact]
    public void CreateIsATemplateTarget()
    {
        var (spec, problems) = Parse(new JObject { ["savePath"] = @"C:\fam", ["familyName"] = "ELV_Device", ["templatePath"] = @"C:\t\Metric Generic Model.rft", ["overwrite"] = true }, ModelFamilySessionMode.Create);
        Assert.Empty(problems);
        Assert.Equal(ModelFamilyTargetKind.Template, spec.Kind);
        Assert.Equal("ELV_Device", spec.FamilyName);
        Assert.True(spec.Overwrite);
        Assert.Equal("Metric Generic Model.rft", ModelFamilyTargetSpec.DefaultTemplateFileName);
    }

    // ── ModelFamilyTargetSpec: problems ──────────────────────────────────────

    [Fact]
    public void MissingTargetNamesWhatToGive()
    {
        Assert.Contains("{savePath, familyName?, templatePath?}", Assert.Single(Parse(null, ModelFamilySessionMode.Create).Problems));
        Assert.Contains("{familyDocument}", Assert.Single(Parse(null, ModelFamilySessionMode.Edit).Problems));
        Assert.Contains("{family}", Assert.Single(Parse("  ", ModelFamilySessionMode.Read).Problems));
        Assert.Contains("{family}", Assert.Single(Parse(JValue.CreateNull(), ModelFamilySessionMode.Read).Problems));
        Assert.Contains("give family", Assert.Single(Parse(new JObject(), ModelFamilySessionMode.Edit).Problems));
    }

    [Fact]
    public void TargetMustBeAnObject()
    {
        Assert.Equal("target: must be an object.", Assert.Single(Parse(new JArray(1), ModelFamilySessionMode.Edit).Problems));
        Assert.StartsWith("target: must be an object, e.g.", Assert.Single(Parse("not json", ModelFamilySessionMode.Edit).Problems));
        // A JSON string, a JValue string and a plain object are all read.
        Assert.Equal(ModelFamilyTargetKind.LoadedFamily, Parse("{\"family\":\"A\"}", ModelFamilySessionMode.Read).Spec.Kind);
        Assert.Equal(ModelFamilyTargetKind.LoadedFamily, Parse(new JValue("{\"family\":\"A\"}"), ModelFamilySessionMode.Read).Spec.Kind);
        Assert.Equal(ModelFamilyTargetKind.LoadedFamily, Parse(new Dictionary<string, object> { ["family"] = "A" }, ModelFamilySessionMode.Read).Spec.Kind);
    }

    [Fact]
    public void UnknownKeysAreProblems()
    {
        var problems = Parse(new JObject { ["family"] = "A", ["saveAs"] = "x" }, ModelFamilySessionMode.Read).Problems;
        Assert.Contains(problems, p => p.StartsWith("target.saveAs: unknown key. Known:", StringComparison.Ordinal));
    }

    [Fact]
    public void OneTargetOnly()
    {
        var problems = Parse(new JObject { ["family"] = "A", ["path"] = @"C:\a.rfa" }, ModelFamilySessionMode.Edit).Problems;
        Assert.Equal("target: give one of family, path and familyDocument, not family and path.", Assert.Single(problems));
    }

    [Fact]
    public void CreateRefusesExistingTargetsAndChecksItsArguments()
    {
        var problems = Parse(new JObject { ["family"] = "A", ["templatePath"] = @"C:\t\x.rfa", ["save"] = true }, ModelFamilySessionMode.Create).Problems;
        Assert.Contains(problems, p => p.StartsWith("target.family: a new family takes no existing target", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("target.savePath is required", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("is not a family template (.rft)", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("target.save applies to an open family document only; a new family", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyCreateTakesATemplate()
    {
        var problems = Parse(new JObject { ["family"] = "A", ["templatePath"] = @"C:\t\x.rft" }, ModelFamilySessionMode.Edit).Problems;
        Assert.Equal("target.templatePath: only the create tool starts from a template.", Assert.Single(problems));
    }

    [Fact]
    public void LoadedFamilyRefusesSaveAndRename()
    {
        var problems = Parse(new JObject { ["family"] = "A", ["save"] = true, ["familyName"] = "B" }, ModelFamilySessionMode.Edit).Problems;
        Assert.Contains(problems, p => p.StartsWith("target.save applies to an open family document only; a loaded family", StringComparison.Ordinal));
        Assert.Contains(problems, p => p == "target.familyName: a loaded family keeps its name.");
        // Reading ignores them.
        Assert.Empty(Parse(new JObject { ["family"] = "A", ["save"] = true, ["familyName"] = "B" }, ModelFamilySessionMode.Read).Problems);
    }

    [Fact]
    public void FileRefusesSave()
    {
        var problems = Parse(new JObject { ["path"] = @"C:\a.rfa", ["save"] = true, ["overwrite"] = true }, ModelFamilySessionMode.Edit).Problems;
        Assert.StartsWith("target.save applies to an open family document only; pass overwrite=true", Assert.Single(problems));
    }

    [Fact]
    public void EveryParseProblemIsATargetProblem()
    {
        var targets = new object?[]
        {
            null, "x", new JArray(), new JObject { ["bad"] = 1 }, new JObject { ["family"] = "A", ["path"] = @"C:\a.rfa" },
            new JObject { ["path"] = "a.rvt", ["save"] = true }, new JObject { ["family"] = "A", ["templatePath"] = @"C:\x.rft" }
        };
        foreach (var mode in new[] { ModelFamilySessionMode.Read, ModelFamilySessionMode.Edit, ModelFamilySessionMode.Create })
            foreach (var target in targets)
                foreach (var problem in Parse(target, mode).Problems)
                    Assert.True(ModelFamilyTargetSpec.IsTargetProblem(problem), problem);
        Assert.False(ModelFamilyTargetSpec.IsTargetProblem("extrusions[0].depth: must be a number."));
    }

    // ── ModelFamilyTargetSpec: descriptions ──────────────────────────────────

    [Fact]
    public void DescribeNamesTheTarget()
    {
        Assert.Equal("the loaded family 'A'", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.LoadedFamily, Family = "A" }.Describe());
        Assert.Equal(@"the family file 'C:\a.rfa'", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.File, Path = @"C:\a.rfa" }.Describe());
        Assert.Equal("the active family document", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.OpenDocument, FamilyDocument = "Active" }.Describe());
        Assert.Equal("the open family document 'D'", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.OpenDocument, FamilyDocument = "D" }.Describe());
        Assert.Equal(@"a new family at 'C:\fam'", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.Template, SavePath = @"C:\fam" }.Describe());
        Assert.Equal("the family", new ModelFamilyTargetSpec().Describe());
    }

    [Fact]
    public void DescribeOutcomeSaysWhatHappensAfterwards()
    {
        Assert.Equal("load it back into the project as one undo step (keeping the project's parameter values)",
            new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.LoadedFamily }.DescribeOutcome());
        Assert.Equal(@"load it back into the project as one undo step (with the family's parameter values) and save a copy to 'C:\out'",
            new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.LoadedFamily, OverwriteParameterValues = true, SavePath = @"C:\out" }.DescribeOutcome());
        Assert.Equal(@"save it back to 'C:\a.rfa', replacing it",
            new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.File, Path = @"C:\a.rfa", Overwrite = true }.DescribeOutcome());
        Assert.Equal(@"save it to 'C:\b.rfa' (replacing an existing file) and load it into the project (keeping the project's parameter values)",
            new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.File, Path = @"C:\a.rfa", SavePath = @"C:\b.rfa", Overwrite = true, Load = true }.DescribeOutcome());
        Assert.Equal("leave it open and unsaved", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.OpenDocument }.DescribeOutcome());
        Assert.Equal("save it", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.OpenDocument, Save = true }.DescribeOutcome());
        Assert.Equal(@"save it as 'C:\b.rfa' and load it into the project (with the family's parameter values)",
            new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.OpenDocument, SavePath = @"C:\b.rfa", Load = true, OverwriteParameterValues = true }.DescribeOutcome());
        Assert.Equal(@"save it to 'C:\fam'", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.Template, SavePath = @"C:\fam" }.DescribeOutcome());
        Assert.Equal(@"save it to 'C:\fam' (replacing an existing file)", new ModelFamilyTargetSpec { Kind = ModelFamilyTargetKind.Template, SavePath = @"C:\fam", Overwrite = true }.DescribeOutcome());
        Assert.Equal("nothing", new ModelFamilyTargetSpec().DescribeOutcome());
    }

    // ── ModelFamilyJson ──────────────────────────────────────────────────────

    [Fact]
    public void TokenReadsEveryArgumentShape()
    {
        var args = new Dictionary<string, object?>
        {
            ["json"] = "[1, 2]",
            ["jvalueJson"] = new JValue("{\"a\":1}"),
            ["plain"] = "hello",
            ["bracket"] = "[not json",
            ["empty"] = "   ",
            ["null"] = JValue.CreateNull(),
            ["raw"] = 5,
            ["token"] = new JObject { ["b"] = 2 }
        };
        Assert.IsType<JArray>(ModelFamilyJson.Token(args, "json"));
        Assert.IsType<JObject>(ModelFamilyJson.Token(args, "jvalueJson"));
        Assert.Equal("hello", (string?)ModelFamilyJson.Token(args, "plain"));
        Assert.Equal("[not json", (string?)ModelFamilyJson.Token(args, "bracket"));
        Assert.Null(ModelFamilyJson.Token(args, "empty"));
        Assert.Null(ModelFamilyJson.Token(args, "null"));
        Assert.Null(ModelFamilyJson.Token(args, "missing"));
        Assert.Equal(5, (int)ModelFamilyJson.Token(args, "raw")!);
        Assert.Equal(2, (int)ModelFamilyJson.Token(args, "token")!["b"]!);
    }

    [Fact]
    public void ObjectsAcceptsAListOrOneObject()
    {
        var problems = new List<string>();
        var args = new Dictionary<string, object?>
        {
            ["list"] = new JArray(new JObject { ["a"] = 1 }, 3, new JObject()),
            ["one"] = new JObject { ["a"] = 1 },
            ["text"] = "x"
        };
        Assert.Equal(2, ModelFamilyJson.Objects(args, "list", problems).Count);
        Assert.Equal("list[1]: must be an object.", Assert.Single(problems));
        problems.Clear();
        Assert.Single(ModelFamilyJson.Objects(args, "one", problems));
        Assert.Empty(ModelFamilyJson.Objects(args, "missing", problems));
        Assert.Empty(problems);
        Assert.Empty(ModelFamilyJson.Objects(args, "text", problems));
        Assert.Equal("text: must be a list of objects.", Assert.Single(problems));
    }

    [Fact]
    public void NumberReadsNumbersAndNumericTexts()
    {
        var problems = new List<string>();
        var obj = new JObject { ["a"] = 12.5, ["b"] = "3.25", ["c"] = "x", ["d"] = 4, ["E"] = JValue.CreateNull() };
        Assert.Equal(12.5, ModelFamilyJson.Number(obj, "a", "op", problems));
        Assert.Equal(3.25, ModelFamilyJson.Number(obj, "b", "op", problems));
        Assert.Equal(4, ModelFamilyJson.Number(obj, "D", "op", problems));
        Assert.Null(ModelFamilyJson.Number(obj, "e", "op", problems));
        Assert.Null(ModelFamilyJson.Number(obj, "missing", "op", problems));
        Assert.Empty(problems);
        Assert.Null(ModelFamilyJson.Number(obj, "c", "op", problems));
        Assert.Equal("op.c: must be a number.", Assert.Single(problems));
        Assert.Null(ModelFamilyJson.AsNumber(new JValue(double.NaN)));
        Assert.Equal(304.8, ModelFamilyJson.ToMm(ModelFamilyJson.ToFeet(304.8)));
    }

    [Fact]
    public void PointReadsTwoOrThreeCoordinatesInMillimetres()
    {
        var problems = new List<string>();
        Assert.Equal(new[] { 1.0, 2.0, 0.0 }, ModelFamilyJson.Point(new JArray(1, 2), "p", problems));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, ModelFamilyJson.Point(new JArray(1, "2", 3), "p", problems));
        Assert.Empty(problems);
        Assert.Null(ModelFamilyJson.Point(new JArray(1, 2), "p", problems, allow2D: false));
        Assert.Equal("p: must be [x, y, z] in millimetres.", Assert.Single(problems));
        problems.Clear();
        Assert.Null(ModelFamilyJson.Point(new JArray(1, 2, 3, 4), "p", problems));
        Assert.Null(ModelFamilyJson.Point(new JArray(1, "a"), "q", problems));
        Assert.Equal(new[] { "p: must be [x, y] or [x, y, z] in millimetres.", "q[1]: must be a number." }, problems);
        problems.Clear();
        var obj = new JObject { ["origin"] = new JArray(0, 0, 10) };
        Assert.Equal(10.0, ModelFamilyJson.Point(obj, "origin", "op", problems)![2]);
        Assert.Null(ModelFamilyJson.Point(obj, "normal", "op", problems));
        Assert.Equal("op.normal is required: [x, y, z] in millimetres.", Assert.Single(problems));
        Assert.Null(ModelFamilyJson.Point(obj, "normal", "op", problems, required: false));
        Assert.Single(problems);
    }

    [Fact]
    public void CheckKeysReportsUnknownKeysCaseInsensitively()
    {
        var problems = new List<string>();
        ModelFamilyJson.CheckKeys(new JObject { ["Depth"] = 1, ["dept"] = 2 }, "extrusions[0]", problems, "depth", "profile");
        Assert.Equal("extrusions[0].dept: unknown key. Known: depth, profile.", Assert.Single(problems));
    }

    [Fact]
    public void SuggestRanksExactThenContainedThenPrefix()
    {
        var known = new[] { "Width", "Height", "Wall Offset", "Depth", "Widthwise", "" };
        Assert.Equal("Width", ModelFamilyJson.Suggest("width", known)[0]);
        Assert.Equal(new[] { "Width", "Widthwise" }, ModelFamilyJson.Suggest("Widt", known));
        Assert.Equal(new[] { "Wall Offset" }, ModelFamilyJson.Suggest("Wallx", known));
        Assert.Empty(ModelFamilyJson.Suggest("Zz", known));
        Assert.Equal(2, ModelFamilyJson.Suggest("Width", known, max: 2).Count);
    }

    [Fact]
    public void StrAndBoolAndIdsReadLeniently()
    {
        var problems = new List<string>();
        var obj = new JObject { ["name"] = "  A ", ["n"] = 5, ["flag"] = "TRUE", ["bad"] = "maybe" };
        Assert.Equal("A", ModelFamilyJson.Str(obj, "name"));
        Assert.Equal("5", ModelFamilyJson.Str(obj, "n"));
        Assert.Equal("fallback", ModelFamilyJson.Str(obj, "missing", "fallback"));
        Assert.True(ModelFamilyJson.Bool(obj, "flag", "op", problems));
        Assert.Null(ModelFamilyJson.Bool(obj, "bad", "op", problems));
        Assert.Equal("op.bad: must be true or false.", Assert.Single(problems));
        Assert.Equal(12L, ModelFamilyJson.AsId(new JValue("12")));
        Assert.Null(ModelFamilyJson.AsId(new JValue(1.5)));
    }
}
