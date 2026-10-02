using System.Text.RegularExpressions;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// #59: every bridge tool whose add-in side loads the device code map must let an MCP client pass
/// the map inline (deviceCodes) and forward it over the pipe. The test project does not reference the
/// bridge, so the bridge source is read as text.
/// </summary>
public class BridgeDeviceCodesTests
{
    public static IEnumerable<object[]> DeviceCodeTools() => new[]
    {
        "revit_get_device_codes",
        "revit_preview_ensure_device_types", "revit_ensure_device_types",
        "revit_preview_place_at_wall", "revit_place_at_wall",
        "revit_preview_place_in_room", "revit_place_in_room",
        "revit_preview_assign_room_to_elements", "revit_assign_room_to_elements",
        "revit_check_devices_per_room", "revit_check_device_alignment",
        "revit_check_coverage", "revit_check_fire_alarm"
    }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(DeviceCodeTools))]
    public void Tool_AcceptsAndForwardsInlineDeviceCodes(string tool)
    {
        var body = MethodSource(tool);
        Assert.Matches(new Regex(@"object\?\s+deviceCodes\s*=\s*null"), body);
        // Forwarded directly, or through the shared EnsureTypeArgs / InRoomArgs builders.
        Assert.Matches(new Regex(@"ToJToken\(deviceCodes\)|deviceCodes, projectRoot\)"), body);
    }

    private static string MethodSource(string tool)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "RevitMCP.Bridge", "RevitMcpTools.cs"));
        var start = source.IndexOf($"[McpServerTool(Name = \"{tool}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Bridge tool {tool} not found.");
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        if (end < 0) end = source.IndexOf("\r\n    }\r\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"End of {tool} not found.");
        return source.Substring(start, end - start);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
