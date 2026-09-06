using TapirSLMP.Contracts;

namespace TapirSLMP.Storage;

public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<bool> TryAddAsync(NewJob job, CancellationToken cancellationToken);

    Task<StoredJob?> GetAsync(string id, CancellationToken cancellationToken);

    Task SweepAsync(
        int maximumAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task PruneAsync(
        DateTimeOffset completedBefore,
        CancellationToken cancellationToken);

    Task<AcquiredLease?> TryAcquireAsync(
        string workerId,
        IReadOnlyCollection<string> routeKeys,
        TimeSpan leaseDuration,
        int maximumAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<StoreMutationResult> MarkExecutingAsync(
        string id,
        string workerId,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<StoreMutationResult> CompleteAsync(
        string id,
        string workerId,
        string leaseToken,
        byte[] responseFrame,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<StoreMutationResult> FailAsync(
        string id,
        string workerId,
        string leaseToken,
        FailureDisposition disposition,
        string error,
        int maximumAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
