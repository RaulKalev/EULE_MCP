namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Result of one loop resistance or sounder-line voltage-drop estimate.
/// Numeric values are null when <see cref="Status"/> is <see cref="FireAlarmVoltageDropEvaluator.InsufficientData"/>.
/// </summary>
public sealed class FireAlarmCircuitEstimate
{
    /// <summary>Ok, Warning, Critical or InsufficientData.</summary>
    public string Status { get; set; } = FireAlarmVoltageDropEvaluator.InsufficientData;

    /// <summary>Why the data is insufficient (missing inputs, "; "-joined); null when the estimate was computed.</summary>
    public string? Reason { get; set; }

    /// <summary>Addressable/module loops: length × round-trip resistance.</summary>
    public double? EstimatedLoopResistanceOhm { get; set; }

    /// <summary>Addressable/module loops: estimated resistance as a percentage of the limit.</summary>
    public double? PercentOfLimit { get; set; }

    /// <summary>Sounder lines: number of devices counted as sounders.</summary>
    public int SounderCount { get; set; }

    /// <summary>Sounder lines: sounder count × current per sounder.</summary>
    public double? TotalCurrentMilliAmps { get; set; }

    /// <summary>Sounder lines: total current × length × round-trip resistance.</summary>
    public double? EstimatedVoltageDrop { get; set; }

    /// <summary>Sounder lines: supply voltage − voltage drop.</summary>
    public double? EstimatedEndVoltage { get; set; }
}

/// <summary>
/// Pure status and current decisions for the fire alarm voltage-drop summary and the preset Excel export.
/// A missing circuit, length, cable type or resistance never yields "Ok": the estimate reports
/// <see cref="InsufficientData"/> with a reason. Sounder-line current counts sounders only, not every
/// device of the group (detectors and call points draw no sounder current).
/// No Revit API dependency — safe to unit-test directly.
/// </summary>
public static class FireAlarmVoltageDropEvaluator
{
    public const string Ok = "Ok";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
    public const string InsufficientData = "InsufficientData";

    /// <summary>Margin above the minimum end voltage below which a sounder line is a Warning.</summary>
    public const double SounderVoltageWarningMarginV = 2.0;

    /// <summary>Share of the loop resistance limit above which an addressable loop is a Warning.</summary>
    public const double LoopResistanceWarningShare = 0.8;

    /// <summary>
    /// Lists the inputs a length-based estimate lacks: the circuit, its length, its cable type and a
    /// positive resistance per metre. Returns null when all are present.
    /// </summary>
    public static string? FindMissingCircuitData(bool hasCircuit, double? lengthMeters, string? cableType, double resistanceOhmPerMeter)
    {
        if (!hasCircuit)
            return "The devices are not on an electrical circuit, so there is no length or cable type.";

        var missing = new List<string>();
        if (!(lengthMeters > 0))
            missing.Add("the circuit has no length");
        if (string.IsNullOrWhiteSpace(cableType))
            missing.Add("the circuit has no cable/wire type");
        if (!(resistanceOhmPerMeter > 0))
            missing.Add("no cable resistance per metre (no matching profile and no positive fallback)");
        return missing.Count == 0 ? null : Capitalize(string.Join("; ", missing)) + ".";
    }

    /// <summary>Estimates the round-trip resistance of an addressable or module loop.</summary>
    public static FireAlarmCircuitEstimate EvaluateLoopResistance(
        bool hasCircuit, double? lengthMeters, string? cableType, double resistanceOhmPerMeter, double maxLoopResistanceOhm)
    {
        var reason = FindMissingCircuitData(hasCircuit, lengthMeters, cableType, resistanceOhmPerMeter);
        if (reason == null && !(maxLoopResistanceOhm > 0))
            reason = "No positive loop resistance limit was given.";
        if (reason != null)
            return new FireAlarmCircuitEstimate { Status = InsufficientData, Reason = reason };

        double loopRes = Math.Round(lengthMeters!.Value * resistanceOhmPerMeter, 2);
        return new FireAlarmCircuitEstimate
        {
            Status = loopRes > maxLoopResistanceOhm ? Critical
                : loopRes > maxLoopResistanceOhm * LoopResistanceWarningShare ? Warning : Ok,
            EstimatedLoopResistanceOhm = loopRes,
            PercentOfLimit = Math.Round(loopRes / maxLoopResistanceOhm * 100, 1)
        };
    }

    /// <summary>
    /// Estimates the voltage drop of a conventional sounder line. The current is
    /// <paramref name="sounderCount"/> × <paramref name="sounderCurrentMilliAmps"/>; a line with no
    /// recognised sounder has insufficient data rather than a zero drop.
    /// </summary>
    public static FireAlarmCircuitEstimate EvaluateSounderLine(
        bool hasCircuit, double? lengthMeters, string? cableType, double resistanceOhmPerMeter,
        int sounderCount, double sounderCurrentMilliAmps, double supplyVoltage, double minimumVoltage)
    {
        var reasons = new List<string>();
        var circuitReason = FindMissingCircuitData(hasCircuit, lengthMeters, cableType, resistanceOhmPerMeter);
        if (circuitReason != null) reasons.Add(circuitReason);
        if (sounderCount <= 0)
            reasons.Add("No device on the line is recognised as a sounder (sireen/vilkur/alarmseade), so the sounder current is unknown.");
        if (!(sounderCurrentMilliAmps > 0))
            reasons.Add("No positive sounder current was given.");

        if (reasons.Count > 0)
            return new FireAlarmCircuitEstimate
            {
                Status = InsufficientData,
                Reason = string.Join(" ", reasons),
                SounderCount = Math.Max(0, sounderCount)
            };

        double totalCurrentA = sounderCount * sounderCurrentMilliAmps / 1000.0;
        double voltageDrop = Math.Round(totalCurrentA * lengthMeters!.Value * resistanceOhmPerMeter, 2);
        double endVoltage = Math.Round(supplyVoltage - voltageDrop, 2);
        return new FireAlarmCircuitEstimate
        {
            Status = endVoltage < minimumVoltage ? Critical
                : endVoltage < minimumVoltage + SounderVoltageWarningMarginV ? Warning : Ok,
            SounderCount = sounderCount,
            TotalCurrentMilliAmps = Math.Round(totalCurrentA * 1000, 1),
            EstimatedVoltageDrop = voltageDrop,
            EstimatedEndVoltage = endVoltage
        };
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
