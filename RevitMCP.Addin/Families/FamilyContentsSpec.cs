using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.Families;

/// <summary>A family read back as the arguments of revit_create_detail_family, and what did not fit.</summary>
public sealed class FamilyContentsSpecResult
{
    /// <summary>Arguments of revit_create_detail_family, without savePath (the user says where the copy goes).</summary>
    public JObject Spec { get; set; } = new();
    /// <summary>What the family holds that the spec leaves out, one line each.</summary>
    public List<string> Omitted { get; } = new();
    /// <summary>What to know before feeding the spec back (shared parameters, label families, text types).</summary>
    public List<string> Notes { get; } = new();
    /// <summary>What DetailFamilySpecParser.Parse still refuses in the spec; empty when it parses.</summary>
    public List<string> Problems { get; } = new();
}

/// <summary>
/// Builds the arguments of revit_create_detail_family from a <see cref="FamilyContentsSnapshot"/>, so a symbol can be
/// copied by feeding it back to the create tool. Everything the create tool cannot express is listed, never guessed.
/// Pure: no Revit types, unit tested through <see cref="DetailFamilySpecParser.Parse"/>.
/// </summary>
public static class FamilyContentsSpecBuilder
{
    /// <param name="linePatterns">The project's line pattern names, when known: a style's pattern the project lacks is left out.</param>
    /// <param name="fillPatterns">The project's fill pattern names, when known: a region type's pattern the project lacks is left out.</param>
    public static FamilyContentsSpecResult Build(FamilyContentsSnapshot s,
        IReadOnlyCollection<string>? linePatterns = null, IReadOnlyCollection<string>? fillPatterns = null)
    {
        var result = new FamilyContentsSpecResult();
        var omitted = result.Omitted;
        var notes = result.Notes;
        var spec = new JObject { ["familyName"] = s.FamilyName };

        if (!s.IsDetailItem)
            notes.Add($"'{s.FamilyName}' is a {(s.Category.Length > 0 ? s.Category : "family without a category")} family; revit_create_detail_family makes Detail Items.");

        // ── Parameters ───────────────────────────────────────────────────────
        var specType = s.Types.FirstOrDefault(t => string.Equals(t.Name, s.CurrentType, StringComparison.Ordinal)) ?? s.Types.FirstOrDefault();
        if (s.Types.Count > 1)
            omitted.Add($"types: {s.Types.Count - 1} type(s) besides '{specType!.Name}' ({string.Join(", ", s.Types.Where(t => t != specType).Select(t => $"'{t.Name}'").Take(10))}" +
                        $"{(s.Types.Count > 11 ? ", …" : string.Empty)}): the create tool makes one type, and the values are '{specType.Name}''s.");

        var parameters = new JArray();
        var emitted = new Dictionary<string, FamilyContentsParameter>(StringComparer.OrdinalIgnoreCase);
        var sharedNoted = false;
        foreach (var p in s.Parameters)
        {
            if (p.IsBuiltIn) continue;
            if (parameters.Count >= DetailFamilySpecParser.MaxParameters)
            {
                omitted.Add($"parameter '{p.Name}': at most {DetailFamilySpecParser.MaxParameters} parameters per family.");
                continue;
            }
            if (p.IsReporting)
            {
                omitted.Add($"parameter '{p.Name}': a reporting parameter reads a dimension, and the create tool draws no dimensions.");
                continue;
            }
            var nameError = DetailFamilySpecParser.CheckParameterName(p.Name);
            if (nameError != null) { omitted.Add($"parameter '{p.Name}': {nameError}"); continue; }

            var item = new JObject { ["name"] = p.Name };
            if (p.IsShared && p.Guid.Length > 0)
            {
                item["shared"] = true;
                item["guid"] = p.Guid;
                if (!sharedNoted)
                {
                    notes.Add("Shared parameters must already be in the shared parameter file: the create tool assigns them by guid and never creates one.");
                    sharedNoted = true;
                }
            }
            else
            {
                var friendly = p.Friendly ?? ParameterSpecMap.ResolveSpec(p.DataType)?.Friendly;
                if (friendly == null || ParameterSpecMap.ResolveSpec(friendly) == null)
                {
                    omitted.Add($"parameter '{p.Name}': its data type '{p.DataType}' is not one the create tool takes.");
                    continue;
                }
                item["dataType"] = friendly;
            }

            var groupKey = ParameterSpecMap.ResolveGroupKey(p.Group);
            if (groupKey != null) item["group"] = ParameterSpecMap.LabelOfGroupKey(groupKey);
            else if (p.Group.Length > 0) notes.Add($"parameter '{p.Name}': group '{p.Group}' is left out (the create tool picks a default group).");
            if (p.IsInstance) item["instance"] = true;

            if (p.Formula.Length > 0)
                item["formula"] = p.Formula;
            else if (specType != null && specType.Values.TryGetValue(p.Name, out var raw) && raw != null)
            {
                var friendly = p.Friendly ?? ParameterSpecMap.ResolveSpec(p.DataType)?.Friendly;
                var token = ValueToken(raw);
                var error = friendly == null ? "its data type is not known" : DetailFamilySpecParser.ConvertValue(friendly, token).Error;
                if (error == null) item["value"] = token;
                else if (!(raw is string text && text.Length == 0))
                    omitted.Add($"parameter '{p.Name}': value {token.ToString(Newtonsoft.Json.Formatting.None)} ({error.TrimEnd('.')}).");
            }
            parameters.Add(item);
            emitted[p.Name] = p;
        }
        var yesNo = new HashSet<string>(emitted.Values.Where(p => p.Friendly == "yesNo").Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        string Visible(string name, string where)
        {
            if (name.Length == 0) return string.Empty;
            if (yesNo.Contains(name)) return name;
            omitted.Add($"{where}: visibleParameter '{name}' is not a yes/no parameter of the spec; the element is always visible in the copy.");
            return string.Empty;
        }

        // ── Curves ───────────────────────────────────────────────────────────
        var lines = new JArray();
        var arcs = new JArray();
        var circles = new JArray();
        var usedStyles = new List<string>();
        var curveCount = 0;
        for (var i = 0; i < s.Curves.Count; i++)
        {
            var c = s.Curves[i];
            var where = $"curves[{i}] (id {c.Id})";
            if (curveCount >= DetailFamilySpecParser.MaxCurves)
            {
                omitted.Add($"curves[{i}] and the {s.Curves.Count - i - 1} after it: at most {DetailFamilySpecParser.MaxCurves} curves per family.");
                break;
            }
            JObject item;
            switch (c.Kind)
            {
                case "line":
                    if (c.Start.DistanceTo(c.End) < DetailFamilySpecParser.MinCurveLengthMm)
                    {
                        omitted.Add($"{where}: a line shorter than {DetailFamilySpecParser.MinCurveLengthMm} mm.");
                        continue;
                    }
                    item = new JObject { ["start"] = FamilyContentsView.Point(c.Start), ["end"] = FamilyContentsView.Point(c.End) };
                    lines.Add(item);
                    break;
                case "arc" when c.Mid != null:
                {
                    var mid = c.Mid.Value;
                    var cross = (mid.X - c.Start.X) * (c.End.Y - c.Start.Y) - (mid.Y - c.Start.Y) * (c.End.X - c.Start.X);
                    if (Math.Abs(cross) < 1e-6 || c.Start.DistanceTo(c.End) < DetailFamilySpecParser.MinCurveLengthMm)
                    {
                        omitted.Add($"{where}: an arc too small or too flat for the create tool.");
                        continue;
                    }
                    item = new JObject { ["start"] = FamilyContentsView.Point(c.Start), ["mid"] = FamilyContentsView.Point(mid), ["end"] = FamilyContentsView.Point(c.End) };
                    arcs.Add(item);
                    break;
                }
                case "circle":
                    if (c.Radius * 2 * Math.PI < DetailFamilySpecParser.MinCurveLengthMm)
                    {
                        omitted.Add($"{where}: a circle smaller than Revit's minimum curve length.");
                        continue;
                    }
                    item = new JObject { ["center"] = FamilyContentsView.Point(c.Center), ["radius"] = FamilyContentsGeometry.Round(c.Radius) };
                    circles.Add(item);
                    break;
                default:
                    omitted.Add($"{where}: a {(c.CurveType.Length > 0 ? c.CurveType : c.Kind)}; the create tool draws lines, arcs and circles only.");
                    continue;
            }
            if (c.LineStyle.Length > 0)
            {
                item["lineStyle"] = c.LineStyle;
                usedStyles.Add(c.LineStyle);
            }
            var visible = Visible(c.VisibleParameter, where);
            if (visible.Length > 0) item["visibleParameter"] = visible;
            curveCount++;
        }

        // ── Filled regions ───────────────────────────────────────────────────
        var regions = new JArray();
        var usedTypes = new List<string>();
        for (var i = 0; i < s.FilledRegions.Count; i++)
        {
            var r = s.FilledRegions[i];
            var where = $"filledRegions[{i}] (id {r.Id})";
            if (regions.Count >= DetailFamilySpecParser.MaxFilledRegions)
            {
                omitted.Add($"{where} and the {s.FilledRegions.Count - i - 1} after it: at most {DetailFamilySpecParser.MaxFilledRegions} filled regions per family.");
                break;
            }
            var loops = FamilyContentsGeometry.OuterFirst(r.Loops);
            if (loops.Count == 0) { omitted.Add($"{where}: no boundary could be read."); continue; }
            var item = LoopObject(loops[0], out var problem);
            if (item == null) { omitted.Add($"{where}: {problem}"); continue; }
            if (loops.Count > 1)
            {
                var holes = new JArray();
                foreach (var hole in loops.Skip(1))
                {
                    var holeObject = LoopObject(hole, out problem);
                    if (holeObject == null) break;
                    holes.Add(holeObject["points"] is JArray points ? points : holeObject);
                }
                if (holes.Count != loops.Count - 1) { omitted.Add($"{where}: a hole {problem}"); continue; }
                item["holes"] = holes;
            }
            if (r.Type.Length > 0)
            {
                item["type"] = r.Type;
                usedTypes.Add(r.Type);
            }
            if (r.LineStyle.Length > 0)
            {
                item["lineStyle"] = r.LineStyle;
                usedStyles.Add(r.LineStyle);
                if (r.MixedLineStyles) omitted.Add($"{where}: its edges use several line styles; the copy uses '{r.LineStyle}' for all of them.");
            }
            var visible = Visible(r.VisibleParameter, where);
            if (visible.Length > 0) item["visibleParameter"] = visible;
            regions.Add(item);
        }

        // ── Line styles and filled region types they use ────────────────────
        var lineStyles = new JArray();
        foreach (var name in usedStyles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var style = s.LineStyles.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (style == null || style.BuiltIn) continue;   // Revit's own styles exist in every project
            var nameError = DetailFamilySpecParser.CheckLineStyleName(style.Name);
            if (nameError != null)
            {
                omitted.Add($"line style '{style.Name}' is not defined in lineStyles ({nameError.TrimEnd('.')}); the curves name it, so the project must have it.");
                continue;
            }
            var item = new JObject { ["name"] = style.Name };
            if (style.Weight is >= 1 and <= 16) item["weight"] = style.Weight;
            if (IsHex(style.Color)) item["color"] = style.Color;
            if (style.Pattern.Length == 0 || style.Pattern.Equals("solid", StringComparison.OrdinalIgnoreCase)) item["pattern"] = "solid";
            else if (linePatterns == null || linePatterns.Contains(style.Pattern, StringComparer.OrdinalIgnoreCase)) item["pattern"] = style.Pattern;
            else omitted.Add($"line style '{style.Name}': line pattern '{style.Pattern}' is not in the project, so the copy's style keeps the project's pattern (solid when new).");
            lineStyles.Add(item);
        }

        var regionTypes = new JArray();
        foreach (var name in usedTypes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var type = s.FilledRegionTypes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (type == null) continue;
            var nameError = DetailFamilySpecParser.CheckFamilyName(type.Name);
            if (nameError != null)
            {
                omitted.Add($"filled region type '{type.Name}' is not defined in filledRegionTypes ({nameError.Replace("family name", "type name").TrimEnd('.')}); the project must have it.");
                continue;
            }
            var item = new JObject { ["name"] = type.Name };
            void Pattern(string key, string value)
            {
                if (value is "solid" or "none" || fillPatterns == null || fillPatterns.Contains(value, StringComparer.OrdinalIgnoreCase)) item[key] = value;
                else omitted.Add($"filled region type '{type.Name}': {key} '{value}' is not a fill pattern of the project.");
            }
            Pattern("foregroundPattern", type.ForegroundPattern);
            if (IsHex(type.ForegroundColor)) item["foregroundColor"] = type.ForegroundColor;
            Pattern("backgroundPattern", type.BackgroundPattern);
            if (IsHex(type.BackgroundColor)) item["backgroundColor"] = type.BackgroundColor;
            if (type.LineWeight is >= 1 and <= 16) item["lineWeight"] = type.LineWeight;
            item["isMasking"] = type.IsMasking;
            regionTypes.Add(item);
        }

        // ── Labels: text notes, nested text families and parameter labels ───
        var labels = new JArray();
        var textTypeNoted = false;
        void AddText(string text, DetailPoint position, double rotation, double? size, string alignment, string visible, string where, string textType)
        {
            if (text.Trim().Length == 0) { omitted.Add($"{where}: empty text."); return; }
            if (text.Length > 1000) { omitted.Add($"{where}: text longer than 1000 characters."); return; }
            if (labels.Count >= DetailFamilySpecParser.MaxLabels) { omitted.Add($"{where}: at most {DetailFamilySpecParser.MaxLabels} labels per family."); return; }
            var label = new JObject { ["text"] = text, ["position"] = FamilyContentsView.Point(position) };
            if (size is > 0 and <= DetailFamilySpecParser.MaxLabelSizeMm) label["size"] = FamilyContentsGeometry.Round(size.Value);
            if (alignment != "left") label["alignment"] = alignment;
            var turn = FamilyContentsGeometry.NormalizeRotation(rotation);
            if (Math.Abs(turn) > 1e-9) label["rotation"] = FamilyContentsGeometry.Round(turn);
            var shown = Visible(visible, where);
            if (shown.Length > 0) label["visibleParameter"] = shown;
            labels.Add(label);
            if (textType.Length > 0 && !textTypeNoted)
            {
                notes.Add("Text types are not copied (a label's textType names a text type of the project): the copy keeps each text's size, not its font.");
                textTypeNoted = true;
            }
        }

        for (var i = 0; i < s.TextNotes.Count; i++)
        {
            var t = s.TextNotes[i];
            AddText(t.Text, t.Position, t.RotationDeg, t.SizeMm, t.Alignment, t.VisibleParameter, $"textNotes[{i}] (id {t.Id})", t.TextType);
        }

        var labelFamiliesNoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < s.NestedInstances.Count; i++)
        {
            var n = s.NestedInstances[i];
            var where = $"nestedInstances[{i}] ('{n.Family}', id {n.Id})";
            if (!n.IsGenericAnnotation)
            {
                omitted.Add($"{where}: a nested {(n.Category.Length > 0 ? n.Category : "family")} instance; the create tool nests only Generic Annotation text and labels.");
                continue;
            }
            if (n.Mirrored) notes.Add($"{where} is mirrored; the copy is not.");
            var links = n.LinkedParameters;
            if (links.Count == 1)
            {
                var link = links[0];
                if (!emitted.ContainsKey(link.FamilyParameter))
                {
                    omitted.Add($"{where}: it shows parameter '{link.FamilyParameter}', which the spec does not have.");
                    continue;
                }
                if (labels.Count >= DetailFamilySpecParser.MaxLabels) { omitted.Add($"{where}: at most {DetailFamilySpecParser.MaxLabels} labels per family."); continue; }
                var label = new JObject
                {
                    ["parameter"] = emitted[link.FamilyParameter].Name,
                    ["position"] = FamilyContentsView.Point(n.Position),
                    ["labelFamily"] = n.Family,
                    ["labelType"] = n.Type,
                    ["labelParameter"] = link.Parameter
                };
                var turn = FamilyContentsGeometry.NormalizeRotation(n.RotationDeg);
                if (Math.Abs(turn) > 1e-9) label["rotation"] = FamilyContentsGeometry.Round(turn);
                var shown = Visible(n.VisibleParameter, where);
                if (shown.Length > 0) label["visibleParameter"] = shown;
                labels.Add(label);
                if (labelFamiliesNoted.Add(n.Family))
                    notes.Add($"Label family '{n.Family}' must be loaded in the project (or labelFamily set to its .rfa path) before the spec is fed back.");
                continue;
            }
            if (links.Count > 1)
            {
                omitted.Add($"{where}: {links.Count} of its parameters are linked ({string.Join(", ", links.Select(l => $"'{l.Parameter}'→'{l.FamilyParameter}'"))}); a label links one.");
                continue;
            }

            var family = s.NestedFamilies.FirstOrDefault(f => string.Equals(f.Name, n.Family, StringComparison.OrdinalIgnoreCase));
            if (family == null || !family.Opened)
            {
                omitted.Add($"{where}: its text was not read{(family != null && family.Reason.Length > 0 ? $" ({family.Reason.TrimEnd('.')})" : string.Empty)}.");
                continue;
            }
            if (family.Texts.Count != 1 || family.OtherElements > 0)
            {
                omitted.Add($"{where}: it holds {family.Texts.Count} text(s){(family.OtherElements > 0 ? $" and {family.OtherElements} other element(s)" : string.Empty)}; the create tool's text families hold one text.");
                continue;
            }
            var text = family.Texts[0];
            AddText(text.Text, FamilyContentsGeometry.Place(text.Position, n.Position, n.RotationDeg), n.RotationDeg + text.RotationDeg,
                text.SizeMm, text.Alignment, n.VisibleParameter, where, text.TextType);
        }

        // ── Assemble, then check it the way the create tool will ─────────────
        if (parameters.Count > 0) spec["parameters"] = parameters;
        if (lineStyles.Count > 0) spec["lineStyles"] = lineStyles;
        if (lines.Count > 0) spec["lines"] = lines;
        if (arcs.Count > 0) spec["arcs"] = arcs;
        if (circles.Count > 0) spec["circles"] = circles;
        if (regionTypes.Count > 0) spec["filledRegionTypes"] = regionTypes;
        if (regions.Count > 0) spec["filledRegions"] = regions;
        if (labels.Count > 0) spec["labels"] = labels;
        result.Spec = spec;

        result.Problems.AddRange(Check(spec));
        return result;
    }

    /// <summary>
    /// What <see cref="DetailFamilySpecParser.Parse"/> refuses in <paramref name="spec"/>. savePath is the one argument
    /// the spec leaves to the user, so a stand-in folder is given for the check.
    /// </summary>
    public static List<string> Check(JObject spec)
    {
        var args = spec.Properties().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal);
        if (!args.ContainsKey("savePath")) args["savePath"] = new JValue("C:\\");
        var errors = new List<string>();
        try { DetailFamilySpecParser.Parse(args, errors); }
        catch (Exception ex) { errors.Add($"The spec could not be checked: {ex.Message}"); }
        return errors;
    }

    /// <summary>A region boundary as the create tool takes it: {points} or {center, radius}; null with the reason.</summary>
    private static JObject? LoopObject(FamilyContentsLoop loop, out string problem)
    {
        problem = string.Empty;
        if (FamilyContentsGeometry.TryCircle(loop, out var center, out var radius))
            return new JObject { ["center"] = FamilyContentsView.Point(center), ["radius"] = FamilyContentsGeometry.Round(radius) };
        var polygon = FamilyContentsGeometry.Polygon(loop);
        if (polygon == null)
        {
            problem = "has arc or curved edges; the create tool's boundaries are polygons or circles.";
            return null;
        }
        var rounded = polygon.Select(p => new DetailPoint(FamilyContentsGeometry.Round(p.X), FamilyContentsGeometry.Round(p.Y))).ToList();
        var polygonProblem = DetailFamilySpecParser.PolygonProblem(rounded);
        if (polygonProblem != null)
        {
            problem = polygonProblem;
            return null;
        }
        return new JObject { ["points"] = new JArray(rounded.Select(FamilyContentsView.Point)) };
    }

    private static JToken ValueToken(object value) => value switch
    {
        double d => new JValue(FamilyContentsGeometry.Round(d)),
        bool b => new JValue(b),
        long l => new JValue(l),
        int i => new JValue(i),
        string t => new JValue(t),
        _ => new JValue(value.ToString())
    };

    private static bool IsHex(string color) =>
        color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
}
