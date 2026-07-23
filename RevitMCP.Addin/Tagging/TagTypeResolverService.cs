#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public static class TagTypeResolverService
    {
        public static FamilySymbol Resolve(
            Document doc,
            Element sampleElement,
            long tagTypeId,
            string familyName,
            string typeName,
            out string error)
        {
            error = null;
            if (tagTypeId > 0)
            {
                var byId = doc.GetElement(new ElementId(tagTypeId)) as FamilySymbol;
                if (!IsTagSymbol(byId))
                    error = "The supplied tagTypeId is not a loaded tag family type.";
                return IsTagSymbol(byId) ? byId : null;
            }

            if (!string.IsNullOrWhiteSpace(familyName) ||
                !string.IsNullOrWhiteSpace(typeName))
            {
                var matches = GetAllTagTypes(doc)
                    .Where(symbol =>
                        (string.IsNullOrWhiteSpace(familyName) ||
                         symbol.Family.Name.IndexOf(
                             familyName,
                             StringComparison.OrdinalIgnoreCase) >= 0) &&
                        (string.IsNullOrWhiteSpace(typeName) ||
                         symbol.Name.IndexOf(
                             typeName,
                             StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                if (matches.Count == 1)
                    return matches[0];

                error = matches.Count == 0
                    ? "No loaded tag type matches the supplied family/type names."
                    : "Multiple tag types match; provide a unique name or tagTypeId.";
                return null;
            }

            if (sampleElement == null || sampleElement.Category == null)
            {
                error = "A tag type could not be inferred because the target category is unavailable.";
                return null;
            }

            var builtInName = sampleElement.Category.BuiltInCategory.ToString();
            if (builtInName.StartsWith("OST_", StringComparison.Ordinal))
            {
                var baseName = builtInName.Substring(4);
                var candidates = new List<string> { baseName + "Tags" };
                if (baseName.EndsWith("s", StringComparison.Ordinal))
                    candidates.Add(baseName.Substring(0, baseName.Length - 1) + "Tags");
                if (baseName.EndsWith("es", StringComparison.Ordinal))
                    candidates.Add(baseName.Substring(0, baseName.Length - 2) + "Tags");

                foreach (var candidate in candidates)
                {
                    BuiltInCategory category;
                    if (!Enum.TryParse("OST_" + candidate, out category))
                        continue;

                    var match = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(category)
                        .Cast<FamilySymbol>()
                        .FirstOrDefault();
                    if (match != null)
                        return match;
                }
            }

            var fallback = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_MultiCategoryTags)
                .Cast<FamilySymbol>()
                .FirstOrDefault();
            if (fallback == null)
                error = "No compatible loaded tag type was found; provide tagTypeId.";
            return fallback;
        }

        public static List<FamilySymbol> GetAllTagTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(IsTagSymbol)
                .OrderBy(symbol => symbol.Category == null ? string.Empty : symbol.Category.Name)
                .ThenBy(symbol => symbol.Family.Name)
                .ThenBy(symbol => symbol.Name)
                .ToList();
        }

        public static DirectionTagTypes FindDirectionTypes(
            Document doc,
            ElementId tagCategoryId,
            string keyword)
        {
            var result = new DirectionTagTypes();
            if (doc == null || tagCategoryId == null ||
                tagCategoryId == ElementId.InvalidElementId ||
                string.IsNullOrWhiteSpace(keyword))
                return result;

            var symbols = GetAllTagTypes(doc)
                .Where(symbol => symbol.Category != null &&
                                 symbol.Category.Id == tagCategoryId)
                .ToList();
            result.LeftTagTypeId = FindDirection(symbols, keyword, "Left");
            result.RightTagTypeId = FindDirection(symbols, keyword, "Right");
            result.UpTagTypeId = FindDirection(symbols, keyword, "Up");
            result.DownTagTypeId = FindDirection(symbols, keyword, "Down");
            return result;
        }

        private static ElementId FindDirection(
            IEnumerable<FamilySymbol> symbols,
            string keyword,
            string direction)
        {
            var normalizedKeyword = Normalize(keyword);
            var normalizedDirection = Normalize(direction);
            var match = symbols.FirstOrDefault(symbol =>
            {
                var name = Normalize(symbol.Name);
                return name.Contains(normalizedDirection) ||
                       name.Contains(normalizedKeyword + normalizedDirection);
            });
            return match == null ? ElementId.InvalidElementId : match.Id;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
        }

        private static bool IsTagSymbol(FamilySymbol symbol)
        {
            if (symbol == null ||
                symbol.Category == null ||
                symbol.Category.CategoryType != CategoryType.Annotation)
                return false;

            var builtInName = symbol.Category.BuiltInCategory.ToString();
            return builtInName.EndsWith("Tags", StringComparison.Ordinal) ||
                   builtInName == "OST_MultiCategoryTags";
        }
    }
}
