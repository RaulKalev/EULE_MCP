using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>Which exporters this Revit can run (OptionalFunctionalityUtils), so a missing one fails fast with exporter_missing.</summary>
public static class ExporterAvailability
{
    public static bool IsAvailable(string format)
    {
        try
        {
            return format switch
            {
                // Native PDF export is part of Revit since 2022; there is no availability switch for it.
                ExportFormats.Pdf => true,
                ExportFormats.Dwg => OptionalFunctionalityUtils.IsDWGExportAvailable(),
                ExportFormats.Dxf => OptionalFunctionalityUtils.IsDXFExportAvailable(),
                ExportFormats.Ifc => OptionalFunctionalityUtils.IsIFCAvailable(),
                ExportFormats.Nwc => OptionalFunctionalityUtils.IsNavisworksExporterAvailable(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    public static string Hint(string format) => format switch
    {
        ExportFormats.Nwc => "Install the Navisworks NWC exporter for this Revit version (Navisworks Exporters / Navisworks Manage).",
        ExportFormats.Ifc => "The IFC exporter is not available in this Revit installation; reinstall or repair the Revit IFC add-in.",
        _ => $"Revit reports that {format.ToUpperInvariant()} export is not available in this installation."
    };

    public static Dictionary<string, bool> All() =>
        ExportFormats.All.ToDictionary(f => f, IsAvailable);
}
