#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tagging;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools
{
    internal static class TaggingToolSupport
    {
        public static View ResolveView(
            UIDocument uidoc,
            Document doc,
            Dictionary<string, object> arguments,
            out string error)
        {
            var result = PlacementHelpers.ResolveGraphicalView(
                uidoc,
                doc,
                ToolArguments.GetLong(arguments, "viewId"));
            error = result.Error;
            if (result.View is View3D view3D && !view3D.IsLocked)
            {
                error = "Tagging in a 3D view requires a locked view.";
                return null;
            }
            return result.View;
        }

        public static List<Element> ResolveElements(
            UIDocument uidoc,
            Document doc,
            View view,
            Dictionary<string, object> arguments,
            out string error)
        {
            error = null;
            var useSelection = ToolArguments.GetBool(arguments, "useSelection");
            var tagAllInView = ToolArguments.GetBool(arguments, "tagAllInView");
            var ids = useSelection
                ? uidoc.Selection.GetElementIds().Select(id => id.Value).ToArray()
                : ToolArguments.GetLongArray(arguments, "elementIds");
            var categoryIdValue = ToolArguments.GetLong(arguments, "categoryId");
            var categoryId = categoryIdValue > 0
                ? new ElementId(categoryIdValue)
                : ElementId.InvalidElementId;

            IEnumerable<Element> elements;
            if (ids.Length > 0)
            {
                elements = ids.Select(id => doc.GetElement(new ElementId(id)))
                    .Where(element => element != null);
            }
            else if (tagAllInView && categoryId != ElementId.InvalidElementId)
            {
                elements = new FilteredElementCollector(doc, view.Id)
                    .OfCategoryId(categoryId)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            else
            {
                error =
                    "Provide elementIds, useSelection=true, or tagAllInView=true with categoryId.";
                return new List<Element>();
            }

            if (categoryId != ElementId.InvalidElementId)
                elements = elements.Where(element =>
                    element.Category != null &&
                    element.Category.Id == categoryId);

            var result = elements
                .GroupBy(element => element.Id)
                .Select(group => group.First())
                .ToList();
            if (result.Count == 0)
                error = "No matching target elements were found.";
            return result;
        }

        public static FamilySymbol ResolveBaseTagType(
            Document doc,
            IList<Element> elements,
            Dictionary<string, object> arguments,
            out string error)
        {
            var hasExplicitType =
                ToolArguments.GetLong(arguments, "tagTypeId") > 0 ||
                !string.IsNullOrWhiteSpace(
                    ToolArguments.GetString(arguments, "tagFamilyName")) ||
                !string.IsNullOrWhiteSpace(
                    ToolArguments.GetString(arguments, "tagTypeName"));
            if (!hasExplicitType &&
                elements != null &&
                elements
                    .Where(element => element.Category != null)
                    .Select(element => element.Category.Id.Value)
                    .Distinct()
                    .Take(2)
                    .Count() > 1)
            {
                error =
                    "Automatic tag type resolution requires targets from one category. " +
                    "Filter by categoryId or provide an explicit tagTypeId.";
                return null;
            }

            return TagTypeResolverService.Resolve(
                doc,
                elements == null ? null : elements.FirstOrDefault(),
                ToolArguments.GetLong(arguments, "tagTypeId"),
                ToolArguments.GetString(arguments, "tagFamilyName"),
                ToolArguments.GetString(arguments, "tagTypeName"),
                out error);
        }

        public static DirectionTagTypes ResolveDirectionTypes(
            Document doc,
            FamilySymbol baseTagType,
            Dictionary<string, object> arguments)
        {
            var result = new DirectionTagTypes
            {
                LeftTagTypeId = ToElementId(
                    ToolArguments.GetLong(arguments, "leftTagTypeId")),
                RightTagTypeId = ToElementId(
                    ToolArguments.GetLong(arguments, "rightTagTypeId")),
                UpTagTypeId = ToElementId(
                    ToolArguments.GetLong(arguments, "upTagTypeId")),
                DownTagTypeId = ToElementId(
                    ToolArguments.GetLong(arguments, "downTagTypeId"))
            };

            var keyword = ToolArguments.GetString(
                arguments,
                "directionKeyword");
            if (string.IsNullOrWhiteSpace(keyword) ||
                baseTagType == null ||
                baseTagType.Category == null)
                return result;

            var discovered = TagTypeResolverService.FindDirectionTypes(
                doc,
                baseTagType.Category.Id,
                keyword);
            if (result.LeftTagTypeId == ElementId.InvalidElementId)
                result.LeftTagTypeId = discovered.LeftTagTypeId;
            if (result.RightTagTypeId == ElementId.InvalidElementId)
                result.RightTagTypeId = discovered.RightTagTypeId;
            if (result.UpTagTypeId == ElementId.InvalidElementId)
                result.UpTagTypeId = discovered.UpTagTypeId;
            if (result.DownTagTypeId == ElementId.InvalidElementId)
                result.DownTagTypeId = discovered.DownTagTypeId;
            return result;
        }

        public static SmartTagOptions ParseOptions(
            Dictionary<string, object> arguments,
            out string error)
        {
            error = null;
            TagPlacementDirection direction;
            if (!Enum.TryParse(
                    ToolArguments.GetString(arguments, "direction", "Right"),
                    true,
                    out direction))
            {
                error = "direction must be Right, Left, Up, or Down.";
                return null;
            }

            TagAnchorPoint anchorPoint;
            if (!Enum.TryParse(
                    ToolArguments.GetString(arguments, "anchorPoint", "Center"),
                    true,
                    out anchorPoint))
            {
                error =
                    "anchorPoint must be Center, TopLeft, TopCenter, TopRight, " +
                    "LeftCenter, RightCenter, BottomLeft, BottomCenter, or BottomRight.";
                return null;
            }

            TagOrientation orientation;
            if (!Enum.TryParse(
                    ToolArguments.GetString(arguments, "orientation", "Horizontal"),
                    true,
                    out orientation))
            {
                error = "orientation is not a valid Revit TagOrientation value.";
                return null;
            }

            LeaderEndCondition leaderEndCondition;
            if (!Enum.TryParse(
                    ToolArguments.GetString(
                        arguments,
                        "leaderEndCondition",
                        "Attached"),
                    true,
                    out leaderEndCondition))
            {
                error = "leaderEndCondition must be Attached or Free.";
                return null;
            }

            return new SmartTagOptions
            {
                Direction = direction,
                AnchorPoint = anchorPoint,
                HasLeader = ToolArguments.GetBool(arguments, "addLeader"),
                HasLeaderSpecified = arguments.ContainsKey("addLeader"),
                AttachedLengthMillimeters = Math.Max(
                    0.0,
                    ToolArguments.GetDouble(arguments, "attachedLengthMm")),
                FreeLengthMillimeters = Math.Max(
                    0.0,
                    ToolArguments.GetDouble(arguments, "freeLengthMm")),
                Orientation = orientation,
                OrientationSpecified = arguments.ContainsKey("orientation"),
                RotationRadians = ToolArguments.GetDouble(
                    arguments,
                    "rotationDegrees") * Math.PI / 180.0,
                DetectElementRotation = ToolArguments.GetBool(
                    arguments,
                    "detectElementRotation"),
                EnableCollisionDetection = ToolArguments.GetBool(
                    arguments,
                    "enableCollisionDetection",
                    true),
                CollisionGapMillimeters = Math.Max(
                    0.0,
                    ToolArguments.GetDouble(arguments, "collisionGapMm", 1.0)),
                MinimumOffsetMillimeters = Math.Max(
                    0.0,
                    ToolArguments.GetDouble(arguments, "minimumOffsetMm", 300.0)),
                LeaderEndCondition = leaderEndCondition,
                LeaderEndConditionSpecified =
                    arguments.ContainsKey("leaderEndCondition"),
                SkipAlreadyTagged = ToolArguments.GetBool(
                    arguments,
                    "skipAlreadyTagged",
                    true)
            };
        }

        public static TagTemplateRequestOptions ParseTemplateOptions(
            Dictionary<string, object> arguments,
            out string error)
        {
            error = null;
            var scopeText = ToolArguments.GetString(
                arguments,
                "scope",
                "sameFamily");
            TagTemplateScopeMode scope;
            if (!Enum.TryParse(scopeText, true, out scope))
            {
                error =
                    "scope must be sameFamily, sameFamilyAndType, sameCategory, selection, or explicitElementIds.";
                return null;
            }

            var anchorText = ToolArguments.GetString(
                arguments,
                "anchorMode",
                "SmartTagCenter");
            if (string.Equals(
                    anchorText,
                    "Center",
                    StringComparison.OrdinalIgnoreCase))
                anchorText = "SmartTagCenter";
            if (string.Equals(
                    anchorText,
                    "BoundingBoxCenter",
                    StringComparison.OrdinalIgnoreCase))
                anchorText = "ViewBoundingBoxCenter";

            HostAnchorMode anchorMode;
            if (!Enum.TryParse(anchorText, true, out anchorMode))
            {
                error =
                    "anchorMode must be SmartTagCenter, LocationPoint, or ViewBoundingBoxCenter.";
                return null;
            }

            var options = new TagTemplateRequestOptions
            {
                SourceTagId = ToolArguments.GetLong(
                    arguments,
                    "sourceTagId"),
                ScopeMode = scope,
                AnchorMode = anchorMode,
                IncludeSourceHost = ToolArguments.GetBool(
                    arguments,
                    "includeSourceHost"),
                SkipAlreadyTagged = ToolArguments.GetBool(
                    arguments,
                    "skipAlreadyTagged",
                    true),
                ReplaceExistingTags = ToolArguments.GetBool(
                    arguments,
                    "replaceExistingTags"),
                IncludeAllHostTypes = ToolArguments.GetBool(
                    arguments,
                    "includeAllHostTypes",
                    true),
                EnableCollisionDetection = ToolArguments.GetBool(
                    arguments,
                    "enableCollisionDetection"),
                CollisionGapMillimeters = Math.Max(
                    0.0,
                    ToolArguments.GetDouble(
                        arguments,
                        "collisionGapMm",
                        1.0)),
                MinimumOffsetMillimeters = Math.Max(
                    0.0,
                    ToolArguments.GetDouble(
                        arguments,
                        "minimumOffsetMm")),
                Page = Math.Max(
                    1,
                    ToolArguments.GetInt(arguments, "page", 1)),
                PageSize = Math.Min(
                    500,
                    Math.Max(
                        1,
                        ToolArguments.GetInt(
                            arguments,
                            "pageSize",
                            100)))
            };
            foreach (var id in ToolArguments.GetLongArray(
                         arguments,
                         "explicitElementIds"))
                options.ExplicitElementIds.Add(id);

            options.Override = ParseTemplateOverride(
                arguments,
                out error);
            if (error != null)
                return null;

            if (options.Override == null)
                options.Override = new TagTemplateOverride();

            if (arguments.ContainsKey("anchorMode"))
            {
                options.Override.HasAnchorMode = true;
                options.Override.AnchorMode = anchorMode;
            }
            if (arguments.ContainsKey("localRightOffsetMm"))
            {
                options.Override.HasLocalRightOffset = true;
                options.Override.LocalRightOffsetMillimeters =
                    ToolArguments.GetDouble(
                        arguments,
                        "localRightOffsetMm");
            }
            if (arguments.ContainsKey("localFrontOffsetMm"))
            {
                options.Override.HasLocalFrontOffset = true;
                options.Override.LocalFrontOffsetMillimeters =
                    ToolArguments.GetDouble(
                        arguments,
                        "localFrontOffsetMm");
            }

            var rotationModeText = ToolArguments.GetString(
                arguments,
                "rotationMode");
            if (!string.IsNullOrWhiteSpace(rotationModeText))
            {
                TagRotationMode rotationMode;
                if (!Enum.TryParse(
                        rotationModeText,
                        true,
                        out rotationMode))
                {
                    error =
                        "rotationMode must be KeepViewAligned, FollowHost, or RelativeToHost.";
                    return null;
                }
                options.Override.HasRotationMode = true;
                options.Override.RotationMode = rotationMode;
            }
            if (arguments.ContainsKey("relativeRotationDegrees"))
            {
                options.Override.HasRelativeRotation = true;
                options.Override.RelativeRotationDegrees =
                    ToolArguments.GetDouble(
                        arguments,
                        "relativeRotationDegrees");
            }
            if (arguments.ContainsKey("orientation"))
            {
                TagOrientation orientation;
                if (!Enum.TryParse(
                        ToolArguments.GetString(arguments, "orientation"),
                        true,
                        out orientation))
                {
                    error =
                        "orientation is not a valid Revit TagOrientation value.";
                    return null;
                }
                options.Override.HasOrientation = true;
                options.Override.Orientation = orientation;
            }
            if (arguments.ContainsKey("hasLeader"))
            {
                options.Override.HasLeader = true;
                options.Override.LeaderValue =
                    ToolArguments.GetBool(arguments, "hasLeader");
            }
            return options;
        }

        public static McpToolResult Fail(McpToolRequest request, string message)
        {
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = message
            };
        }

        private static ElementId ToElementId(long value)
        {
            return value > 0 ? new ElementId(value) : ElementId.InvalidElementId;
        }

        private static TagTemplateOverride ParseTemplateOverride(
            Dictionary<string, object> arguments,
            out string error)
        {
            error = null;
            if (!arguments.TryGetValue(
                    "analyzedTemplate",
                    out var raw) ||
                raw == null)
                return null;

            JObject root;
            if (raw is JObject objectValue)
            {
                root = objectValue;
            }
            else if (raw is string text)
            {
                root = ToolArguments.TryParseJObject(text);
            }
            else
            {
                try { root = JObject.FromObject(raw); }
                catch { root = null; }
            }
            if (root == null)
            {
                error =
                    "analyzedTemplate must be a JSON object returned by the analysis tool.";
                return null;
            }

            var payload = root["data"] as JObject ?? root;
            var source = payload["source"] as JObject ?? payload;
            var rule = payload["inferredRule"] as JObject ??
                       payload["template"] as JObject ??
                       payload;
            var value = new TagTemplateOverride
            {
                ExpectedSourceTagId =
                    GetLong(source, "tagId", "sourceTagId"),
                ExpectedSourceHostElementId =
                    GetLong(
                        source,
                        "hostElementId",
                        "sourceHostElementId"),
                ExpectedSourceViewId =
                    GetLong(source, "viewId", "sourceViewId"),
                ExpectedTagTypeId =
                    GetLong(source, "tagTypeId")
            };

            if (TryGetEnum(
                    rule,
                    "anchorMode",
                    out HostAnchorMode anchorMode))
            {
                value.HasAnchorMode = true;
                value.AnchorMode = anchorMode;
            }
            if (TryGetDouble(
                    rule,
                    "localRightOffsetMm",
                    "localRightOffsetMillimeters",
                    out var right))
            {
                value.HasLocalRightOffset = true;
                value.LocalRightOffsetMillimeters = right;
            }
            if (TryGetDouble(
                    rule,
                    "localFrontOffsetMm",
                    "localFrontOffsetMillimeters",
                    out var front))
            {
                value.HasLocalFrontOffset = true;
                value.LocalFrontOffsetMillimeters = front;
            }
            if (TryGetEnum(
                    rule,
                    "rotationMode",
                    out TagRotationMode rotationMode))
            {
                value.HasRotationMode = true;
                value.RotationMode = rotationMode;
            }
            if (TryGetDouble(
                    rule,
                    "relativeRotationDegrees",
                    out var relative))
            {
                value.HasRelativeRotation = true;
                value.RelativeRotationDegrees = relative;
            }
            if (TryGetEnum(
                    rule,
                    "orientation",
                    out TagOrientation orientation))
            {
                value.HasOrientation = true;
                value.Orientation = orientation;
            }
            var leaderToken = rule["hasLeader"] ??
                              rule["HasLeader"];
            if (leaderToken != null &&
                leaderToken.Type != JTokenType.Null)
            {
                value.HasLeader = true;
                value.LeaderValue = leaderToken.Value<bool>();
            }
            return value;
        }

        private static long GetLong(
            JObject value,
            params string[] names)
        {
            foreach (var name in names)
            {
                var token = value[name];
                if (token == null || token.Type == JTokenType.Null)
                    continue;
                try { return token.Value<long>(); }
                catch
                {
                    // Try the next accepted property name.
                }
            }
            return 0L;
        }

        private static bool TryGetDouble(
            JObject value,
            string name,
            out double result)
        {
            return TryGetDouble(value, name, null, out result);
        }

        private static bool TryGetDouble(
            JObject value,
            string firstName,
            string secondName,
            out double result)
        {
            result = 0.0;
            var token = value[firstName] ??
                        (secondName == null
                            ? null
                            : value[secondName]);
            if (token == null || token.Type == JTokenType.Null)
                return false;
            try
            {
                result = token.Value<double>();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetEnum<T>(
            JObject value,
            string name,
            out T result)
            where T : struct
        {
            result = default(T);
            var text = value[name]?.Value<string>();
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (typeof(T) == typeof(HostAnchorMode) &&
                string.Equals(
                    text,
                    "Center",
                    StringComparison.OrdinalIgnoreCase))
                text = "SmartTagCenter";
            return Enum.TryParse(text, true, out result);
        }
    }
}
