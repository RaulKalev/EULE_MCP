using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#172: family parameters, types, formulas and associations: the pure planning and the tool surface.</summary>
public class ModelFamilyParameterTests
{
    private static ModelFamilyParameterState P(string name, string? type = "length", bool instance = false, string formula = "", bool shared = false,
        string? guid = null, bool builtIn = false) => new()
    {
        Name = name, DataType = type, DataTypeLabel = type ?? string.Empty, Instance = instance, Formula = formula, Shared = shared, Guid = guid, BuiltIn = builtIn
    };

    private static ModelFamilyParameterContext Family(params ModelFamilyParameterState[] parameters)
    {
        var context = new ModelFamilyParameterContext();
        context.Parameters.AddRange(parameters);
        context.TypeNames.Add("Type 1");
        return context;
    }

    private static ModelFamilyParameterSpec Spec(object args, out List<string> problems)
    {
        problems = new List<string>();
        var dictionary = JObject.FromObject(args).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        return ModelFamilyParameterSpecParser.Parse(dictionary, problems);
    }

    private static ModelFamilyParameterPlan Plan(ModelFamilyParameterContext context, object args)
    {
        var spec = Spec(args, out var problems);
        Assert.Empty(problems);
        return ModelFamilyParameterPlanner.Plan(context, spec);
    }

    // ── Formulas ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReferencesMatchNamesWithSpacesLongestFirst()
    {
        var refs = ModelFamilyFormula.References("Door Width + Door / 2", new[] { "Door", "Door Width", "Height" });
        Assert.Equal(new[] { "Door Width", "Door" }, refs.Parameters);
        Assert.Empty(refs.Unknown);
    }

    [Fact]
    public void ReferencesIgnoreFunctionsNumbersUnitsAndQuotedText()
    {
        var refs = ModelFamilyFormula.References("if(and(Width > 10 mm, not(Flag)), round(sqrt(Width) * pi) + 2.5m, \"Height text\")", new[] { "Width", "Flag", "Height" });
        Assert.Equal(new[] { "Width", "Flag" }, refs.Parameters);
        Assert.Empty(refs.Unknown);
    }

    [Fact]
    public void ReferencesNeedWordBoundaries()
    {
        var refs = ModelFamilyFormula.References("Width2 * 2 + Width", new[] { "Width" });
        Assert.Equal(new[] { "Width" }, refs.Parameters);
        Assert.Equal(new[] { "Width2" }, refs.Unknown);
    }

    [Fact]
    public void UnknownIdentifiersAreReportedAsPhrases()
    {
        var refs = ModelFamilyFormula.References("Missing Thing * Width", new[] { "Width" });
        Assert.Equal(new[] { "Missing Thing" }, refs.Unknown);
        Assert.Equal(new[] { "Width" }, refs.Parameters);
    }

    [Fact]
    public void ReferencesHandleEmptyAndUnitAfterNumber()
    {
        Assert.Empty(ModelFamilyFormula.References("", new[] { "A" }).Parameters);
        Assert.Empty(ModelFamilyFormula.References("45\u00B0 + 10 mm + 3m", new[] { "A" }).Unknown);
    }

    [Fact]
    public void CycleIsReportedAsAPath()
    {
        var graph = ModelFamilyFormulaGraph.Build(new Dictionary<string, string?> { ["A"] = "B + 1", ["B"] = "C * 2", ["C"] = "A", ["D"] = null });
        var cycle = ModelFamilyFormulaGraph.FindCycle(graph);
        Assert.NotNull(cycle);
        Assert.Equal(cycle![0], cycle[^1]);
        Assert.Equal("A \u2192 B \u2192 C \u2192 A", ModelFamilyFormulaGraph.DescribeCycle(cycle));
    }

    [Fact]
    public void SelfReferenceIsACycleAndAChainIsNot()
    {
        Assert.NotNull(ModelFamilyFormulaGraph.FindCycle(ModelFamilyFormulaGraph.Build(new Dictionary<string, string?> { ["A"] = "A + 1" })));
        Assert.Null(ModelFamilyFormulaGraph.FindCycle(ModelFamilyFormulaGraph.Build(new Dictionary<string, string?> { ["A"] = "B", ["B"] = "C", ["C"] = null })));
    }

    [Fact]
    public void DependentsAreTransitive()
    {
        var graph = ModelFamilyFormulaGraph.Build(new Dictionary<string, string?> { ["A"] = null, ["B"] = "A * 2", ["C"] = "B + 1", ["D"] = null });
        var usedBy = ModelFamilyFormulaGraph.UsedBy(graph);
        Assert.Equal(new[] { "B", "C" }, ModelFamilyFormulaGraph.Dependents(usedBy, "A"));
        Assert.Empty(ModelFamilyFormulaGraph.Dependents(usedBy, "D"));
    }

    // ── Values ───────────────────────────────────────────────────────────────

    [Fact]
    public void ValuesConvertToInternalUnits()
    {
        var (length, _) = ModelFamilyParameterValues.ToInternal("length", new JValue(304.8));
        Assert.Equal(1.0, (double)length!.Internal!, 9);
        var (angle, _) = ModelFamilyParameterValues.ToInternal("angle", new JValue(180));
        Assert.Equal(Math.PI, (double)angle!.Internal!, 9);
        var (yesNo, _) = ModelFamilyParameterValues.ToInternal("yesNo", new JValue(true));
        Assert.Equal(1, yesNo!.Internal);
        var (voltage, _) = ModelFamilyParameterValues.ToInternal("electricalPotential", new JValue(230));
        Assert.True(voltage!.NeedsDocumentUnit);
    }

    [Fact]
    public void MaterialValuesAreNamesOrNone()
    {
        var (named, _) = ModelFamilyParameterValues.ToInternal("material", new JValue("Steel"));
        Assert.Equal("Steel", named!.MaterialName);
        var (none, _) = ModelFamilyParameterValues.ToInternal("material", JValue.CreateNull());
        Assert.True(none!.IsMaterial);
        Assert.Null(none.MaterialName);
        Assert.NotNull(ModelFamilyParameterValues.ToInternal("material", new JValue(true)).Error);
    }

    [Fact]
    public void BadValuesNameTheExpectedForm()
    {
        var (_, error) = ModelFamilyParameterValues.ToInternal("length", new JValue("wide"));
        Assert.Contains("number", error);
        Assert.NotNull(ModelFamilyParameterValues.ToInternal("yesNo", new JValue("maybe")).Error);
        Assert.NotNull(ModelFamilyParameterValues.ToInternal("integer", new JValue(1.5)).Error);
        Assert.NotNull(ModelFamilyParameterValues.ToInternal(null, new JValue(1)).Error);
        Assert.Contains("millimetres", ModelFamilyParameterValues.ExpectedForm("length"));
    }

    [Fact]
    public void WireNumbersRoundTrip()
    {
        Assert.Equal(100.0, ModelFamilyParameterValues.ToWireNumber("length", 100 / 304.8), 4);
        Assert.Equal(45.0, ModelFamilyParameterValues.ToWireNumber("angle", Math.PI / 4), 4);
        Assert.Equal(2.0, ModelFamilyParameterValues.ToWireNumber("area", 2 * 10.763910416709722), 4);
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParserReadsEveryList()
    {
        var spec = Spec(new
        {
            removeParameters = new[] { "Old" },
            parameters = new object[]
            {
                new { name = "Width", dataType = "length", instance = true, value = 300 },
                new { name = "Code", shared = new { guid = "5f9d3a3c-0f5e-4f0e-8d7e-1d2a1c3b4a5f" } }
            },
            types = new object[] { new { name = "A", values = new { Width = 100 }, makeCurrent = true } },
            formulas = new object[] { new { parameter = "Area", formula = "Width * 2" } },
            associations = new object[] { new { elements = new[] { 10, 11 }, parameter = "IS_VISIBLE_PARAM", familyParameter = "Show" } },
            sharedParameterFile = "C:\\x\\shared.txt"
        }, out var problems);
        Assert.Empty(problems);
        Assert.Equal("Old", Assert.Single(spec.RemoveParameters));
        Assert.Equal(2, spec.Parameters.Count);
        Assert.True(spec.Parameters[1].Shared);
        Assert.Equal("5f9d3a3c-0f5e-4f0e-8d7e-1d2a1c3b4a5f", spec.Parameters[1].Guid);
        Assert.Equal("Width", spec.Types[0].Values[0].Key);
        Assert.True(spec.Types[0].MakeCurrent);
        Assert.Equal("Width * 2", spec.Formulas[0].Formula);
        Assert.Equal(2, spec.Associations.Count);
        Assert.Equal("Show", spec.Associations[0].FamilyParameter);
        Assert.Equal("C:\\x\\shared.txt", spec.SharedParameterFile);
    }

    [Fact]
    public void ParserKeepsNullFormulaAndNullFamilyParameter()
    {
        var args = new Dictionary<string, object?>
        {
            ["parameters"] = JArray.Parse("[{\"name\":\"A\",\"formula\":null}]"),
            ["associations"] = JArray.Parse("[{\"element\":5,\"parameter\":\"MATERIAL_ID_PARAM\",\"familyParameter\":null}]")
        };
        var problems = new List<string>();
        var spec = ModelFamilyParameterSpecParser.Parse(args, problems);
        Assert.Empty(problems);
        Assert.True(spec.Parameters[0].FormulaGiven);
        Assert.Null(spec.Parameters[0].Formula);
        Assert.True(spec.Associations[0].Dissociate);
    }

    [Fact]
    public void ParserReportsBadInputWithSources()
    {
        Spec(new
        {
            parameters = new object[] { new { name = "A", dataType = "lenth", bogus = 1 }, new { dataType = "text" }, new { name = "B", formula = "A", value = 1 } },
            types = new object[] { new { rename = "X" }, new { name = "T", values = 5 } },
            formulas = new object[] { new { parameter = "A" } },
            associations = new object[] { new { parameter = "X", familyParameter = "Y" } }
        }, out var problems);
        Assert.Contains(problems, p => p.StartsWith("parameters[0].bogus: unknown key"));
        Assert.Contains(problems, p => p.StartsWith("parameters[0].dataType") && p.Contains("length"));
        Assert.Contains(problems, p => p.StartsWith("parameters[1].name is required"));
        Assert.Contains(problems, p => p.StartsWith("parameters[2]") && p.Contains("value or a formula"));
        Assert.Contains(problems, p => p.StartsWith("types[0].name is required"));
        Assert.Contains(problems, p => p.StartsWith("types[1].values"));
        Assert.Contains(problems, p => p.StartsWith("formulas[0].formula is required"));
        Assert.Contains(problems, p => p.StartsWith("associations[0]") && p.Contains("element"));
    }

    [Fact]
    public void ParserRejectsADuplicateAndABadGuid()
    {
        Spec(new
        {
            parameters = new object[] { new { name = "A", dataType = "text" }, new { name = "a", dataType = "text" }, new { name = "G", guid = "not-a-guid" } }
        }, out var problems);
        Assert.Contains(problems, p => p.Contains("'a' is listed 2 times") || p.Contains("is listed 2 times"));
        Assert.Contains(problems, p => p.Contains("not a GUID"));
    }

    // ── Planner: removal and impact ──────────────────────────────────────────

    [Fact]
    public void RemovalIsRefusedWhileAFormulaOfARemainingParameterUsesIt()
    {
        var context = Family(P("Width"), P("Area", formula: "Width * 2"));
        var plan = Plan(context, new { removeParameters = new[] { "Width" } });
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("'Width' cannot be removed", problem);
        Assert.Contains("'Area'", problem);
    }

    [Fact]
    public void RemovalPassesWhenTheFormulaIsClearedOrTheUserIsRemovedToo()
    {
        var context = Family(P("Width"), P("Area", formula: "Width * 2"));
        Assert.Empty(Plan(context, new { removeParameters = new[] { "Width" }, parameters = new object[] { new { name = "Area", formula = (string?)null } } }).Problems);
        Assert.Empty(Plan(context, new { removeParameters = new[] { "Width", "Area" } }).Problems);
    }

    [Fact]
    public void RemoveAndReAddIsRefusedWhileAnUnchangedFormulaUsesIt()
    {
        // Revit deletes every formula that uses a removed parameter; re-adding the name does not bring the formula back.
        var context = Family(P("Width"), P("Area", formula: "Width * 2"));
        var plan = Plan(context, new { removeParameters = new[] { "Width" }, parameters = new object[] { new { name = "Width", dataType = "length" } } });
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("'Area'", problem);
        Assert.Contains("Width * 2", problem);

        // Setting the formula again in the same call (it runs after the new parameter exists) is fine.
        Assert.Empty(Plan(context, new
        {
            removeParameters = new[] { "Width" },
            parameters = new object[] { new { name = "Width", dataType = "length" } },
            formulas = new object[] { new { parameter = "Area", formula = "Width * 2" } }
        }).Problems);
    }

    [Fact]
    public void RemovalReportsLabelsAndAssociationsAsWarnings()
    {
        var context = Family(P("Width"));
        context.LabelsByParameter["Width"] = new List<long> { 77 };
        context.AssociatedByParameter["Width"] = new List<ModelFamilyAssociatedRef> { new() { ElementId = 90, Element = "Extrusion 90", Parameter = "Depth" } };
        var plan = Plan(context, new { removeParameters = new[] { "Width" } });
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Warnings, w => w.Contains("dimension 77"));
        Assert.Contains(plan.Warnings, w => w.Contains("1 association") && w.Contains("90 Depth"));
        var row = (JObject)plan.Impact["removed"]![0]!;
        Assert.Equal("Width", (string?)row["parameter"]);
        Assert.Equal(77, (long)row["dimensionLabels"]![0]!);
    }

    [Fact]
    public void ADissociationInTheSameCallReleasesTheAssociationBeforeRemoval()
    {
        var context = Family(P("Width"));
        context.AssociatedByParameter["Width"] = new List<ModelFamilyAssociatedRef> { new() { ElementId = 90, Parameter = "Depth" } };
        var plan = Plan(context, new
        {
            removeParameters = new[] { "Width" },
            associations = new object[] { new { element = 90, parameter = "Depth", familyParameter = (string?)null } }
        });
        Assert.Empty(plan.Problems);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("association"));
    }

    [Fact]
    public void RemovingAnUnknownOrBuiltInParameterIsAProblem()
    {
        var context = Family(P("Width"), P("Default Elevation", builtIn: true));
        var plan = Plan(context, new { removeParameters = new[] { "Widht", "Default Elevation" } });
        Assert.Contains(plan.Problems, p => p.Contains("did you mean 'Width'"));
        Assert.Contains(plan.Problems, p => p.Contains("built-in"));
    }

    [Fact]
    public void ATooltipOnABuiltInParameterIsAProblemBeforeRevitIsAsked()
    {
        // FamilyManager.SetDescription throws for a built-in parameter.
        var plan = Plan(Family(P("Default Elevation", builtIn: true)), new { parameters = new object[] { new { name = "Default Elevation", tooltip = "Height" } } });
        Assert.Contains(plan.Problems, p => p.Contains("built-in") && p.Contains("tooltip"));
    }

    [Fact]
    public void ChangingAParameterListsItsDependents()
    {
        var context = Family(P("Width"), P("Area", formula: "Width * 2"), P("Volume", formula: "Area * 3"));
        var plan = Plan(context, new { parameters = new object[] { new { name = "Width", instance = true } } });
        Assert.Contains(plan.Problems, p => p.Contains("type parameter") || p.Contains("instance parameter"));
        var changed = (JObject)plan.Impact["changed"]![0]!;
        Assert.Equal(new[] { "Area", "Volume" }, changed["usedByFormulas"]!.Select(t => (string?)t));
    }

    // ── Planner: formulas ────────────────────────────────────────────────────

    [Fact]
    public void FormulaCyclesAreReportedAfterTheEdit()
    {
        var context = Family(P("A"), P("B", formula: "A + 1"));
        var plan = Plan(context, new { formulas = new object[] { new { parameter = "A", formula = "B * 2" } } });
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("circular reference", problem);
        Assert.Contains("A \u2192 B \u2192 A", problem);
    }

    [Fact]
    public void ACycleThroughANewParameterIsFound()
    {
        var context = Family(P("A"));
        var plan = Plan(context, new
        {
            parameters = new object[]
            {
                new { name = "B", dataType = "length", formula = "C" },
                new { name = "C", dataType = "length", formula = "B" }
            }
        });
        Assert.Contains(plan.Problems, p => p.Contains("circular reference"));
    }

    [Fact]
    public void UnknownIdentifiersInAFormulaAreOnlyWarnings()
    {
        var context = Family(P("A"));
        var plan = Plan(context, new { formulas = new object[] { new { parameter = "A", formula = "Nothing * 2" } } });
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Warnings, w => w.Contains("'Nothing'") && w.Contains("Revit decides"));
    }

    [Fact]
    public void ATypeParameterFormulaCannotUseAnInstanceParameter()
    {
        var context = Family(P("Inst", instance: true), P("Typ"));
        var plan = Plan(context, new { formulas = new object[] { new { parameter = "Typ", formula = "Inst * 2" } } });
        Assert.Contains(plan.Problems, p => p.Contains("type parameter") && p.Contains("instance parameter 'Inst'"));
        // The other direction is fine.
        var ok = Plan(Family(P("Inst", instance: true), P("Typ")), new { formulas = new object[] { new { parameter = "Inst", formula = "Typ * 2" } } });
        Assert.Empty(ok.Problems);
    }

    [Fact]
    public void AReportingParameterMustBeAnInstanceParameter()
    {
        var plan = Plan(Family(), new { parameters = new object[] { new { name = "Measured", dataType = "length", reporting = true } } });
        Assert.Contains(plan.Problems, p => p.Contains("reporting") && p.Contains("instance"));
        Assert.Empty(Plan(Family(), new { parameters = new object[] { new { name = "Measured", dataType = "length", reporting = true, instance = true } } }).Problems);
    }

    [Fact]
    public void LaterFormulaWinsAndFormulasFollowARename()
    {
        var context = Family(P("Width"), P("Area", formula: "Width * 2"));
        var plan = Plan(context, new
        {
            parameters = new object[] { new { name = "Width", rename = "Breadth" }, new { name = "Area", formula = "Breadth * 3" } },
            formulas = new object[] { new { parameter = "Area", formula = "Breadth * 4" } }
        });
        Assert.Empty(plan.Problems);
        Assert.Equal("Breadth * 4", plan.Final.Single(p => p.Name == "Area").Formula);
        Assert.Contains(plan.Final, p => p.Name == "Breadth");
    }

    // ── Planner: parameters ──────────────────────────────────────────────────

    [Fact]
    public void ANewParameterNeedsADataType()
    {
        var plan = Plan(Family(), new { parameters = new object[] { new { name = "Width" } } });
        Assert.Contains(plan.Problems, p => p.Contains("needs a dataType"));
    }

    [Fact]
    public void TheDataTypeOfAnExistingParameterCannotChange()
    {
        var plan = Plan(Family(P("Width")), new { parameters = new object[] { new { name = "Width", dataType = "text" } } });
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("cannot be changed", problem);
        Assert.Contains("Remove it", problem);
    }

    [Fact]
    public void ASameDataTypeIsNotAProblemAndRemoveThenReAddIsFine()
    {
        Assert.Empty(Plan(Family(P("Width")), new { parameters = new object[] { new { name = "Width", dataType = "length", instance = true } } }).Problems);
        var plan = Plan(Family(P("Width")), new { removeParameters = new[] { "Width" }, parameters = new object[] { new { name = "Width", dataType = "text" } } });
        Assert.Empty(plan.Problems);
        Assert.Equal("create", plan.Parameters[0].Action);
    }

    [Fact]
    public void RenameRules()
    {
        var context = Family(P("Width"), P("Depth"), P("Code", shared: true, guid: "11111111-1111-1111-1111-111111111111"));
        var plan = Plan(context, new
        {
            parameters = new object[]
            {
                new { name = "Width", rename = "Depth" },
                new { name = "Code", rename = "Other" },
                new { name = "Missing", dataType = "text", rename = "X" }
            }
        });
        Assert.Contains(plan.Problems, p => p.Contains("already has a parameter 'Depth'"));
        Assert.Contains(plan.Problems, p => p.Contains("shared parameter") && p.Contains("name belongs"));
        Assert.Contains(plan.Problems, p => p.Contains("nothing to rename"));
    }

    // ── Planner: shared parameters ───────────────────────────────────────────

    private static ModelFamilySharedDefinition Def(string name, string guid, string group = "EULE", string type = "text") =>
        new() { Name = name, Guid = guid, Group = group, DataTypeId = "autodesk.spec:" + type + "-2.0.0", Friendly = type };

    private const string G1 = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string G2 = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public void ASharedParameterIsResolvedByGuidAndKeepsTheDefinitionsDataType()
    {
        var context = Family();
        context.SharedDefinitions = new List<ModelFamilySharedDefinition> { Def("Device Code", G1), Def("Device Code", G2, "Other") };
        var plan = Plan(context, new { parameters = new object[] { new { guid = G2, instance = true } } });
        Assert.Empty(plan.Problems);
        var item = Assert.Single(plan.Parameters);
        Assert.Equal(G2, item.Definition!.Guid);
        Assert.Equal("Device Code", item.FinalName);
        Assert.Equal("text", item.Friendly);
        Assert.True(plan.Final.Single().Shared);
    }

    [Fact]
    public void ASharedNameWithTwoGuidsIsAmbiguous()
    {
        var context = Family();
        context.SharedDefinitions = new List<ModelFamilySharedDefinition> { Def("Device Code", G1), Def("Device Code", G2, "Other") };
        var plan = Plan(context, new { parameters = new object[] { new { name = "Device Code", shared = true } } });
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("2 definitions named 'Device Code' with different GUIDs", problem);
        Assert.Contains(G1, problem);
        Assert.Contains("pass guid", problem);
    }

    [Fact]
    public void ASharedDefinitionThatIsMissingOrMisnamedIsAProblem()
    {
        var context = Family();
        context.SharedDefinitions = new List<ModelFamilySharedDefinition> { Def("Device Code", G1) };
        var plan = Plan(context, new
        {
            parameters = new object[]
            {
                new { name = "Device Cod", shared = true },
                new { guid = G2 },
                new { name = "Other Name", guid = G1 }
            }
        });
        Assert.Contains(plan.Problems, p => p.Contains("no shared parameter 'Device Cod'") && p.Contains("did you mean 'Device Code'"));
        Assert.Contains(plan.Problems, p => p.Contains($"no definition with GUID {G2}"));
        Assert.Contains(plan.Problems, p => p.Contains("is 'Device Code'") && p.Contains("not 'Other Name'"));
    }

    [Fact]
    public void ARequestedDataTypeMustMatchTheDefinition()
    {
        var context = Family();
        context.SharedDefinitions = new List<ModelFamilySharedDefinition> { Def("Device Code", G1) };
        var plan = Plan(context, new { parameters = new object[] { new { guid = G1, dataType = "length" } } });
        Assert.Contains(plan.Problems, p => p.Contains("differs from the data type of the shared definition"));
        Assert.Empty(Plan(context, new { parameters = new object[] { new { guid = G1, dataType = "text" } } }).Problems);
    }

    [Fact]
    public void SameNameDifferentGuidInTheFamilyIsAConflict()
    {
        var context = Family(P("Device Code", "text", shared: true, guid: G1));
        context.SharedDefinitions = new List<ModelFamilySharedDefinition> { Def("Device Code", G2) };
        var byGuid = Plan(context, new { parameters = new object[] { new { name = "Device Code", guid = G2 } } });
        Assert.Contains(byGuid.Problems, p => p.Contains("Same name, different GUID"));
        var byName = Plan(context, new { parameters = new object[] { new { name = "Device Code", shared = true } } });
        Assert.Contains(byName.Problems, p => p.Contains("Same name, different GUID"));
        Assert.Empty(Plan(Family(P("Device Code", "text", shared: true, guid: G2)) .WithDefinitions(Def("Device Code", G2)),
            new { parameters = new object[] { new { name = "Device Code", shared = true } } }).Problems);
    }

    [Fact]
    public void AFamilyParameterIsReplacedByASharedOneOnlyWithTheSameDataType()
    {
        var context = Family(P("Device Code", "text"), P("Width"));
        context.SharedDefinitions = new List<ModelFamilySharedDefinition> { Def("Device Code", G1), Def("Width", G2, type: "text") };
        var plan = Plan(context, new { parameters = new object[] { new { name = "Device Code", shared = true }, new { name = "Width", shared = true } } });
        Assert.Equal("replaceWithShared", plan.Parameters[0].Action);
        Assert.Contains("replacedWithShared", plan.Parameters[0].Changes);
        Assert.Contains(plan.Problems, p => p.Contains("'Width' is length") && p.Contains("data types match"));
    }

    [Fact]
    public void ASharedParameterCannotBeTurnedIntoAFamilyParameter()
    {
        var plan = Plan(Family(P("Code", "text", shared: true, guid: G1)), new { parameters = new object[] { new { name = "Code", shared = false } } });
        Assert.Contains(plan.Problems, p => p.Contains("cannot be turned into a family parameter"));
    }

    [Fact]
    public void AddingAnExistingFamilyParameterAsSharedByGuidAndAnExistingNonSharedNameClash()
    {
        var context = Family(P("Device Code", "text"));
        context.SharedDefinitions = new List<ModelFamilySharedDefinition> { Def("Device Code", G1) };
        var plan = Plan(context, new { removeParameters = new[] { "Nothing" }, parameters = new object[] { new { guid = G1 } } });
        // The guid-only entry resolves to the definition's name; the family has a non-shared parameter of that name.
        Assert.Contains(plan.Problems, p => p.Contains("that is not shared") || p.Contains("replace"));
    }

    // ── Planner: types and values ────────────────────────────────────────────

    [Fact]
    public void TypeValuesAreCheckedAgainstTheParameters()
    {
        var context = Family(P("Width"), P("Area", formula: "Width * 2"), P("Show", "yesNo"), P("Body", "material"));
        context.MaterialNames = new List<string> { "Steel", "Steel Dark" };
        var plan = Plan(context, new
        {
            types = new object[]
            {
                new { name = "A", values = new Dictionary<string, object> { ["Width"] = "wide", ["Area"] = 5, ["Show"] = "maybe", ["Nope"] = 1, ["Body"] = "Stee" } }
            }
        });
        Assert.Contains(plan.Problems, p => p.Contains("types[0].values.Width") && p.Contains("millimetres"));
        Assert.Contains(plan.Problems, p => p.Contains("'Area' is driven by the formula"));
        Assert.Contains(plan.Problems, p => p.Contains("types[0].values.Show") && p.Contains("true or false"));
        Assert.Contains(plan.Problems, p => p.Contains("no parameter 'Nope'"));
        Assert.Contains(plan.Problems, p => p.Contains("no material 'Stee'") && p.Contains("'Steel'"));
    }

    [Fact]
    public void ValidTypeValuesPass()
    {
        var context = Family(P("Width"), P("Show", "yesNo"), P("Body", "material"));
        context.MaterialNames = new List<string> { "Steel" };
        var plan = Plan(context, new
        {
            types = new object[]
            {
                new { name = "Type 1", values = new Dictionary<string, object> { ["Width"] = 100, ["Show"] = true, ["Body"] = "steel" } },
                new { name = "Type 2", values = new Dictionary<string, object> { ["Width"] = 200, ["Show"] = false } }
            }
        });
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void ClearingAFormulaAllowsAValueInTheSameCall()
    {
        var context = Family(P("Width"), P("Area", formula: "Width * 2"));
        var plan = Plan(context, new
        {
            parameters = new object[] { new { name = "Area", formula = (string?)null } },
            types = new object[] { new { name = "Type 1", values = new { Area = 4 } } }
        });
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void TypeRules()
    {
        var context = Family(P("Width"));
        var deleteLast = Plan(context, new { types = new object[] { new { name = "Type 1", delete = true } } });
        Assert.Contains(deleteLast.Problems, p => p.Contains("last type"));

        var ok = Plan(context, new { types = new object[] { new { name = "Type 2" }, new { name = "Type 1", delete = true } } });
        Assert.Empty(ok.Problems);

        var missing = Plan(context, new { types = new object[] { new { name = "Typ 1", delete = true }, new { name = "Nope", rename = "X" } } });
        Assert.Contains(missing.Problems, p => p.Contains("no type 'Typ 1'") && p.Contains("Type 1"));
        Assert.Contains(missing.Problems, p => p.Contains("no type 'Nope' to rename"));

        var twoCurrent = Plan(context, new { types = new object[] { new { name = "A", makeCurrent = true }, new { name = "B", makeCurrent = true } } });
        Assert.Contains(twoCurrent.Problems, p => p.Contains("only one can be current"));

        var clash = Plan(context, new { types = new object[] { new { name = "A" }, new { name = "Type 1", rename = "A" } } });
        Assert.Contains(clash.Problems, p => p.Contains("already has a type 'A'"));
    }

    [Fact]
    public void ACurrentTypeValueIsValidatedLikeATypeValue()
    {
        var plan = Plan(Family(P("Width")), new { parameters = new object[] { new { name = "Width", value = "wide" } } });
        Assert.Contains(plan.Problems, p => p.Contains("parameters[0].value"));
    }

    // ── Planner: associations ────────────────────────────────────────────────

    [Fact]
    public void AssociationsNeedAFamilyParameter()
    {
        var plan = Plan(Family(P("Show", "yesNo")), new
        {
            associations = new object[]
            {
                new { element = 5, parameter = "IS_VISIBLE_PARAM", familyParameter = "Sho" },
                new { element = 5, parameter = "IS_VISIBLE_PARAM", familyParameter = "Show" },
                new { element = 5, parameter = "IS_VISIBLE_PARAM", familyParameter = (string?)null }
            }
        });
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("did you mean 'Show'", problem);
    }

    [Fact]
    public void AnAssociationMayUseAParameterCreatedInTheSameCall()
    {
        var plan = Plan(Family(), new
        {
            parameters = new object[] { new { name = "Show", dataType = "yesNo", instance = true } },
            associations = new object[] { new { element = 5, parameter = "IS_VISIBLE_PARAM", familyParameter = "Show" } }
        });
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void AnEmptyEditIsEmpty()
    {
        Assert.True(Spec(new { }, out _).IsEmpty);
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    private static readonly string[] Tools = { "revit_preview_edit_model_family_parameters", "revit_edit_model_family_parameters" };

    [Fact]
    public void ToolsAreInTheFamilyEditorGroup()
    {
        foreach (var tool in Tools) Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));
    }

    [Theory]
    [InlineData("revit_preview_edit_model_family_parameters", "ReadOnly")]
    [InlineData("revit_edit_model_family_parameters", "RequiresApproval")]
    public void PermissionsMatchTheBridgeReadOnlyFlag(string tool, string permission)
    {
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void TheEditIsClassifiedAsCreatingForPlans()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_edit_model_family_parameters"));
    }

    [Fact]
    public void ThePreviewAndTheEditSendTheSameArguments()
    {
        var preview = ToolSourceScan.Bridge.Value.Single(b => b.Name == "revit_preview_edit_model_family_parameters");
        var edit = ToolSourceScan.Bridge.Value.Single(b => b.Name == "revit_edit_model_family_parameters");
        foreach (var key in new[] { "target", "removeParameters", "parameters", "types", "formulas", "associations", "sharedParameterFile", "document" })
        {
            Assert.Contains($"{key} ", preview.Source.Replace("\n", " "));
            Assert.Contains($"{key} ", edit.Source.Replace("\n", " "));
        }
    }

    [Fact]
    public void TheEditToolHasAnApprovalSummaryAndRunsTheSharedBody()
    {
        var edit = ToolSourceScan.Addin.Value["revit_edit_model_family_parameters"];
        Assert.Contains("IApprovalSummaryTool", edit.Source);
        Assert.Contains("ModelFamilySession.Edit(uiapp, request, apply: true", edit.Source);
        var preview = ToolSourceScan.Addin.Value["revit_preview_edit_model_family_parameters"];
        Assert.Contains("ModelFamilySession.Edit(uiapp, request, apply: false", preview.Source);
        Assert.Equal(edit.Source.Contains("ModelFamilyParameterExecutor.Run"), preview.Source.Contains("ModelFamilyParameterExecutor.Run"));
    }

    [Fact]
    public void TheFeatureRegistersItsInspectionSections()
    {
        var source = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin", "Tools", "ModelFamilies", "ModelFamilyParameterTools.cs"));
        foreach (var section in new[] { "\"parameters\"", "\"types\"", "\"associableParameters\"" })
            Assert.Contains($"ModelFamilyInspect.AddSection({section}", source.Replace("\r\n", "\n").Replace(",\n            ", ", "));
    }

    [Fact]
    public void NoRevitApiIsUsedInThePureFolder()
    {
        var folder = Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin", "ModelFamilies");
        foreach (var file in Directory.GetFiles(folder, "ModelFamilyParameter*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Autodesk.Revit", text);
            Assert.DoesNotContain("RevitMCP.Addin.Tools", text);
        }
    }
}

internal static class ModelFamilyParameterTestExtensions
{
    public static ModelFamilyParameterContext WithDefinitions(this ModelFamilyParameterContext context, params ModelFamilySharedDefinition[] definitions)
    {
        context.SharedDefinitions = definitions.ToList();
        return context;
    }
}
