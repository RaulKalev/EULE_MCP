namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>Pure conversion safety and identity rules used by Revit services and tests.</summary>
public static class IfcConversionPolicy
{
    public static bool ShouldWrite(bool dryRun) => !dryRun;

    public static bool IsExactRoomIdentity(
        long candidateLevelId, string? candidateNumber, string? candidateName,
        long roomLevelId, string? roomNumber, string? roomName) =>
        candidateLevelId == roomLevelId &&
        EqualsTrimmed(candidateNumber, roomNumber) &&
        EqualsTrimmed(candidateName, roomName);

    public static bool IsNumberLevelConflict(
        long candidateLevelId, string? candidateNumber,
        long roomLevelId, string? roomNumber) =>
        candidateLevelId == roomLevelId &&
        !string.IsNullOrWhiteSpace(candidateNumber) &&
        EqualsTrimmed(candidateNumber, roomNumber);

    private static bool EqualsTrimmed(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
