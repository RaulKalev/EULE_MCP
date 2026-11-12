using System.IO;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Parameters;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>A parameter of the spec, resolved against the running Revit and the shared parameter file.</summary>
internal sealed class ResolvedFamilyParameter
{
    public FamilyParameterSpec Spec { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    /// <summary>The data type's forge id.</summary>
    public string SpecTypeId { get; set; } = string.Empty;
    public string? Friendly { get; set; }
    public string DataTypeLabel { get; set; } = string.Empty;
    /// <summary>The GroupTypeId member name or forge id; "" = Other.</summary>
    public string GroupKey { get; set; } = string.Empty;
    public string GroupLabel { get; set; } = string.Empty;
    public SharedDefinitionInfo? Shared { get; set; }
    /// <summary>The value in internal units (string, int or double), or null.</summary>
    public object? Value { get; set; }
}

/// <summary>What the parameters of a spec resolved to, and the shared parameter file they came from.</summary>
internal sealed class ResolvedFamilyParameters
{
    public List<ResolvedFamilyParameter> Items { get; } = new();
    public string? SharedFilePath { get; set; }
    public string? SharedFileSource { get; set; }
    public bool AnyShared => Items.Any(i => i.Shared != null);
}

/// <summary>A project text type the labels copy into their annotation families.</summary>
internal sealed class ProjectTextTypeInfo
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<BuiltInParameter, object> Values { get; } = new();
    public double? SizeMm { get; set; }
    public string Font { get; set; } = string.Empty;
}

/// <summary>
/// Phases 2 and 3 of revit_create_detail_family (#147): family and shared parameters, and text through nested Generic
/// Annotation families. Shared parameters are only ever read from the shared parameter file, never created in it.
/// </summary>
internal static class DetailFamilyExtrasRevit
{
    private const double MmPerFoot = 304.8;

    /// <summary>Text type parameters copied from the project's type into the annotation family's.</summary>
    private static readonly BuiltInParameter[] TextTypeParameters =
    {
        BuiltInParameter.TEXT_SIZE,
        BuiltInParameter.TEXT_FONT,
        BuiltInParameter.TEXT_WIDTH_SCALE,
        BuiltInParameter.TEXT_STYLE_BOLD,
        BuiltInParameter.TEXT_STYLE_ITALIC,
        BuiltInParameter.TEXT_STYLE_UNDERLINE,
        BuiltInParameter.TEXT_BACKGROUND,
        BuiltInParameter.TEXT_TAB_SIZE,
        BuiltInParameter.LINE_COLOR,
        BuiltInParameter.LINE_PEN
    };

    // ── Parameters (phase 2) ─────────────────────────────────────────────────

    public static ResolvedFamilyParameters ResolveParameters(RevitApplication app, Document doc, DetailFamilySpec spec,
        List<string> problems, List<string> warnings)
    {
        var result = new ResolvedFamilyParameters();
        if (spec.Parameters.Count == 0) return result;

        List<SharedDefinitionInfo>? definitions = null;
        var fileLabel = "the shared parameter file";
        if (spec.Parameters.Any(p => p.Shared))
        {
            var choice = SharedParameterFileReader.Choose(app, doc, spec.SharedParameterFile);
            result.SharedFileSource = choice.Source;
            if (choice.Error != null)
                problems.Add(choice.Error);
            else
            {
                result.SharedFilePath = choice.Path;
                fileLabel = $"'{choice.Path}'";
                if (!File.Exists(choice.Path))
                    problems.Add($"The shared parameter file {fileLabel} ({choice.Source}) does not exist. This tool never creates one.");
                else
                {
                    definitions = SharedParameterFileReader.WithFile(app, choice.Path!,
                        file => file == null ? null : SharedParameterFileReader.ReadDefinitions(file).Select(r => r.Info).ToList(), warnings);
                    if (definitions == null) problems.Add($"Revit could not read the shared parameter file {fileLabel}.");
                }
            }
        }

        foreach (var parameter in spec.Parameters)
        {
            var item = new ResolvedFamilyParameter { Spec = parameter, Name = parameter.Name };
            string? requestedSpec = null;
            if (parameter.DataType.Length > 0)
            {
                requestedSpec = ParameterRevitTypes.ResolveSpec(parameter.DataType);
                if (requestedSpec == null)
                {
                    problems.Add($"{parameter.Source}: Revit does not know the data type '{parameter.DataType}'.");
                    continue;
                }
            }

            if (parameter.Shared)
            {
                if (definitions == null) continue; // the file problem is already reported
                var (definition, error) = DetailFamilySpecParser.MatchSharedDefinition(parameter, definitions, fileLabel);
                if (error != null) { problems.Add(error); continue; }
                item.Shared = definition;
                item.Name = definition!.Name;
                item.SpecTypeId = definition.DataTypeId;
                if (requestedSpec != null && !ParameterSpecMap.SameType(requestedSpec, definition.DataTypeId))
                {
                    problems.Add($"{parameter.Source}: the shared parameter '{definition.Name}' is " +
                                 $"{ParameterRevitTypes.SpecLabel(ParameterRevitTypes.SpecOf(definition.DataTypeId))}, not '{parameter.DataType}'.");
                    continue;
                }
            }
            else
            {
                item.SpecTypeId = requestedSpec!;
            }

            var specId = ParameterRevitTypes.SpecOf(item.SpecTypeId);
            item.Friendly = parameter.Spec?.Friendly ?? ParameterRevitTypes.FriendlyOf(specId);
            item.DataTypeLabel = ParameterRevitTypes.SpecLabel(specId);

            if (parameter.Group.Length > 0 && parameter.GroupKey == null)
            {
                var group = ParameterRevitTypes.ResolveGroup(parameter.Group);
                if (group == null)
                {
                    problems.Add($"{parameter.Source}: '{parameter.Group}' is not a parameter group (for example Data, Text, Dimensions, Identity Data, Graphics, Electrical).");
                    continue;
                }
                item.GroupKey = group;
            }
            else
            {
                item.GroupKey = parameter.Group.Length > 0 ? parameter.GroupKey! : DetailFamilySpecParser.DefaultGroupKey(item.Friendly);
            }
            item.GroupLabel = ParameterRevitTypes.GroupLabel(ParameterRevitTypes.GroupOf(item.GroupKey));

            if (parameter.Value != null)
            {
                if (item.Friendly == null)
                {
                    problems.Add($"{parameter.Source} ('{item.Name}'): values of type {item.DataTypeLabel} cannot be set here; leave the value out, or give a formula.");
                    continue;
                }
                var (value, valueError) = DetailFamilySpecParser.ConvertValue(item.Friendly, parameter.Value);
                if (valueError != null) { problems.Add($"{parameter.Source} ('{item.Name}'): {valueError}"); continue; }
                item.Value = value;
            }
            result.Items.Add(item);
        }

        var duplicates = result.Items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var name in duplicates)
            problems.Add($"The parameter '{name}' is requested more than once.");
        return result;
    }

    public static object DescribePlan(ResolvedFamilyParameter item) => new
    {
        name = item.Name,
        shared = item.Shared != null,
        guid = item.Shared?.Guid,
        sharedGroup = item.Shared?.Group,
        dataType = item.DataTypeLabel,
        group = item.GroupLabel,
        instance = item.Spec.IsInstance,
        value = item.Spec.Value?.ToString(Newtonsoft.Json.Formatting.None),
        formula = item.Spec.Formula.Length > 0 ? item.Spec.Formula : null
    };

    /// <summary>
    /// Adds the parameters to the family and sets the values and formulas of its current type (a type named after the
    /// family is created when the template has none). Runs inside the caller's transaction. The shared parameter file
    /// is opened for the shared ones and Revit's own setting restored afterwards.
    /// </summary>
    public static List<object> AddParameters(RevitApplication app, Document familyDoc, DetailFamilySpec spec,
        ResolvedFamilyParameters resolved, HashSet<ElementId> protectedIds, List<string> warnings)
    {
        if (resolved.Items.Count == 0) return new List<object>();
        if (!resolved.AnyShared)
            return Add(familyDoc, spec, resolved, null, protectedIds, warnings);
        return SharedParameterFileReader.WithFile(app, resolved.SharedFilePath!,
            file => Add(familyDoc, spec, resolved, file, protectedIds, warnings), warnings);
    }

    private static List<object> Add(Document familyDoc, DetailFamilySpec spec, ResolvedFamilyParameters resolved,
        DefinitionFile? file, HashSet<ElementId> protectedIds, List<string> warnings)
    {
        var results = new List<object>();
        var manager = familyDoc.FamilyManager;
        var needsType = resolved.Items.Any(i => i.Value != null || i.Spec.Formula.Length > 0);
        if (needsType && manager.CurrentType == null)
        {
            manager.CurrentType = manager.Types.Size > 0
                ? manager.Types.Cast<FamilyType>().First()
                : manager.NewType(spec.FamilyName);
        }
        if (needsType && manager.Types.Size > 1)
            warnings.Add($"The family has {manager.Types.Size} types; values were set on '{manager.CurrentType?.Name}' only.");

        foreach (var item in resolved.Items)
        {
            string status;
            string? reason = null;
            try
            {
                var group = ParameterRevitTypes.GroupOf(item.GroupKey) ?? new ForgeTypeId();
                var parameter = manager.get_Parameter(item.Name);
                if (parameter != null)
                {
                    status = "existing";
                    reason = "The template already has a parameter of this name; it was kept and only its value or formula set.";
                }
                else if (item.Shared != null)
                {
                    var definition = file == null ? null : SharedParameterFileReader.Find(file, item.Shared);
                    if (definition == null)
                    {
                        results.Add(new { name = item.Name, status = "failed", reason = "The shared parameter definition is no longer in the file." });
                        continue;
                    }
                    parameter = manager.AddParameter(definition, group, item.Spec.IsInstance);
                    status = "added";
                }
                else
                {
                    parameter = manager.AddParameter(item.Name, group, new ForgeTypeId(item.SpecTypeId), item.Spec.IsInstance);
                    status = "added";
                }
                protectedIds.Add(parameter.Id);

                if (item.Spec.Formula.Length > 0)
                    manager.SetFormula(parameter, item.Spec.Formula);
                else if (item.Value != null)
                {
                    switch (item.Value)
                    {
                        case string text: manager.Set(parameter, text); break;
                        case int number: manager.Set(parameter, number); break;
                        case double number: manager.Set(parameter, number); break;
                    }
                }
                results.Add(new
                {
                    name = item.Name,
                    status,
                    reason,
                    shared = item.Shared != null,
                    guid = item.Shared?.Guid,
                    dataType = item.DataTypeLabel,
                    group = item.GroupLabel,
                    instance = parameter.IsInstance,
                    formula = item.Spec.Formula.Length > 0 ? item.Spec.Formula : null,
                    value = item.Spec.Value?.ToString(Newtonsoft.Json.Formatting.None)
                });
            }
            catch (Exception ex)
            {
                results.Add(new { name = item.Name, status = "failed", reason = ex.Message });
                warnings.Add($"Parameter '{item.Name}' failed: {ex.Message}");
            }
        }
        return results;
    }

    // ── Labels (phase 3) ─────────────────────────────────────────────────────

    public static Dictionary<string, ProjectTextTypeInfo> ReadProjectTextTypes(Document doc)
    {
        var result = new Dictionary<string, ProjectTextTypeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>())
        {
            var info = new ProjectTextTypeInfo { Name = type.Name };
            foreach (var bip in TextTypeParameters)
            {
                var parameter = type.get_Parameter(bip);
                if (parameter == null || !parameter.HasValue) continue;
                object? value = parameter.StorageType switch
                {
                    StorageType.Double => parameter.AsDouble(),
                    StorageType.Integer => parameter.AsInteger(),
                    StorageType.String => parameter.AsString(),
                    _ => null
                };
                if (value != null) info.Values[bip] = value;
            }
            if (info.Values.TryGetValue(BuiltInParameter.TEXT_SIZE, out var size)) info.SizeMm = (double)size * MmPerFoot;
            if (info.Values.TryGetValue(BuiltInParameter.TEXT_FONT, out var font)) info.Font = (string)font;
            result[info.Name] = info;
        }
        return result;
    }

    /// <summary>Close names for a text type the project does not have.</summary>
    public static List<string> SuggestTextTypes(string requested, IEnumerable<string> names) =>
        names.Where(n => n.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0
                         || requested.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                         || n.Split(' ').Any(w => w.Length > 1 && requested.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

    /// <summary>
    /// Builds one Generic Annotation family per label group from <paramref name="templatePath"/>: the template's own
    /// text is removed, one text type is made (copied from the project's text type, or the template's at the given
    /// size) and the text placed at the origin with the requested alignment. Saved to <paramref name="folder"/>.
    /// </summary>
    public static string CreateAnnotationFamily(RevitApplication app, string templatePath, DetailLabelGroup group,
        IReadOnlyDictionary<string, ProjectTextTypeInfo> projectTextTypes, string folder, bool purge,
        Func<Document, HashSet<ElementId>, Dictionary<string, int>> purgeDocument, List<string> warnings)
    {
        var path = Path.Combine(folder, group.FamilyName + ".rfa");
        Document? annotationDoc = null;
        try
        {
            annotationDoc = app.NewFamilyDocument(templatePath)
                            ?? throw new InvalidOperationException($"Revit could not create a family from '{templatePath}'.");
            var category = annotationDoc.OwnerFamily?.FamilyCategory;
            if (category == null || category.Id.Value != (long)BuiltInCategory.OST_GenericAnnotation)
                warnings.Add($"The annotation template's category is '{category?.Name ?? "none"}', not Generic Annotations.");

            var view = FindView(annotationDoc)
                       ?? throw new InvalidOperationException($"The annotation template '{templatePath}' has no view to place text in.");
            var protectedIds = new HashSet<ElementId>();
            var label = group.Template;
            using (var tx = new Transaction(annotationDoc, "Revit MCP - Label text"))
            {
                tx.Start();
                var templateText = new FilteredElementCollector(annotationDoc).OfClass(typeof(TextNote)).ToElementIds();
                if (templateText.Count > 0) annotationDoc.Delete(templateText);

                var baseType = new FilteredElementCollector(annotationDoc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                    ?? throw new InvalidOperationException("The annotation template has no text type to start from.");
                var textType = MakeTextType(annotationDoc, baseType, label, projectTextTypes, warnings);
                protectedIds.Add(textType.Id);

                var options = new TextNoteOptions(textType.Id)
                {
                    HorizontalAlignment = label.Alignment switch
                    {
                        "center" => HorizontalTextAlignment.Center,
                        "right" => HorizontalTextAlignment.Right,
                        _ => HorizontalTextAlignment.Left
                    },
                    VerticalAlignment = VerticalTextAlignment.Middle
                };
                var note = TextNote.Create(annotationDoc, view.Id, XYZ.Zero, label.Text, options);
                protectedIds.Add(note.Id);
                TransactionCommitGuard.CommitOrThrow(tx);
            }

            if (purge) purgeDocument(annotationDoc, protectedIds);
            annotationDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            return path;
        }
        finally
        {
            try { annotationDoc?.Close(false); } catch { /* already closed */ }
        }
    }

    private static TextNoteType MakeTextType(Document annotationDoc, TextNoteType baseType, DetailLabelSpec label,
        IReadOnlyDictionary<string, ProjectTextTypeInfo> projectTextTypes, List<string> warnings)
    {
        ProjectTextTypeInfo? project = null;
        if (label.TextType.Length > 0) projectTextTypes.TryGetValue(label.TextType, out project);
        var sizeMm = label.SizeMm ?? project?.SizeMm ?? DetailFamilySpecParser.DefaultLabelSizeMm;
        var name = project != null
            ? label.SizeMm != null ? $"{project.Name} {sizeMm:0.##} mm" : project.Name
            : $"{sizeMm:0.##} mm";

        var existing = new FilteredElementCollector(annotationDoc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        var type = existing ?? (TextNoteType)baseType.Duplicate(name);

        if (project != null)
        {
            foreach (var pair in project.Values)
            {
                var parameter = type.get_Parameter(pair.Key);
                if (parameter == null || parameter.IsReadOnly) continue;
                try
                {
                    switch (pair.Value)
                    {
                        case double d: parameter.Set(d); break;
                        case int i: parameter.Set(i); break;
                        case string s: parameter.Set(s); break;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Text type '{name}': {pair.Key} was not copied ({ex.Message}).");
                }
            }
        }
        type.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(sizeMm / MmPerFoot);
        return type;
    }

    /// <summary>
    /// Loads the label families into the detail family and places one instance per label at its position, turned by
    /// its rotation. Runs inside the caller's transaction.
    /// </summary>
    public static List<object> PlaceLabels(Document familyDoc, View view, IReadOnlyList<(DetailLabelGroup Group, string Path)> families,
        HashSet<ElementId> protectedIds, List<VisibilityLink> visibility, List<string> warnings)
    {
        var results = new List<object>();
        foreach (var (group, path) in families)
        {
            if (!familyDoc.LoadFamily(path, new FamilyLoadOptions(FamilyIfExists.Overwrite), out var family) || family == null)
            {
                warnings.Add($"The label family '{group.FamilyName}' could not be loaded into the detail family.");
                results.Add(new { family = group.FamilyName, text = group.Template.Text, placed = 0, status = "failed" });
                continue;
            }
            protectedIds.Add(family.Id);
            var symbol = family.GetFamilySymbolIds().Select(id => familyDoc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault();
            if (symbol == null)
            {
                warnings.Add($"The label family '{group.FamilyName}' has no type.");
                results.Add(new { family = group.FamilyName, text = group.Template.Text, placed = 0, status = "failed" });
                continue;
            }
            if (!symbol.IsActive) symbol.Activate();
            protectedIds.Add(symbol.Id);

            var placed = 0;
            var failures = new List<string>();
            foreach (var label in group.Instances)
            {
                try
                {
                    var point = new XYZ(label.Position.X / MmPerFoot, label.Position.Y / MmPerFoot, 0);
                    var instance = familyDoc.FamilyCreate.NewFamilyInstance(point, symbol, view);
                    if (Math.Abs(label.RotationDeg) > 1e-9)
                        ElementTransformUtils.RotateElement(familyDoc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ),
                            label.RotationDeg * Math.PI / 180);
                    if (label.VisibleParameter.Length > 0)
                        visibility.Add(new VisibilityLink { ElementId = instance.Id, Parameter = label.VisibleParameter, Source = label.Source });
                    placed++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{label.Source}: {ex.Message}");
                }
            }
            if (failures.Count > 0) warnings.Add($"Labels not placed: {string.Join("; ", failures.Take(5))}");
            results.Add(new
            {
                family = group.FamilyName,
                text = group.Template.Text,
                textType = group.Template.TextType.Length > 0 ? group.Template.TextType : null,
                sizeMm = group.Template.SizeMm,
                alignment = group.Template.Alignment,
                placed,
                status = placed == group.Instances.Count ? "placed" : placed > 0 ? "partial" : "failed"
            });
        }
        return results;
    }

    private static View? FindView(Document familyDoc)
    {
        var views = new FilteredElementCollector(familyDoc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        return views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
               ?? views.FirstOrDefault(v => v.ViewType == ViewType.DraftingView)
               ?? views.FirstOrDefault(v => v.ViewType is not (ViewType.ThreeD or ViewType.Schedule or ViewType.ProjectBrowser
                   or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined));
    }
}
