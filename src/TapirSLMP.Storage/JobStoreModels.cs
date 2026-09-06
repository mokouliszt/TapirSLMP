using TapirSLMP.Contracts;

namespace TapirSLMP.Storage;

public sealed record NewJob(
    string Id,
    string RouteKey,
    SlmpTransportKind Transport,
    JobRisk Risk,
    ushort Command,
    ushort Subcommand,
    byte[] RequestFrame,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record StoredJob(
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
    DateTimeOffset? LeaseUntil,
    string? LeaseToken,
    string? WorkerId,
    DateTimeOffset? CompletedAt,
    string? Error)
{
    public JobView ToView() => new(
        Id,
        RouteKey,
        Transport,
        Status,
        Risk,
        Command,
        Subcommand,
        RequestFrame,
        ResponseFrame,
        AttemptCount,
        CreatedAt,
        ExpiresAt,
        CompletedAt,
        Error);
}

public sealed record AcquiredLease(StoredJob Job, string LeaseToken, DateTimeOffset LeaseUntil);

public enum StoreMutationResult
{
    Success,
    NotFound,
    Conflict,
}
