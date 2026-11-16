using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Pin/unpin and flip facing/hand (#162, docs/element-orientation.md).</summary>
internal sealed partial class RevitMcpTools
{
    [McpServerTool(Name = "revit_preview_set_pin_state", ReadOnly = true),
     Description("Previews pinning or unpinning elements WITHOUT changing the model. Same arguments as revit_set_pin_state. Per element: name, category, current pin state, status (ready, noOp when already in the wanted state, grouped, ownedByOther, notAnInstance, notFound) and the reason. Counts plus up to sampleSize elements, blocked ones first.")]
    public async Task<string> PreviewSetPinState(
        [Description("pinned=true pins, pinned=false unpins.")] bool pinned,
        [Description("Element ids.")] long[]? elementIds = null,
        [Description("Use the current selection instead of elementIds.")] bool useSelection = false,
        [Description("true: change nothing unless every non-noOp element can change. Default false: change what can change.")] bool atomic = false,
        [Description("Elements to return in the result (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        FormatResult(await pipeClient.SendAsync("revit_preview_set_pin_state",
            PinArgs(pinned, elementIds, useSelection, atomic, sampleSize, document), cancellationToken));

    [McpServerTool(Name = "revit_set_pin_state"),
     Description("Pins (pinned=true) or unpins (pinned=false) elements, including view-specific ones such as detail items, in ONE transaction and one approval. Requires approval. Target elementIds or useSelection. Elements already in the wanted state are reported as noOp. Group members, elements checked out by someone else, types and unknown ids are reported per element and left alone. atomic=true writes nothing unless every non-noOp element can change. Result: counts and up to sampleSize elements. Run revit_preview_set_pin_state first.")]
    public async Task<string> SetPinState(
        [Description("pinned=true pins, pinned=false unpins.")] bool pinned,
        [Description("Element ids.")] long[]? elementIds = null,
        [Description("Use the current selection instead of elementIds.")] bool useSelection = false,
        [Description("true: change nothing unless every non-noOp element can change. Default false: change what can change.")] bool atomic = false,
        [Description("Elements to return in the result (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        FormatResult(await pipeClient.SendAsync("revit_set_pin_state",
            PinArgs(pinned, elementIds, useSelection, atomic, sampleSize, document), cancellationToken));

    [McpServerTool(Name = "revit_preview_flip_family_instances", ReadOnly = true),
     Description("Previews flipping the facing and/or hand of family instances WITHOUT changing the model. Same arguments as revit_flip_family_instances. Per element: name, category, host, pinned, canFlipFacing, canFlipHand, current facingFlipped/handFlipped, status (ready, pinned, grouped, ownedByOther, cannotFlip, notFamilyInstance, notFound) and the reason.")]
    public async Task<string> PreviewFlipFamilyInstances(
        [Description("Flip the facing (the FacingOrientation vector).")] bool flipFacing = false,
        [Description("Flip the hand (the HandOrientation vector).")] bool flipHand = false,
        [Description("Element ids.")] long[]? elementIds = null,
        [Description("Use the current selection instead of elementIds.")] bool useSelection = false,
        [Description("Allow unpinning a pinned element to flip it and pin it again. Default false: pinned elements are reported, never unpinned.")] bool unpinToFlip = false,
        [Description("true: change nothing unless every flippable element succeeds. Default false: report failures per element.")] bool atomic = false,
        [Description("Elements to return in the result (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        FormatResult(await pipeClient.SendAsync("revit_preview_flip_family_instances",
            FlipArgs(flipFacing, flipHand, elementIds, useSelection, unpinToFlip, atomic, sampleSize, document), cancellationToken));

    [McpServerTool(Name = "revit_flip_family_instances"),
     Description("Flips the facing (flipFacing) and/or hand (flipHand) of family instances with Revit's own flip — not a rotation or a mirror — keeping the host. Requires approval. Target elementIds or useSelection. Needs CanFlipFacing/CanFlipHand; other elements get a precise reason. A pinned element is NOT silently unpinned: pass unpinToFlip=true to unpin, flip and re-pin it. Each flip is verified (FacingFlipped/HandFlipped changed, host unchanged, location within 0.5 mm; otherwise undone and reported). atomic=true undoes everything if one flip fails. One transaction, one undo. Run revit_preview_flip_family_instances first.")]
    public async Task<string> FlipFamilyInstances(
        [Description("Flip the facing (the FacingOrientation vector).")] bool flipFacing = false,
        [Description("Flip the hand (the HandOrientation vector).")] bool flipHand = false,
        [Description("Element ids.")] long[]? elementIds = null,
        [Description("Use the current selection instead of elementIds.")] bool useSelection = false,
        [Description("Allow unpinning a pinned element to flip it and pin it again. Default false: pinned elements are reported, never unpinned.")] bool unpinToFlip = false,
        [Description("true: change nothing unless every flippable element succeeds. Default false: report failures per element.")] bool atomic = false,
        [Description("Elements to return in the result (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        FormatResult(await pipeClient.SendAsync("revit_flip_family_instances",
            FlipArgs(flipFacing, flipHand, elementIds, useSelection, unpinToFlip, atomic, sampleSize, document), cancellationToken));

    private static Dictionary<string, object?> PinArgs(bool pinned, long[]? elementIds, bool useSelection, bool atomic, int sampleSize, string? document) => new()
    {
        ["pinned"] = pinned,
        ["elementIds"] = elementIds ?? [],
        ["useSelection"] = useSelection,
        ["atomic"] = atomic,
        ["sampleSize"] = sampleSize,
        ["document"] = document ?? string.Empty
    };

    private static Dictionary<string, object?> FlipArgs(bool flipFacing, bool flipHand, long[]? elementIds, bool useSelection,
        bool unpinToFlip, bool atomic, int sampleSize, string? document) => new()
    {
        ["flipFacing"] = flipFacing,
        ["flipHand"] = flipHand,
        ["elementIds"] = elementIds ?? [],
        ["useSelection"] = useSelection,
        ["unpinToFlip"] = unpinToFlip,
        ["atomic"] = atomic,
        ["sampleSize"] = sampleSize,
        ["document"] = document ?? string.Empty
    };
}
