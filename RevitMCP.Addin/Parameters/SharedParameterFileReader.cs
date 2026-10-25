using System.IO;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.RoomDevices;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCP.Addin.Parameters;

/// <summary>A definition of a shared parameter file, with what the list tool shows.</summary>
internal sealed class SharedDefinitionRow
{
    public SharedDefinitionInfo Info { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public bool Visible { get; set; } = true;
    public bool UserModifiable { get; set; } = true;
}

/// <summary>Which shared parameter file a call uses, and where that choice came from.</summary>
internal sealed class SharedFileChoice
{
    public string? Path { get; set; }
    /// <summary>argument | project config | user config | company config | Revit setting.</summary>
    public string Source { get; set; } = string.Empty;
    public string? Error { get; set; }
}

/// <summary>
/// Opens shared parameter files for the parameter tools (#102). Revit reads one file at a time, named by
/// Application.SharedParametersFilename, so every call sets it, opens the file, and restores the user's own setting in
/// a finally — after every call, whatever happens. The company file is only written by the create tool when
/// createInFileIfMissing=true; source=project definitions go to a connector-owned scratch file.
/// </summary>
internal static class SharedParameterFileReader
{
    public const string ConfigSection = "parameters";
    public const string ConfigKey = "sharedParameterFile";

    public static string ScratchFilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "Parameters", "RevitMCP-project-parameters.txt");

    /// <summary>
    /// The file to use: the sharedParameterFile argument (checked against the file access policy), else
    /// parameters.sharedParameterFile from the project, user or company config (first that defines it), else Revit's
    /// own setting.
    /// </summary>
    public static SharedFileChoice Choose(RevitApplication app, Document? doc, string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var (full, normalizeError) = FilePathPolicy.NormalizePath(explicitPath);
            var error = normalizeError ?? new FilePathPolicy().ValidateRead(full!);
            return error != null
                ? new SharedFileChoice { Source = "argument", Error = $"sharedParameterFile '{explicitPath}': {error}" }
                : new SharedFileChoice { Path = full, Source = "argument" };
        }

        foreach (var (scope, path) in ConfigCandidates(doc))
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                var (config, _) = new JsonConfigService().Read(path!, createIfMissing: false);
                var value = config?[ConfigSection]?[ConfigKey]?.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                return new SharedFileChoice { Path = Environment.ExpandEnvironmentVariables(value!.Trim()), Source = $"{scope} config ({path})" };
            }
            catch
            {
                // An unreadable config is skipped; the next scope may define the key.
            }
        }

        string? current = null;
        try { current = app.SharedParametersFilename; }
        catch { /* not available */ }
        return string.IsNullOrWhiteSpace(current)
            ? new SharedFileChoice
            {
                Source = "Revit setting",
                Error = "No shared parameter file is set: pass sharedParameterFile, set parameters.sharedParameterFile in the " +
                        "project or company config, or choose one in Revit (Manage > Shared Parameters)."
            }
            : new SharedFileChoice { Path = current, Source = "Revit setting" };
    }

    /// <summary>
    /// Runs <paramref name="body"/> with <paramref name="path"/> as Revit's shared parameter file and restores the
    /// previous setting afterwards. The body gets null when the file does not exist or Revit cannot read it.
    /// </summary>
    public static T WithFile<T>(RevitApplication app, string path, Func<DefinitionFile?, T> body, List<string> warnings)
    {
        string previous = string.Empty;
        try { previous = app.SharedParametersFilename ?? string.Empty; }
        catch { /* unreadable: restored to empty below */ }

        try
        {
            DefinitionFile? file = null;
            if (File.Exists(path))
            {
                app.SharedParametersFilename = path;
                try { file = app.OpenSharedParameterFile(); }
                catch (Exception ex) { warnings.Add($"Revit could not read the shared parameter file '{path}': {ex.Message}"); }
            }
            return body(file);
        }
        finally
        {
            try
            {
                if (!string.Equals(app.SharedParametersFilename ?? string.Empty, previous, StringComparison.OrdinalIgnoreCase))
                    app.SharedParametersFilename = previous;
            }
            catch (Exception ex)
            {
                warnings.Add($"Revit's shared parameter file setting could not be restored to '{previous}': {ex.Message}");
            }
        }
    }

    /// <summary>Creates the connector's scratch file (empty; Revit writes the header on the first definition).</summary>
    public static string? EnsureScratchFile()
    {
        try
        {
            var path = ScratchFilePath;
            if (File.Exists(path)) return null;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return null;
        }
        catch (Exception ex)
        {
            return $"The connector's scratch parameter file could not be created ({ScratchFilePath}): {ex.Message}";
        }
    }

    public static List<SharedDefinitionRow> ReadDefinitions(DefinitionFile file)
    {
        var rows = new List<SharedDefinitionRow>();
        foreach (DefinitionGroup group in file.Groups)
        {
            foreach (Definition definition in group.Definitions)
            {
                if (definition is not ExternalDefinition external) continue;
                rows.Add(new SharedDefinitionRow
                {
                    Info = new SharedDefinitionInfo
                    {
                        Group = group.Name,
                        Name = external.Name,
                        Guid = external.GUID.ToString(),
                        DataTypeId = SafeDataType(external)
                    },
                    Description = Safe(() => external.Description) ?? string.Empty,
                    Visible = Safe(() => (bool?)external.Visible) ?? true,
                    UserModifiable = Safe(() => (bool?)external.UserModifiable) ?? true
                });
            }
        }
        return rows;
    }

    /// <summary>The definition a plan item names, looked up in the open file by GUID, else by group and name.</summary>
    public static ExternalDefinition? Find(DefinitionFile file, SharedDefinitionInfo info)
    {
        foreach (DefinitionGroup group in file.Groups)
            foreach (Definition definition in group.Definitions)
                if (definition is ExternalDefinition external && string.Equals(external.GUID.ToString(), info.Guid, StringComparison.OrdinalIgnoreCase))
                    return external;
        return file.Groups.get_Item(info.Group)?.Definitions.get_Item(info.Name) as ExternalDefinition;
    }

    /// <summary>Creates a definition in <paramref name="groupName"/> (created when missing). Writes the file.</summary>
    public static ExternalDefinition Create(DefinitionFile file, string groupName, string name, ForgeTypeId spec)
    {
        var group = file.Groups.get_Item(groupName) ?? file.Groups.Create(groupName);
        if (group.Definitions.get_Item(name) is ExternalDefinition existing) return existing;
        var options = new ExternalDefinitionCreationOptions(name, spec) { Visible = true };
        return (ExternalDefinition)group.Definitions.Create(options);
    }

    private static string SafeDataType(Definition definition)
    {
        try { return definition.GetDataType()?.TypeId ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }

    private static IEnumerable<(string Scope, string? Path)> ConfigCandidates(Document? doc)
    {
        if (doc != null)
        {
            string? root = null;
            try { root = ProjectConfigLocator.FindProjectRoot(doc, string.Empty); }
            catch { /* no project root */ }
            if (root != null)
                yield return (ConfigPathResolver.ScopeProject, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeProject, root).Path);
        }
        yield return (ConfigPathResolver.ScopeUser, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser).Path);
        yield return (ConfigPathResolver.ScopeCompany, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeCompany).Path);
    }
}
