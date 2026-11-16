using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Parameters;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Parameters;

/// <summary>revit_preview_set_parameter_values (#165): what writing a value per element would do, without changing anything.</summary>
public sealed class PreviewSetParameterValuesTool : IRevitMcpTool
{
    public const string ToolName = "revit_preview_set_parameter_values";
    public string Name => ToolName;

    public string Description =>
        "Previews writing a different value to each element, without changing the model. Same arguments as " +
        "revit_set_parameter_values. Returns counts only (rows, willChange, unchanged, notFound, noParameter, readOnly, " +
        "invalidValue, conflict) with up to sampleSize examples, and the duplicate values when unique=true.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetParameterValuesExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_set_parameter_values (#165): writes ElementId → value pairs in one transaction.</summary>
public sealed class SetParameterValuesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public const string ToolName = "revit_set_parameter_values";
    public string Name => ToolName;

    public string Description =>
        "Writes a different value to each element in one transaction and one approval. Rows come from values " +
        "[{elementId | uniqueId, value, parameterName?}] or from sourceFile (.xlsx/.xlsm/.csv/.json; sourceSheet, idColumn, " +
        "uniqueIdColumn, valueColumn, parameterColumn). parameterName (or builtInParameter / parameterGuid) is the default " +
        "parameter; exactMatch defaults to true. String, Integer, Double (internal units) and ElementId parameters. " +
        "onlyIfDifferent (default true) skips elements that already hold the value. unique=true refuses the whole write " +
        "when a value would be held by more than one element (in the input, or in the model within the targets' " +
        "categories). The result is aggregated (counts and up to sampleSize examples). Run " +
        "revit_preview_set_parameter_values first; create a checkpoint with the parameter in parameters to be able to roll back.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetParameterValuesExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        try
        {
            return SetParameterValuesExecutor.ApprovalSummary(uiapp, request);
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
            return null;
        }
    }
}

internal static class SetParameterValuesExecutor
{
    private sealed class PlannedWrite
    {
        public ParameterValueRow Row = null!;
        public Element Element = null!;
        public Parameter Parameter = null!;
        public string ParameterName = string.Empty;
        public string? OldValue;
        public bool Unchanged;
        public ElementId? ElementIdValue;
    }

    private sealed class Plan
    {
        public string Source = string.Empty;
        public int RowCount;
        public int Collapsed;
        public List<PlannedWrite> Writes = new();
        public List<ParameterValueProblem> Problems = new();
        public List<DuplicateValue> Duplicates = new();
        public HashSet<string> PartialMatches = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Parameters = new(StringComparer.OrdinalIgnoreCase);
        public int Changing => Writes.Count(w => !w.Unchanged);
        public int Unchanged => Writes.Count(w => w.Unchanged);
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var args = request.Arguments;
        var sample = ParameterValueMapping.ClampSample(ToolArguments.GetInt(args, "sampleSize", ParameterValueMapping.DefaultSampleSize));
        var unique = ToolArguments.GetBool(args, "unique");

        var plan = BuildPlan(doc, request, cancellationToken, out var error);
        if (plan == null) return Fail(request, error!);

        var warnings = new List<string>();
        if (plan.PartialMatches.Count > 0)
            warnings.Add($"Resolved by partial name match to: {string.Join(", ", plan.PartialMatches.Select(n => $"'{n}'"))}. Pass exactMatch=true (the default) to avoid it.");
        if (plan.Collapsed > 0)
            warnings.Add($"{plan.Collapsed} repeated row(s) with the same element, parameter and value were written once.");

        if (!apply || (unique && plan.Duplicates.Count > 0) || plan.Changing == 0)
        {
            sw.Stop();
            var refused = apply && unique && plan.Duplicates.Count > 0;
            var message = refused
                ? $"Nothing written: unique=true and {plan.Duplicates.Count} value(s) would be held by more than one element."
                : apply
                    ? $"Nothing to write: {plan.Unchanged} element(s) already hold the value, {plan.Problems.Count} row(s) could not be used."
                    : $"Preview: {plan.Changing} of {plan.RowCount} row(s) will change {Describe(plan.Parameters)}; {plan.Unchanged} unchanged, {plan.Problems.Count} problem(s).";
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = !refused,
                Status = refused ? "validation_failed" : null,
                Message = message,
                Data = Summary(doc, plan, sample, apply ? 0 : -1, null, unique),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        // ── Write ──
        var written = new List<PlannedWrite>();
        var writeFailures = new List<ParameterValueProblem>();
        var onlyIfDifferent = ToolArguments.GetBool(args, "onlyIfDifferent", true);
        cancellationToken.ThrowIfCancellationRequested();
        using (var tx = new Transaction(doc, "Revit MCP - Set Parameter Values"))
        {
            tx.Start();
            var n = 0;
            foreach (var w in plan.Writes)
            {
                if (++n % 500 == 0) cancellationToken.ThrowIfCancellationRequested();
                if (w.Unchanged && onlyIfDifferent) continue;
                try
                {
                    if (Write(w)) written.Add(w);
                    else writeFailures.Add(Problem(w.Row, "invalidValue: Revit refused the value"));
                }
                catch (Exception ex)
                {
                    writeFailures.Add(Problem(w.Row, "writeFailed: " + ex.Message));
                }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        plan.Problems.AddRange(writeFailures);

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = written.Count > 0,
            Status = written.Count == 0 ? "transaction_failed" : null,
            Message = $"Wrote {Describe(plan.Parameters)} on {written.Count} element(s); {plan.Unchanged} already held the value, {plan.Problems.Count} row(s) failed or were skipped.",
            Data = Summary(doc, plan, sample, written.Count, written, unique),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>"N element(s), parameter Y, Z change" — built live so the approval shows real counts, not the values.</summary>
    public static string ApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return "Write parameter values (the document could not be resolved).";
        var plan = BuildPlan(target.Document, request, CancellationToken.None, out var error);
        if (plan == null) return $"Write parameter values — the input cannot be used: {error}";

        var lines = new List<string>
        {
            $"Write {Describe(plan.Parameters)} on {plan.Changing} element(s) in '{target.Document.Title}', one value per element ({plan.Source}).",
            $"{plan.RowCount} row(s): {plan.Changing} change, {plan.Unchanged} already hold the value, {plan.Problems.Count} cannot be used."
        };
        var reasons = ParameterValueMapping.CountByReason(plan.Problems);
        if (reasons.Count > 0) lines.Add("Problems: " + string.Join(", ", reasons.Select(r => $"{r.Value} {r.Key}")) + ".");
        foreach (var w in plan.Writes.Where(w => !w.Unchanged).Take(5))
            lines.Add($"  {w.Element.Id.Value}: '{w.OldValue ?? string.Empty}' → '{w.Row.Value}'");
        if (plan.Changing > 5) lines.Add($"  … and {plan.Changing - 5} more.");
        if (ToolArguments.GetBool(request.Arguments, "unique"))
            lines.Add(plan.Duplicates.Count == 0
                ? "unique=true: no value would be held by more than one element."
                : $"unique=true: {plan.Duplicates.Count} duplicate value(s) — the call will write nothing.");
        lines.Add("One transaction, one undo step.");
        return string.Join("\n", lines);
    }

    // ── Plan ────────────────────────────────────────────────────────────────

    private static Plan? BuildPlan(Document doc, McpToolRequest request, CancellationToken cancellationToken, out string? error)
    {
        var args = request.Arguments;
        error = null;

        var defaultName = ToolArguments.GetString(args, "parameterName").Trim();
        var exact = ToolArguments.GetBool(args, "exactMatch", true);
        var defaultSelector = new ParameterTarget
        {
            Name = defaultName,
            ExactMatch = exact,
            BuiltInParameter = NullIfBlank(ToolArguments.GetString(args, "builtInParameter")),
            Guid = NullIfBlank(ToolArguments.GetString(args, "parameterGuid"))
        };
        if (defaultSelector.BuiltInParameter != null && !Enum.TryParse<BuiltInParameter>(defaultSelector.BuiltInParameter, true, out _))
        {
            error = $"Unknown builtInParameter '{defaultSelector.BuiltInParameter}'. Use the BuiltInParameter enum name.";
            return null;
        }
        if (defaultSelector.Guid != null && !Guid.TryParse(defaultSelector.Guid, out _))
        {
            error = $"parameterGuid '{defaultSelector.Guid}' is not a valid GUID.";
            return null;
        }
        var defaultKey = defaultName.Length > 0 ? defaultName : defaultSelector.BuiltInParameter ?? defaultSelector.Guid ?? string.Empty;

        // Rows: inline or from a file, not both.
        args.TryGetValue("values", out var rawValues);
        var sourceFile = ToolArguments.GetString(args, "sourceFile").Trim();
        List<ParameterValueRow> rows;
        string source;
        var inline = ParameterValueMapping.ParseInline(rawValues, out var inlineError);
        if (inlineError != null) { error = inlineError; return null; }
        if (sourceFile.Length > 0)
        {
            if (inline.Count > 0) { error = "Give values or sourceFile, not both."; return null; }
            var fileSource = new ParameterValueSource
            {
                Path = sourceFile,
                Sheet = NullIfBlank(ToolArguments.GetString(args, "sourceSheet")),
                IdColumn = NullIfBlank(ToolArguments.GetString(args, "idColumn")),
                UniqueIdColumn = NullIfBlank(ToolArguments.GetString(args, "uniqueIdColumn")),
                ValueColumn = NullIfBlank(ToolArguments.GetString(args, "valueColumn")),
                ParameterColumn = NullIfBlank(ToolArguments.GetString(args, "parameterColumn"))
            };
            rows = ParameterValueMapping.ReadFile(fileSource, out var fileError);
            if (fileError != null) { error = fileError; return null; }
            source = $"from {System.IO.Path.GetFileName(sourceFile)}{(fileSource.Sheet != null ? $", sheet '{fileSource.Sheet}'" : string.Empty)}";
        }
        else
        {
            rows = inline;
            source = "values given in the call";
        }
        if (rows.Count == 0) { error = "No rows: give values [{elementId | uniqueId, value}] or sourceFile."; return null; }

        var scope = ToolArguments.GetString(args, "scope", "Instance");
        var onlyIfDifferent = ToolArguments.GetBool(args, "onlyIfDifferent", true);
        var plan = new Plan { Source = source, RowCount = rows.Count };
        var usable = ParameterValueMapping.Normalize(rows, defaultKey, plan.Problems, out plan.Collapsed);
        var idByName = new Dictionary<string, ElementId?>(StringComparer.OrdinalIgnoreCase);

        var n = 0;
        foreach (var row in usable)
        {
            if (++n % 500 == 0) cancellationToken.ThrowIfCancellationRequested();
            var element = row.ElementId != null ? doc.GetElement(new ElementId(row.ElementId.Value)) : doc.GetElement(row.UniqueId);
            if (element == null) { plan.Problems.Add(Problem(row, "notFound: no such element in the document")); continue; }

            var selector = row.ParameterName == null ? defaultSelector : new ParameterTarget { Name = row.ParameterName, ExactMatch = exact };
            var isType = element is ElementType;
            var resolution = ParameterResolver.Resolve(doc, element, selector,
                includeInstance: scope != "Type" || isType,
                includeType: scope != "Instance" && !isType);
            var param = ParameterResolver.Selected(resolution);
            if (param == null)
            {
                plan.Problems.Add(Problem(row, "noParameter: " + (resolution.Problem ?? $"{selector.Describe()} not found")));
                continue;
            }
            if (param.IsReadOnly) { plan.Problems.Add(Problem(row, $"readOnly: '{param.Definition?.Name}' is read-only")); continue; }
            if (resolution.MatchedBy == "partial") plan.PartialMatches.Add(param.Definition?.Name ?? string.Empty);

            var w = new PlannedWrite { Row = row, Element = element, Parameter = param, ParameterName = param.Definition?.Name ?? selector.Describe() };
            var valueProblem = Check(doc, w, idByName);
            if (valueProblem != null) { plan.Problems.Add(Problem(row, valueProblem)); continue; }
            w.OldValue = Text(param);
            w.Unchanged = onlyIfDifferent && Same(w);
            plan.Writes.Add(w);
            plan.Parameters.Add(w.ParameterName);
        }

        // Two rows landing on the same parameter (a type parameter through two instances) with different values.
        foreach (var group in plan.Writes.GroupBy(w => (Owner: w.Parameter.Element?.Id.Value ?? w.Element.Id.Value, w.ParameterName)).ToList())
        {
            if (group.Select(w => w.Row.Value).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            foreach (var w in group)
            {
                plan.Writes.Remove(w);
                plan.Problems.Add(Problem(w.Row, $"conflict: rows {string.Join(", ", group.Select(g => g.Row.Row))} write different values to the same parameter of element {group.Key.Owner}"));
            }
        }

        if (ToolArguments.GetBool(args, "unique"))
            plan.Duplicates = FindDuplicates(doc, plan, scope, cancellationToken);
        return plan;
    }

    /// <summary>
    /// Duplicates once the writes are done: the targets with their new values plus every other element of the
    /// targets' categories with its current value of the same parameter.
    /// </summary>
    private static List<DuplicateValue> FindDuplicates(Document doc, Plan plan, string scope, CancellationToken cancellationToken)
    {
        var finalValues = new List<(string Parameter, string Element, string? Value)>();
        var targetOwners = new HashSet<(long, string)>();
        foreach (var w in plan.Writes)
        {
            var owner = w.Parameter.Element?.Id.Value ?? w.Element.Id.Value;
            if (targetOwners.Add((owner, w.ParameterName)))
                finalValues.Add((w.ParameterName, owner.ToString(CultureInfo.InvariantCulture), w.Row.Value));
        }

        foreach (var byParameter in plan.Writes.GroupBy(w => w.ParameterName))
        {
            var probe = byParameter.First().Parameter;
            var categoryIds = byParameter.Select(w => w.Parameter.Element?.Category?.Id ?? w.Element.Category?.Id)
                .Where(id => id != null).Select(id => id!).GroupBy(id => id.Value).Select(g => g.First()).ToList();
            if (categoryIds.Count == 0) continue;

            var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categoryIds));
            collector = byParameter.Any(w => w.Parameter.Element is ElementType) ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
            var n = 0;
            foreach (var e in collector)
            {
                if (++n % 2000 == 0) cancellationToken.ThrowIfCancellationRequested();
                if (targetOwners.Contains((e.Id.Value, byParameter.Key))) continue;
                var p = SameParameterOn(e, probe);
                if (p == null) continue;
                finalValues.Add((byParameter.Key, e.Id.Value.ToString(CultureInfo.InvariantCulture), Text(p)));
            }
        }
        return ParameterValueMapping.FindDuplicates(finalValues);
    }

    private static Parameter? SameParameterOn(Element e, Parameter probe)
    {
        try
        {
            if (probe.IsShared) return e.get_Parameter(probe.GUID);
            if (probe.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID)
                return e.get_Parameter(d.BuiltInParameter);
            return probe.Definition == null ? null : e.LookupParameter(probe.Definition.Name);
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
            return null;
        }
    }

    // ── Values ──────────────────────────────────────────────────────────────

    /// <summary>Null when the value fits the parameter's storage type; otherwise the problem.</summary>
    private static string? Check(Document doc, PlannedWrite w, Dictionary<string, ElementId?> idByName)
    {
        var value = w.Row.Value;
        switch (w.Parameter.StorageType)
        {
            case StorageType.String:
                return null;
            case StorageType.Integer:
                return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    ? null : $"invalidValue: '{value}' is not an integer";
            case StorageType.Double:
                return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    ? null : $"invalidValue: '{value}' is not a number (internal units, '.' decimal separator)";
            case StorageType.ElementId:
                w.ElementIdValue = ResolveElementId(doc, value.Trim(), idByName);
                return w.ElementIdValue != null ? null : $"invalidValue: no element with id or name '{value}'";
            default:
                return $"unsupportedType: {w.Parameter.StorageType}";
        }
    }

    private static ElementId? ResolveElementId(Document doc, string value, Dictionary<string, ElementId?> idByName)
    {
        if (value.Length == 0 || value == "-1") return ElementId.InvalidElementId;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return doc.GetElement(new ElementId(id)) != null ? new ElementId(id) : null;
        if (idByName.TryGetValue(value, out var cached)) return cached;

        var match = new FilteredElementCollector(doc).WhereElementIsElementType()
                        .FirstOrDefault(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase))
                    ?? new FilteredElementCollector(doc).WhereElementIsNotElementType()
                        .FirstOrDefault(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase));
        return idByName[value] = match?.Id;
    }

    private static bool Same(PlannedWrite w)
    {
        var p = w.Parameter;
        var value = w.Row.Value.Trim();
        return p.StorageType switch
        {
            StorageType.String => string.Equals(p.AsString() ?? string.Empty, w.Row.Value, StringComparison.Ordinal),
            StorageType.Integer => p.HasValue && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && p.AsInteger() == i,
            StorageType.Double => p.HasValue && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(p.AsDouble() - d) < 1e-9,
            StorageType.ElementId => w.ElementIdValue != null && p.AsElementId() == w.ElementIdValue,
            _ => false
        };
    }

    private static bool Write(PlannedWrite w)
    {
        var p = w.Parameter;
        var value = w.Row.Value.Trim();
        return p.StorageType switch
        {
            StorageType.String => p.Set(w.Row.Value),
            StorageType.Integer => p.Set(int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            StorageType.Double => p.Set(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)),
            StorageType.ElementId => w.ElementIdValue != null && p.Set(w.ElementIdValue),
            _ => false
        };
    }

    private static string? Text(Parameter p) => p.StorageType == StorageType.String ? p.AsString() : ParameterResolver.DisplayValue(p);

    // ── Result ──────────────────────────────────────────────────────────────

    /// <summary>Counts and samples only: a 6000-row result would flood the agent's context.</summary>
    private static object Summary(Document doc, Plan plan, int sample, int writtenCount, List<PlannedWrite>? written, bool unique)
    {
        var reasons = ParameterValueMapping.CountByReason(plan.Problems);
        int Count(string code) => reasons.TryGetValue(code, out var c) ? c : 0;
        var shown = written ?? plan.Writes.Where(w => !w.Unchanged).ToList();
        return new
        {
            documentTitle = doc.Title,
            source = plan.Source,
            parameters = plan.Parameters.ToList(),
            rows = plan.RowCount,
            willChange = written == null ? plan.Changing : (int?)null,
            written = writtenCount >= 0 ? writtenCount : (int?)null,
            unchanged = plan.Unchanged,
            collapsedRepeats = plan.Collapsed,
            notFound = Count("notFound"),
            noParameter = Count("noParameter"),
            readOnly = Count("readOnly"),
            invalidValue = Count("invalidValue"),
            invalidElementId = Count("invalidElementId"),
            conflict = Count("conflict"),
            writeFailed = Count("writeFailed"),
            problemsByReason = reasons,
            unique = unique ? new
            {
                duplicateValues = plan.Duplicates.Count,
                samples = plan.Duplicates.Take(sample).Select(d => new { d.Parameter, d.Value, elementCount = d.Elements.Count, elements = d.Elements.Take(10).ToList() }).ToList()
            } : null,
            changes = shown.Take(sample).Select(w => new
            {
                row = w.Row.Row,
                elementId = w.Element.Id.Value,
                parameter = w.ParameterName,
                oldValue = w.OldValue,
                newValue = w.Row.Value
            }).ToList(),
            problems = plan.Problems.OrderBy(p => p.Row).Take(sample).ToList(),
            sampleSize = sample
        };
    }

    private static ParameterValueProblem Problem(ParameterValueRow row, string reason)
        => new() { Row = row.Row, Element = row.ElementKey, Reason = reason };

    private static string Describe(IEnumerable<string> parameters)
    {
        var list = parameters.ToList();
        return list.Count == 0 ? "the parameter" : list.Count == 1 ? $"'{list[0]}'" : string.Join(", ", list.Select(n => $"'{n}'"));
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static McpToolResult Fail(McpToolRequest request, string message) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = "validation_failed",
        Message = message
    };
}
