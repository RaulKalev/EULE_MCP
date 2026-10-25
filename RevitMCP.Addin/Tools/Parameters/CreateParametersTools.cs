using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Parameters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Parameters;

/// <summary>revit_preview_create_parameters (#102): resolves every requested parameter without changing anything.</summary>
public sealed class PreviewCreateParametersTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_parameters";

    public string Description =>
        "Previews creating project parameters, without changing the model or any file. Same arguments as " +
        "revit_create_parameters. Resolves each requested parameter against the shared parameter file and the project's " +
        "bindings and reports willCreate | willRebind | exists | missingDefinition | unknownCategory | invalidType | " +
        "invalid, with the categories, data type and group that would be used.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateParametersExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_create_parameters (#102): creates project parameters and binds them to categories.</summary>
public sealed class CreateParametersTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_parameters";

    public string Description =>
        "Creates project parameters and binds them to categories. Requires approval. parameters: up to 50 of {name, " +
        "source: shared | project, guid?, group? (the shared file group), dataType (text, integer, number, length, area, " +
        "yesNo, material, … or a SpecTypeId string), parameterGroup (Data, Electrical, Identity Data, Text, …; default " +
        "Data), binding: instance | type, categories[], variesAcrossGroups?, ifExists: skip | rebind}. source=shared binds " +
        "a definition of the shared parameter file (sharedParameterFile, else parameters.sharedParameterFile in the config, " +
        "else Revit's setting); the file is never changed unless createInFileIfMissing=true. source=project: Revit 2024 " +
        "and 2026 have no API for a non-shared project parameter, so it is created as a shared definition in the " +
        "connector's own scratch file (group RevitMCP) and the result says so. ifExists=rebind adds categories to an " +
        "existing binding (the union; nothing is removed). One transaction per parameter, one undo step. Run " +
        "revit_preview_create_parameters first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateParametersExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        var choice = SharedParameterFileReader.Choose(uiapp.Application, target?.Document,
            ToolArguments.GetString(request.Arguments, "sharedParameterFile"));
        return FamiliesAndParametersApprovalSummaries.CreateParameters(request, choice.Path);
    }
}

internal static class CreateParametersExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var app = uiapp.Application;
        var args = request.Arguments;
        var warnings = new List<string>();

        if (doc.IsFamilyDocument)
            return Fail(request, "This is a family document: project parameters are bound in projects. Family parameters are out of scope here.");

        args.TryGetValue("parameters", out var raw);
        var requests = ParameterCreatePlanner.Parse(raw, out var parseError);
        if (parseError != null) return Fail(request, parseError);
        var createInFile = ToolArguments.GetBool(args, "createInFileIfMissing");

        // The shared parameter file: read once for the plan, reopened for the writes.
        var choice = SharedParameterFileReader.Choose(app, doc, ToolArguments.GetString(args, "sharedParameterFile"));
        var needsShared = requests.Any(r => r.IsShared);
        List<SharedDefinitionInfo>? sharedDefinitions = null;
        string? sharedProblem = choice.Error;
        if (needsShared && choice.Path != null)
        {
            sharedDefinitions = SharedParameterFileReader.WithFile(app, choice.Path, file =>
                file == null ? null : SharedParameterFileReader.ReadDefinitions(file).Select(r => r.Info).ToList(), warnings);
            if (sharedDefinitions == null) sharedProblem = $"The shared parameter file '{choice.Path}' ({choice.Source}) does not exist or Revit cannot read it.";
        }

        var scratchDefinitions = new List<SharedDefinitionInfo>();
        if (requests.Any(r => !r.IsShared) && System.IO.File.Exists(SharedParameterFileReader.ScratchFilePath))
        {
            scratchDefinitions = SharedParameterFileReader.WithFile(app, SharedParameterFileReader.ScratchFilePath, file =>
                file == null ? new List<SharedDefinitionInfo>() : SharedParameterFileReader.ReadDefinitions(file).Select(r => r.Info).ToList(), warnings);
        }

        var bindings = ParameterBindingReader.Read(doc);
        var context = new ParameterPlanContext
        {
            Existing = bindings.Select(b => b.ToExisting()).ToList(),
            SharedFile = sharedDefinitions,
            SharedFileProblem = sharedProblem,
            ScratchFile = scratchDefinitions,
            CreateInFileIfMissing = createInFile,
            ResolveCategory = name =>
            {
                var (category, error) = ParameterBindingReader.ResolveCategory(doc, name);
                return (category?.Name, error);
            },
            ResolveSpec = ParameterRevitTypes.ResolveSpec,
            ResolveGroup = ParameterRevitTypes.ResolveGroup
        };
        var plan = ParameterCreatePlanner.Plan(requests, context);

        if (!apply)
        {
            sw.Stop();
            var counts = plan.GroupBy(p => p.Action).ToDictionary(g => g.Key, g => g.Count());
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: {string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"))} of {plan.Count} parameter(s).",
                Data = new
                {
                    documentTitle = doc.Title,
                    sharedParameterFile = choice.Path,
                    sharedParameterFileSource = choice.Source,
                    createInFileIfMissing = createInFile,
                    fileWillChange = plan.Any(p => p.WillWrite && p.CreateInFile && p.Request.IsShared),
                    scratchFile = plan.Any(p => !p.Request.IsShared) ? SharedParameterFileReader.ScratchFilePath : null,
                    counts,
                    parameters = plan.Select(p => Describe(p, null)).ToList()
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var outcomes = plan.ToDictionary(p => p, _ => (Status: "notAttempted", Reason: (string?)null, Guid: (string?)null));
        foreach (var item in plan.Where(p => !p.WillWrite))
            outcomes[item] = (item.Action == "exists" ? "skipped" : "failed", item.Reason, item.Definition?.Guid ?? item.Existing?.Guid);

        var writes = plan.Where(p => p.WillWrite).ToList();
        string? groupError = null;
        if (writes.Count > 0)
        {
            if (writes.Any(w => !w.Request.IsShared) && SharedParameterFileReader.EnsureScratchFile() is { } scratchError)
            {
                foreach (var item in writes.Where(w => !w.Request.IsShared)) outcomes[item] = ("failed", scratchError, null);
                writes = writes.Where(w => w.Request.IsShared).ToList();
            }

            using var transactions = ItemTransactions.Start(doc, "Revit MCP - Create Parameters", out var startError);
            if (transactions == null) return Fail(request, startError!, warnings);

            // Revit reads one shared parameter file at a time: the writes run per file, each inside WithFile.
            foreach (var fileGroup in writes.GroupBy(w => w.Request.IsShared ? choice.Path! : SharedParameterFileReader.ScratchFilePath))
            {
                SharedParameterFileReader.WithFile<object?>(app, fileGroup.Key, file =>
                {
                    foreach (var item in fileGroup)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        outcomes[item] = Write(doc, file, item, bindings, transactions);
                    }
                    return null;
                }, warnings);
            }

            groupError = transactions.Finish();
            warnings.AddRange(transactions.Warnings.Distinct().Select(w => "Revit: " + w));
            if (groupError != null)
                foreach (var item in writes.Where(w => outcomes[w].Status is "created" or "rebound"))
                    outcomes[item] = ("failed", groupError, outcomes[item].Guid);
        }

        sw.Stop();
        var created = outcomes.Values.Count(o => o.Status == "created");
        var rebound = outcomes.Values.Count(o => o.Status == "rebound");
        var skipped = outcomes.Values.Count(o => o.Status == "skipped");
        var failedCount = outcomes.Values.Count(o => o.Status == "failed");
        var fileChanged = writes.Any(w => w.CreateInFile && w.Request.IsShared && outcomes[w].Status == "created");
        if (plan.Any(p => !p.Request.IsShared && outcomes[p].Status == "created"))
            warnings.Add("source=project parameters were created as shared definitions in the connector's scratch file " +
                         $"({SharedParameterFileReader.ScratchFilePath}, group {ParameterCreatePlanner.ScratchGroup}): Revit 2024/2026 have no API for a non-shared project parameter.");

        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created + rebound + skipped > 0 && groupError == null,
            Status = created + rebound + skipped == 0 ? "transaction_failed" : null,
            Message = $"Created {created}, extended {rebound}, skipped {skipped}, failed {failedCount} of {plan.Count} parameter(s)" +
                      $"{(fileChanged ? $"; definitions were added to '{choice.Path}'" : string.Empty)}{(groupError != null ? $": {groupError}" : ".")}",
            Data = new
            {
                documentTitle = doc.Title,
                sharedParameterFile = choice.Path,
                sharedParameterFileChanged = fileChanged,
                created,
                rebound,
                skipped,
                failed = failedCount,
                parameters = plan.Select(p => Describe(p, outcomes[p])).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static (string Status, string? Reason, string? Guid) Write(Document doc, DefinitionFile? file, ParameterPlanItem item,
        List<BindingRow> bindings, ItemTransactions transactions)
    {
        var name = item.Request.Name.Trim();
        if (file == null)
            return ("failed", "The parameter file could not be opened for writing.", null);

        string? guid = item.Definition?.Guid;
        var status = item.Action == "willRebind" ? "rebound" : "created";
        var error = transactions.Run($"Revit MCP - Parameter {name}", () =>
        {
            var categories = ParameterBindingReader.CategorySet(doc, item.Categories);
            if (categories.IsEmpty) return "None of the categories could be resolved.";
            var binding = ParameterBindingReader.NewBinding(doc, item.Request.IsInstance, categories);

            if (item.Action == "willRebind")
            {
                var existing = ParameterBindingReader.Find(bindings, name, item.Existing?.Guid ?? string.Empty);
                if (existing == null) return "The existing binding was not found any more.";
                guid = existing.Guid;
                return doc.ParameterBindings.ReInsert(existing.Definition, binding)
                    ? null
                    : "Revit refused to extend the binding (ReInsert returned false).";
            }

            ExternalDefinition? definition;
            if (item.CreateInFile)
            {
                var spec = ParameterRevitTypes.SpecOf(item.DataTypeId);
                if (spec == null) return "No data type to create the definition with.";
                definition = SharedParameterFileReader.Create(file, item.FileGroup, name, spec);
            }
            else
            {
                definition = SharedParameterFileReader.Find(file, item.Definition!);
                if (definition == null) return $"The definition '{name}' is no longer in the file.";
            }
            guid = definition.GUID.ToString();

            var group = ParameterRevitTypes.GroupOf(item.ParameterGroupKey);
            var inserted = group == null
                ? doc.ParameterBindings.Insert(definition, binding)
                : doc.ParameterBindings.Insert(definition, binding, group);
            if (!inserted)
                return "Revit refused the binding (Insert returned false): a parameter with this GUID or name may already be bound.";

            if (item.Request.IsInstance && item.Request.VariesAcrossGroups == true)
            {
                var parameterElement = SharedParameterElement.Lookup(doc, definition.GUID);
                parameterElement?.GetDefinition()?.SetAllowVaryBetweenGroups(doc, true);
            }
            return null;
        });

        return error == null ? (status, item.Reason, guid) : ("failed", error, guid);
    }

    private static object Describe(ParameterPlanItem item, (string Status, string? Reason, string? Guid)? outcome)
    {
        var spec = ParameterRevitTypes.SpecOf(item.DataTypeId);
        return new
        {
            name = item.Request.Name,
            source = item.Request.IsShared ? "shared" : "project",
            action = item.Action,
            status = outcome?.Status,
            reason = outcome?.Reason ?? item.Reason,
            guid = outcome?.Guid ?? item.Definition?.Guid ?? item.Existing?.Guid,
            fileGroup = item.Definition?.Group ?? (item.CreateInFile ? item.FileGroup : null),
            createInFile = item.CreateInFile,
            dataType = item.DataTypeId,
            dataTypeLabel = ParameterRevitTypes.SpecLabel(spec),
            parameterGroup = item.ParameterGroupKey == null ? null : ParameterSpecMap.LabelOfGroupKey(item.ParameterGroupKey),
            binding = item.Request.IsInstance ? "instance" : "type",
            categories = item.Categories,
            addedCategories = item.AddedCategories.Count > 0 ? item.AddedCategories : null,
            unknownCategories = item.UnknownCategories.Count > 0 ? item.UnknownCategories : null,
            variesAcrossGroups = item.Request.VariesAcrossGroups
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = "validation_failed",
        Message = message,
        Warnings = warnings ?? new List<string>()
    };
}
