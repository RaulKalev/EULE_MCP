#nullable disable

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tagging;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools
{
    public sealed class AnnotateDetailLinesTool : IRevitMcpTool
    {
        public string Name => "revit_annotate_detail_lines";

        public string Description =>
            "Places a detail-item family instance at the midpoint of detail lines. " +
            "Required: detailItemTypeId and one target mode: detailLineIds, useSelection=true, " +
            "or annotateAllInView=true. Optional viewId, offsetMm, direction " +
            "(Right/Left/Up/Down), and alignToLineDirection. This ports SmartTags' " +
            "detail-line annotation workflow without its UI. Requires approval and is reversible.";

        public ToolPermission Permission => ToolPermission.RequiresApproval;
        public ToolCategory Category => ToolCategory.Documentation;

        public Task<McpToolResult> ExecuteAsync(
            UIApplication uiapp,
            McpToolRequest request,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
                return Task.FromResult(
                    TaggingToolSupport.Fail(request, "No active document."));

            var view = TaggingToolSupport.ResolveView(
                uidoc,
                doc,
                request.Arguments,
                out var error);
            if (view == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

            var typeId = ToolArguments.GetLong(
                request.Arguments,
                "detailItemTypeId");
            var symbol = typeId > 0
                ? doc.GetElement(new ElementId(typeId)) as FamilySymbol
                : null;
            if (symbol == null ||
                symbol.Category == null ||
                symbol.Category.Id !=
                new ElementId(BuiltInCategory.OST_DetailComponents))
            {
                return Task.FromResult(TaggingToolSupport.Fail(
                    request,
                    "detailItemTypeId must identify a loaded Detail Items family type."));
            }

            var useSelection = ToolArguments.GetBool(
                request.Arguments,
                "useSelection");
            var annotateAll = ToolArguments.GetBool(
                request.Arguments,
                "annotateAllInView");
            var ids = useSelection
                ? uidoc.Selection.GetElementIds()
                    .Select(id => id.Value)
                    .ToArray()
                : ToolArguments.GetLongArray(
                    request.Arguments,
                    "detailLineIds");

            List<DetailCurve> curves;
            if (ids.Length > 0)
            {
                curves = ids.Select(id => doc.GetElement(new ElementId(id)))
                    .OfType<DetailCurve>()
                    .ToList();
            }
            else if (annotateAll)
            {
                curves = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(DetailCurve))
                    .Cast<DetailCurve>()
                    .ToList();
            }
            else
            {
                return Task.FromResult(TaggingToolSupport.Fail(
                    request,
                    "Provide detailLineIds, useSelection=true, or annotateAllInView=true."));
            }
            if (curves.Count == 0)
                return Task.FromResult(TaggingToolSupport.Fail(
                    request,
                    "No matching detail lines were found."));

            TagPlacementDirection direction;
            if (!Enum.TryParse(
                    ToolArguments.GetString(request.Arguments, "direction", "Right"),
                    true,
                    out direction))
            {
                return Task.FromResult(TaggingToolSupport.Fail(
                    request,
                    "direction must be Right, Left, Up, or Down."));
            }

            var createdIds = new List<ElementId>();
            var transactionResult = RevitTransactionRunner.Run(
                doc,
                "Revit MCP - Annotate Detail Lines",
                () =>
                {
                    createdIds = DetailLineAnnotationService.Place(
                        doc,
                        view,
                        curves,
                        symbol,
                        Math.Max(
                            0.0,
                            ToolArguments.GetDouble(
                                request.Arguments,
                                "offsetMm")),
                        direction,
                        ToolArguments.GetBool(
                            request.Arguments,
                            "alignToLineDirection"));
                });
            stopwatch.Stop();

            if (!transactionResult.Success)
            {
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = transactionResult.Diagnostics.OriginalError ??
                              "Detail-line annotation transaction failed.",
                    Data = new
                    {
                        transactionDiagnostics = transactionResult.Diagnostics
                    },
                    DurationMs = stopwatch.ElapsedMilliseconds
                });
            }

            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = createdIds.Count > 0,
                Message = string.Format(
                    "Placed {0} detail annotation(s) for {1} detail line(s).",
                    createdIds.Count,
                    curves.Count),
                Data = new
                {
                    viewId = view.Id.Value,
                    viewName = view.Name,
                    detailLineCount = curves.Count,
                    createdElementIds = createdIds
                        .Select(id => id.Value)
                        .ToArray()
                },
                DurationMs = stopwatch.ElapsedMilliseconds
            });
        }
    }
}
