using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>Select and zoom to elements in the active Revit view. Built only from a flyer.</summary>
public sealed class VillageShowRequest
{
    /// <summary>The model the flyer was captured in; the add-in refuses to select in any other document.</summary>
    public string ModelId { get; set; } = string.Empty;

    public IReadOnlyList<long> ElementIds { get; set; } = Array.Empty<long>();
}

public static class VillageShowStatus
{
    public const string Shown = "shown";
    public const string NotFound = "not_found";
    public const string DifferentModel = "different_model";
    public const string NoDocument = "no_document";
    public const string Busy = "busy";
    public const string Unavailable = "unavailable";
    public const string BadRequest = "bad_request";
    public const string Error = "error";
}

/// <summary>What <c>POST /show</c> answers.</summary>
public sealed class VillageShowResult
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = VillageShowStatus.Error;
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
    [JsonProperty("selected")] public int Selected { get; set; }
    [JsonProperty("missing")] public int Missing { get; set; }

    public static VillageShowResult Fail(string status, string message) => new() { Ok = false, Status = status, Message = message };
}

/// <summary>
/// The only things a viewer can ask the connector to do: list and open flyers, archive, unarchive
/// or dismiss one, and select a flyer's elements in Revit. Nothing here reaches MCP, the agent or
/// the tool registry, and no action can change the model. No Revit API dependency.
/// </summary>
public interface IVillageActions
{
    /// <summary><c>GET /flyers</c> body: a <see cref="VillageFlyerBoardView"/>.</summary>
    string BoardJson();

    /// <summary><c>GET /flyers/{id}</c> body, or null when there is no such flyer.</summary>
    string? FlyerJson(string id);

    /// <summary>Archive, unarchive or dismiss. False when the flyer does not exist.</summary>
    bool Apply(string id, string action);

    /// <summary>Selects the flyer's elements (all, or the subset given) in the active Revit document.</summary>
    Task<VillageShowResult> ShowAsync(string flyerId, IReadOnlyList<long>? elementIds, CancellationToken ct);
}

/// <summary>
/// <see cref="IVillageActions"/> over a <see cref="VillageFlyerBoard"/> and an optional Revit
/// handler supplied by the add-in. Show only ever selects ids that are on the flyer, in the model
/// the flyer was captured in, so the page can point Revit at nothing the agent did not already
/// retrieve.
/// </summary>
public sealed class VillageFlyerActions : IVillageActions
{
    public const int MaxShowIds = 5000;
    public const int ShowTimeoutMs = 20000;

    private readonly VillageFlyerBoard _board;
    private readonly bool _enabled;
    private readonly Func<Func<VillageShowRequest, CancellationToken, Task<VillageShowResult>>?> _show;

    public VillageFlyerActions(
        VillageFlyerBoard board,
        Func<Func<VillageShowRequest, CancellationToken, Task<VillageShowResult>>?>? showProvider = null,
        bool enabled = true)
    {
        _board = board;
        _enabled = enabled;
        _show = showProvider ?? (() => null);
    }

    public bool ShowAvailable
    {
        get { try { return _enabled && _show() != null; } catch { return false; } }
    }

    public VillageFlyerBoardView View() => new()
    {
        Enabled = _enabled,
        ShowAvailable = ShowAvailable,
        Flyers = _enabled ? _board.Summaries() : new List<VillageFlyer>()
    };

    public string BoardJson() => JsonConvert.SerializeObject(View());

    public string? FlyerJson(string id)
    {
        if (!_enabled || !VillageFlyerBoard.IsValidId(id)) return null;
        var flyer = _board.Get(id);
        return flyer == null ? null : JsonConvert.SerializeObject(flyer);
    }

    public bool Apply(string id, string action) =>
        _enabled && VillageFlyerBoard.IsValidId(id) && _board.Apply(id, action);

    public async Task<VillageShowResult> ShowAsync(string flyerId, IReadOnlyList<long>? elementIds, CancellationToken ct)
    {
        if (!_enabled) return VillageShowResult.Fail(VillageShowStatus.Unavailable, "Flyers are turned off.");
        var show = _show();
        if (show == null) return VillageShowResult.Fail(VillageShowStatus.Unavailable, "Show in Revit is not available in this session.");
        if (!VillageFlyerBoard.IsValidId(flyerId)) return VillageShowResult.Fail(VillageShowStatus.BadRequest, "Unknown flyer.");

        var flyer = _board.Get(flyerId);
        if (flyer == null) return VillageShowResult.Fail(VillageShowStatus.NotFound, "That flyer is no longer on the board.");

        var onFlyer = new HashSet<long>((flyer.Items ?? new List<VillageFlyerItem>()).Select(i => i.Id));
        List<long> ids;
        if (elementIds == null || elementIds.Count == 0)
            ids = onFlyer.ToList();
        else
            ids = elementIds.Where(onFlyer.Contains).Distinct().ToList();

        if (ids.Count == 0) return VillageShowResult.Fail(VillageShowStatus.BadRequest, "None of those elements are on this flyer.");
        if (ids.Count > MaxShowIds) return VillageShowResult.Fail(VillageShowStatus.BadRequest, $"At most {MaxShowIds} elements can be shown at once; filter the flyer first.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ShowTimeoutMs);
        try
        {
            var task = show(new VillageShowRequest { ModelId = flyer.ModelId, ElementIds = ids }, timeout.Token);
            var finished = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
            if (finished != task)
                return VillageShowResult.Fail(VillageShowStatus.Busy, "Revit did not respond. Close any open dialog in Revit and try again.");
            return await task.ConfigureAwait(false) ?? VillageShowResult.Fail(VillageShowStatus.Error, "Revit returned no result.");
        }
        catch (OperationCanceledException)
        {
            return VillageShowResult.Fail(VillageShowStatus.Busy, "Revit did not respond. Close any open dialog in Revit and try again.");
        }
        catch (Exception ex)
        {
            return VillageShowResult.Fail(VillageShowStatus.Error, "Show in Revit failed: " + VillageEventSerializer.Truncate(ex.Message));
        }
        finally
        {
            // Releases the watchdog delay once the handler has answered.
            try { timeout.Cancel(); } catch { }
        }
    }
}
