namespace TapirSLMP.Protocol;

public enum SlmpFrameFormat
{
    Binary3E = 3,
    Binary4E = 4,
}

public enum SlmpFrameParseStatus
{
    NeedMoreData,
    Complete,
    Invalid,
}

public enum SlmpCommandRisk
{
    ReadOnly,
    StateChanging,
}

public readonly record struct SlmpRoute(
    byte NetworkNumber,
    byte PcNumber,
    ushort DestinationModuleIo,
    byte DestinationModuleStation)
{
    public override string ToString() =>
        $"{NetworkNumber:X2}:{PcNumber:X2}:{DestinationModuleIo:X4}:{DestinationModuleStation:X2}";
}

public sealed record SlmpRequestFrame(
    SlmpFrameFormat Format,
    ushort SerialNumber,
    SlmpRoute Route,
    ushort MonitoringTimer,
    ushort Command,
    ushort Subcommand,
    byte[] RawFrame)
{
    public SlmpCommandRisk Risk => SlmpCommandClassifier.Classify(Command);
}

public sealed record SlmpResponseFrame(
    SlmpFrameFormat Format,
    ushort SerialNumber,
    SlmpRoute Route,
    ushort EndCode,
    byte[] RawFrame);

public static class SlmpCommandClassifier
{
    // Intentionally conservative. Anything not known to be a pure read is
    // treated as state-changing so it cannot be retried after an ambiguous send.
    private static readonly HashSet<ushort> ReadOnlyCommands =
    [
        0x0101, // Read type name
        0x0401, // Batch read
        0x0403, // Random read
        0x0406, // Block read
    ];

    public static SlmpCommandRisk Classify(ushort command) =>
        ReadOnlyCommands.Contains(command)
            ? SlmpCommandRisk.ReadOnly
            : SlmpCommandRisk.StateChanging;
}
