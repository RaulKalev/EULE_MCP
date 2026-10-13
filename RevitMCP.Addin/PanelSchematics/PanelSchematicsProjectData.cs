using System.Globalization;
using Autodesk.Revit.DB;
using Contract = RevitMCP.Addin.PanelSchematics.PanelSchematicsContract;
using StoreState = RevitMCP.Addin.PanelSchematics.PanelSchematicsStorage.StoreState;

namespace RevitMCP.Addin.PanelSchematics;

/// <summary>
/// The plugin's per-panel data kept on Project Information, read in one go: title-page metadata, Üheaegsustegur
/// and Pingelang, cable-length overrides, orientations, colours, the main panel and the panel-info links.
/// For the two stores that have a legacy 32-bit copy, the effective values are the V2 store once it exists and
/// the legacy store before that, exactly as the plugin reads them.
/// </summary>
internal sealed class PanelSchematicsProjectData
{
    public StoreState Metadata { get; private set; } = new();
    public StoreState Overview { get; private set; } = new();
    public StoreState OverviewLegacy { get; private set; } = new();
    public StoreState CableLengths { get; private set; } = new();
    public StoreState Orientations { get; private set; } = new();
    public StoreState Colors { get; private set; } = new();
    public StoreState MainPanel { get; private set; } = new();
    public StoreState InfoLinks { get; private set; } = new();
    public StoreState InfoLinksLegacy { get; private set; } = new();

    public const string FactorsField = "Uheaegsustegurid";
    public const string DropsField = "Pingelangud";
    public const string OverridesField = "CableLengthOverrides";
    public const string OriginalsField = "CableLengthOriginals";
    public const string OrientationsField = "Orientations";
    public const string MetadataField = "PanelMetadata";
    public const string ColorsField = "PanelColorConfigJson";
    public const string MainPanelField = "MainPanelId";
    public const string LinksField = "Links";

    public static PanelSchematicsProjectData Read(Document doc)
    {
        var host = doc.ProjectInformation;
        return new PanelSchematicsProjectData
        {
            Metadata = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.MetadataKey)),
            Overview = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.OverviewValuesKey)),
            OverviewLegacy = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.OverviewValuesLegacyKey)),
            CableLengths = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.CableLengthKey)),
            Orientations = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.OrientationsKey)),
            Colors = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.ColorsKey)),
            MainPanel = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.MainPanelKey)),
            InfoLinks = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.InfoLinksKey)),
            InfoLinksLegacy = PanelSchematicsStorage.Read(host, Contract.GetStore(Contract.InfoLinksLegacyKey)),
        };
    }

    public IEnumerable<(string Key, StoreState State)> All => new[]
    {
        (Contract.MetadataKey, Metadata), (Contract.OverviewValuesKey, Overview), (Contract.OverviewValuesLegacyKey, OverviewLegacy),
        (Contract.CableLengthKey, CableLengths), (Contract.OrientationsKey, Orientations), (Contract.ColorsKey, Colors),
        (Contract.MainPanelKey, MainPanel), (Contract.InfoLinksKey, InfoLinks), (Contract.InfoLinksLegacyKey, InfoLinksLegacy)
    };

    public List<string> Problems => All.Where(s => !s.State.Readable).Select(s => s.State.Problem ?? s.Key + " is unreadable.").ToList();

    /// <summary>Üheaegsustegur by panel id: V2 once it exists, else the legacy store.</summary>
    public Dictionary<string, double> Factors => (Overview.Exists ? Overview : OverviewLegacy).NumberMap(FactorsField);

    public Dictionary<string, double> Drops => (Overview.Exists ? Overview : OverviewLegacy).NumberMap(DropsField);

    public Dictionary<string, string> Links => (InfoLinks.Exists ? InfoLinks : InfoLinksLegacy).TextMap(LinksField);

    public string MainPanelId => (MainPanel.Text(MainPanelField) ?? string.Empty).Trim();

    public static string Key(ElementId id) => id.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Fingerprint of everything on Project Information, for the preview-then-write check.</summary>
    public string Token()
    {
        var parts = new List<string?>();
        foreach (var (key, state) in All)
        {
            parts.Add(key + (state.Exists ? "+" : "-"));
            foreach (var text in state.Texts.OrderBy(t => t.Key, StringComparer.Ordinal))
                parts.Add(text.Key + "=" + text.Value);
            foreach (var map in state.TextMaps.OrderBy(m => m.Key, StringComparer.Ordinal))
                parts.Add(map.Key + "=" + string.Join("|", map.Value.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value)));
            foreach (var map in state.NumberMaps.OrderBy(m => m.Key, StringComparer.Ordinal))
                parts.Add(map.Key + "=" + string.Join("|", map.Value.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key + ":" + p.Value.ToString("R", CultureInfo.InvariantCulture))));
        }
        return EmptyRowsEditor.StateToken(parts.ToArray());
    }
}
