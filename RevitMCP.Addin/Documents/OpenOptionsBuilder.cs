using RevitMCP.Addin.Plans;

namespace RevitMCP.Addin.Documents;

public enum OpenMode
{
    /// <summary>A workshared central becomes a new local copy (the default for centrals); anything else opens plainly.</summary>
    Auto,
    NewLocal,
    Detach,
    Plain
}

public enum WorksetChoice
{
    All,
    Editable,
    LastViewed,
    CloseAll,
    Named
}

/// <summary>revit_open_document arguments as a plain description; the tool builds Revit's OpenOptions from it.</summary>
public sealed class OpenRequest
{
    public string Path { get; set; } = string.Empty;
    public OpenMode Mode { get; set; } = OpenMode.Auto;
    public WorksetChoice Worksets { get; set; } = WorksetChoice.All;
    public List<string> WorksetNames { get; set; } = new();
    public string? LocalFolder { get; set; }
    public bool Audit { get; set; }
    public bool Activate { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Parses revit_open_document's arguments (#101): path, mode (newLocal | detach | plain), worksets (all | editable |
/// lastViewed | closeAll | a list of names), localFolder, audit, activate. Pure.
/// </summary>
public static class OpenOptionsBuilder
{
    private static readonly string[] ServerPrefixes = { "RSN://", "BIM 360://", "Autodesk Docs://", "ACC://" };

    public static OpenRequest Parse(IDictionary<string, object?> arguments)
    {
        var request = new OpenRequest();
        JToken(arguments, "path", out var path);
        request.Path = PlanArgs.String(path).Trim().Trim('"');
        if (request.Path.Length == 0)
            request.Errors.Add("path is required: the central model, or a plain .rvt file.");
        else if (ServerPrefixes.Any(p => request.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            request.Errors.Add("Revit Server and cloud models cannot be opened by the connector yet; open them in Revit and pass document=<title>.");
        else if (!request.Path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            request.Errors.Add("path must be a .rvt file.");

        JToken(arguments, "mode", out var mode);
        switch (PlanArgs.String(mode).Trim().ToLowerInvariant())
        {
            case "": case "auto": request.Mode = OpenMode.Auto; break;
            case "newlocal": case "new_local": case "local": request.Mode = OpenMode.NewLocal; break;
            case "detach": request.Mode = OpenMode.Detach; break;
            case "plain": request.Mode = OpenMode.Plain; break;
            default: request.Errors.Add("mode must be newLocal, detach or plain."); break;
        }

        JToken(arguments, "worksets", out var worksets);
        if (worksets is Newtonsoft.Json.Linq.JArray)
        {
            request.Worksets = WorksetChoice.Named;
            request.WorksetNames = PlanArgs.Strings(worksets);
            if (request.WorksetNames.Count == 0) request.Worksets = WorksetChoice.CloseAll;
        }
        else
        {
            switch (PlanArgs.String(worksets).Trim().ToLowerInvariant())
            {
                case "": case "all": request.Worksets = WorksetChoice.All; break;
                case "editable": request.Worksets = WorksetChoice.Editable; break;
                case "lastviewed": case "last_viewed": request.Worksets = WorksetChoice.LastViewed; break;
                case "closeall": case "close_all": case "none": request.Worksets = WorksetChoice.CloseAll; break;
                default:
                    // A single name, or several separated by commas.
                    request.Worksets = WorksetChoice.Named;
                    request.WorksetNames = PlanArgs.String(worksets).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    break;
            }
        }

        JToken(arguments, "localFolder", out var folder);
        var localFolder = PlanArgs.String(folder).Trim();
        request.LocalFolder = localFolder.Length > 0 ? localFolder : null;
        JToken(arguments, "audit", out var audit);
        request.Audit = PlanArgs.Bool(audit, false);
        JToken(arguments, "activate", out var activate);
        request.Activate = PlanArgs.Bool(activate, false);
        return request;
    }

    /// <summary>The mode that applies to a file: Auto becomes NewLocal for a central, Plain otherwise.</summary>
    public static OpenMode Effective(OpenMode requested, bool isWorkshared, bool isCentral, out string? error)
    {
        error = null;
        switch (requested)
        {
            case OpenMode.Auto:
                return isWorkshared && isCentral ? OpenMode.NewLocal : OpenMode.Plain;
            case OpenMode.NewLocal when !isWorkshared || !isCentral:
                error = "mode=newLocal needs a workshared central model; this file is " + (isWorkshared ? "a local copy, not the central" : "not workshared") + ".";
                return requested;
            case OpenMode.Detach when !isWorkshared:
                error = "mode=detach needs a workshared model; open it with mode=plain.";
                return requested;
            default:
                return requested;
        }
    }

    /// <summary>Which of the model's user worksets to open for a choice; null means the Revit option handles it.</summary>
    public static List<int>? WorksetsToOpen(OpenRequest request, IReadOnlyList<(int Id, string Name, string Owner)> available, string username, out List<string> unknown)
    {
        unknown = new List<string>();
        switch (request.Worksets)
        {
            case WorksetChoice.Editable:
                // Not borrowed by anyone else: the agent can edit what is in them.
                return available.Where(w => string.IsNullOrEmpty(w.Owner) || string.Equals(w.Owner, username, StringComparison.OrdinalIgnoreCase))
                                .Select(w => w.Id).ToList();
            case WorksetChoice.Named:
                var ids = new List<int>();
                foreach (var name in request.WorksetNames)
                {
                    var match = available.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match.Name == null) unknown.Add(name);
                    else ids.Add(match.Id);
                }
                return ids;
            default:
                return null;
        }
    }

    private static void JToken(IDictionary<string, object?> arguments, string key, out Newtonsoft.Json.Linq.JToken? token)
        => token = arguments.TryGetValue(key, out var value) ? PlanArgs.Token(value) : null;
}
