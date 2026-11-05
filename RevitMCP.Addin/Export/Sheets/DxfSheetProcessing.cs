using System.IO;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Objects;
using netDxf.Units;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>
/// DXF post-processing ported unchanged in behaviour from the EliteSheets plugin (DxfPaperToModelPromoter,
/// DxfMergeService), so a DXF/merged drawing from the connector is the same file the plugin makes.
/// </summary>
public static class DxfSheetProcessing
{
    /// <summary>EliteSheets places merged sheets 220 mm apart in the template.</summary>
    public const double DefaultSheetSpacingMm = 220.0;

    /// <summary>
    /// Brings the first layout's paper space (title block) and the model geometry seen through its first viewport into
    /// model space at paper scale, and rewrites the file with only that content. A file without a paper layout is left
    /// as it is; one without a viewport gets just its paper content.
    /// </summary>
    public static void PromotePaperToModel(string dxfPath, bool contentOnly = false)
    {
        if (!File.Exists(dxfPath))
            throw new FileNotFoundException("DXF not found.", dxfPath);

        var doc = DxfDocument.Load(dxfPath);
        doc.DrawingVariables.InsUnits = DrawingUnits.Millimeters;

        var paperLayout = doc.Layouts
            .FirstOrDefault(l => !string.Equals(l.Name, Layout.ModelSpaceName, StringComparison.OrdinalIgnoreCase));
        if (paperLayout?.AssociatedBlock == null) return;

        var paperBlock = paperLayout.AssociatedBlock;
        var modelBlock = doc.Blocks.Contains(Layout.ModelSpaceName)
            ? doc.Blocks[Layout.ModelSpaceName]
            : doc.Blocks.FirstOrDefault(b => b.Name.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase));

        var vp = paperBlock.Entities.OfType<Viewport>().FirstOrDefault();
        if (vp == null)
        {
            if (!contentOnly && paperBlock.Entities.Count > 0)
            {
                var tb = new Block($"PS_ONLY_{Guid.NewGuid():N}");
                foreach (var e in paperBlock.Entities)
                    if (e is not Viewport) tb.Entities.Add((EntityObject)e.Clone());
                doc.Blocks.Add(tb);
                doc.Entities.Add(new Insert(tb) { Position = new Vector3(0, 0, 0) });
                doc.Save(dxfPath);
            }
            return;
        }

        // Align the model to the viewport's centre and scale.
        var vpScale = vp.Height > 1e-9 ? vp.ViewHeight / vp.Height : 1.0;
        var scale = 1.0 / vpScale;
        var dx = vp.Center.X - vp.ViewCenter.X / vpScale;
        var dy = vp.Center.Y - vp.ViewCenter.Y / vpScale;

        var combined = new Block($"PROMOTED_{Guid.NewGuid():N}");
        if (!contentOnly)
            foreach (var ent in paperBlock.Entities)
                if (ent is not Viewport) combined.Entities.Add((EntityObject)ent.Clone());

        if (modelBlock != null)
        {
            var modelCopy = new Block($"MODEL_COPY_{Guid.NewGuid():N}");
            foreach (var ent in modelBlock.Entities)
                modelCopy.Entities.Add((EntityObject)ent.Clone());
            doc.Blocks.Add(modelCopy);
            combined.Entities.Add(new Insert(modelCopy)
            {
                Position = new Vector3(dx, dy, 0),
                Scale = new Vector3(scale, scale, 1),
                Rotation = 0
            });
        }
        doc.Blocks.Add(combined);

        // A fresh document holding only the combined insert, saved over the original.
        var newDoc = new DxfDocument();
        newDoc.DrawingVariables.InsUnits = doc.DrawingVariables.InsUnits;
        var combinedClone = new Block(combined.Name);
        foreach (var ent in combined.Entities)
            combinedClone.Entities.Add((EntityObject)ent.Clone());
        newDoc.Blocks.Add(combinedClone);
        newDoc.Entities.Add(new Insert(combinedClone)
        {
            Position = new Vector3(0, 0, 0),
            Scale = new Vector3(1, 1, 1),
            Rotation = 0
        });
        newDoc.Save(dxfPath);
    }

    /// <summary>
    /// Places the (promoted) sheet DXFs side by side, <paramref name="sheetSpacingMm"/> apart, in the template's model
    /// space and saves the result to <paramref name="outputDxfPath"/>. The template's paper layouts keep only content on
    /// locked layers (its frame); viewports and other paper content are removed, as in EliteSheets.
    /// </summary>
    /// <returns>How many source files were placed.</returns>
    public static int MergeIntoTemplate(
        IList<string> sourceDxfPaths,
        string templateDxfPath,
        string outputDxfPath,
        double sheetSpacingMm = DefaultSheetSpacingMm,
        double insertXmm = 0.0,
        double insertYmm = 0.0)
    {
        if (sourceDxfPaths == null || sourceDxfPaths.Count == 0)
            throw new ArgumentException("No source DXF files to merge.", nameof(sourceDxfPaths));
        if (string.IsNullOrWhiteSpace(templateDxfPath) || !File.Exists(templateDxfPath))
            throw new FileNotFoundException("Template DXF not found.", templateDxfPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputDxfPath) ?? ".");

        // 1) Sheets as blocks, inserted along X.
        var pages = new List<(Block Block, double X)>();
        var currentX = 0.0;
        var idx = 0;
        foreach (var path in sourceDxfPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var src = DxfDocument.Load(path);

            var content = src.Blocks.FirstOrDefault(b => !string.IsNullOrEmpty(b.Name) && b.Name[0] != '*' &&
                                                         b.Entities != null && b.Entities.Count > 0);
            if (content == null)
            {
                var modelLayout = src.Layouts.FirstOrDefault(l =>
                    string.Equals(l.Name, Layout.ModelSpaceName, StringComparison.OrdinalIgnoreCase));
                content = modelLayout?.AssociatedBlock;
                if (content != null && (content.Entities == null || content.Entities.Count == 0)) content = null;
            }
            if (content == null) continue;

            var cloned = new Block($"MERGE_{idx}_{Guid.NewGuid():N}");
            foreach (var e in content.Entities)
                cloned.Entities.Add((EntityObject)e.Clone());
            pages.Add((cloned, currentX));
            currentX += sheetSpacingMm;
            idx++;
        }
        if (pages.Count == 0)
            throw new InvalidDataException("None of the sheet DXFs had content to merge.");

        // 2) Into the template's model space.
        var template = DxfDocument.Load(templateDxfPath);
        template.DrawingVariables.InsUnits = DrawingUnits.Millimeters;
        foreach (var (block, x) in pages)
        {
            var name = block.Name;
            if (template.Blocks.Contains(name))
                name = $"_{Guid.NewGuid():N}".Substring(0, 12);
            var copy = new Block(name);
            foreach (var ent in block.Entities)
                copy.Entities.Add((EntityObject)ent.Clone());
            template.Blocks.Add(copy);
            template.Entities.Add(new Insert(template.Blocks[name])
            {
                Position = new Vector3(x + insertXmm, insertYmm, 0),
                Scale = new Vector3(1, 1, 1),
                Rotation = 0
            });
        }

        // 3) Paper layouts keep only content on locked layers.
        foreach (var layout in template.Layouts)
        {
            if (layout.Name.Equals(Layout.ModelSpaceName, StringComparison.OrdinalIgnoreCase)) continue;
            var layoutBlock = layout.AssociatedBlock;
            if (layoutBlock == null) continue;
            var toRemove = layoutBlock.Entities.Where(ent => !(ent.Layer?.IsLocked ?? false)).ToList();
            foreach (var ent in toRemove)
                layoutBlock.Entities.Remove(ent);
        }

        template.Save(outputDxfPath);
        return pages.Count;
    }
}
