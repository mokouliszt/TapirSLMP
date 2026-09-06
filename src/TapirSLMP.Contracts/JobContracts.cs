namespace TapirSLMP.Contracts;

public enum JobStatus
{
    Queued,
    Leased,
    Executing,
    Completed,
    Failed,
    Expired,
    OutcomeUnknown,
}

public enum SlmpTransportKind
{
    Tcp,
    Udp,
}

public enum JobRisk
{
    ReadOnly,
    StateChanging,
}

public enum FailureDisposition
{
    Retry,
    Failed,
    OutcomeUnknown,
}

public sealed record EnqueueJobRequest(
    string ClientRequestId,
    string RouteKey,
    SlmpTransportKind Transport,
    byte[] RequestFrame,
    DateTimeOffset? ExpiresAt = null);

public sealed record EnqueueJobResponse(string Id, DateTimeOffset CreatedAt, bool Replayed = false);

public sealed record JobView(
    string Id,
    string RouteKey,
    SlmpTransportKind Transport,
    JobStatus Status,
    JobRisk Risk,
    ushort Command,
    ushort Subcommand,
    byte[] RequestFrame,
    byte[]? ResponseFrame,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public sealed record AcquireLeaseRequest(
    string WorkerId,
    IReadOnlyList<string> RouteKeys,
    int LeaseSeconds = 30);

public sealed record LeaseView(
    string LeaseToken,
    DateTimeOffset LeaseUntil,
    JobView Job);

public sealed record LeaseActionRequest(string WorkerId, string LeaseToken);

public sealed record CompleteJobRequest(
    string WorkerId,
    string LeaseToken,
    byte[] ResponseFrame);

public sealed record FailJobRequest(
    string WorkerId,
    string LeaseToken,
    FailureDisposition Disposition,
    string Error);

public sealed record ErrorResponse(string Code, string Message);

public static class JobIdentifiers
{
    public static string FromClientRequestId(string clientRequestId) =>
        $"job_{clientRequestId}";
}
