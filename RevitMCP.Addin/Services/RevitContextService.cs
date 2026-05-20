using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin.Services;

public record RevitContextInfo(
    string RevitVersion,
    bool IsDocumentOpen,
    string DocumentTitle,
    string ActiveViewName,
    bool IsWorkshared,
    string CentralModelPath,
    string LocalModelPath,
    string RevitUsername,
    int SelectedElementCount
);

/// <summary>
/// Reads Revit document context. Must only be called from the Revit API thread.
/// </summary>
public static class RevitContextService
{
    public static RevitContextInfo Read(UIApplication uiapp)
    {
        var app = uiapp.Application;
        var version = app.VersionNumber;
        var doc = uiapp.ActiveUIDocument?.Document;

        if (doc == null)
            return new RevitContextInfo(version, false, string.Empty, string.Empty, false, string.Empty, string.Empty, app.Username, 0);

        var activeViewName = uiapp.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        var isWorkshared = doc.IsWorkshared;
        var centralPath = string.Empty;
        var localPath = doc.PathName;

        if (isWorkshared)
        {
            try
            {
                var wsInfo = doc.GetWorksharingCentralModelPath();
                centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(wsInfo);
            }
            catch { }
        }

        var selectedCount = uiapp.ActiveUIDocument?.Selection?.GetElementIds()?.Count ?? 0;

        return new RevitContextInfo(
            version,
            true,
            doc.Title,
            activeViewName,
            isWorkshared,
            centralPath,
            localPath,
            app.Username,
            selectedCount
        );
    }
}
