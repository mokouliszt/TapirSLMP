using TapirSLMP.Contracts;

namespace TapirSLMP.Storage;

/// <summary>
/// Talks to a job store directly, with no HTTP hop. Intended for single-trust-zone
/// deployments where the sealer, liberator, and store live in one process.
/// It applies the same <see cref="JobAdmissionPolicy"/> as the HTTP endpoints, so the
/// read-only allow-list and TTL clamping hold on this path too. It does NOT apply
/// bearer-token authentication: there is no network boundary to authenticate across.
/// </summary>
public sealed class InProcessJobGateway(
    IJobStore store,
    JobAdmissionPolicy policy,
    TimeProvider timeProvider) : IJobSubmissionGateway, IJobWorkerGateway
{
    public async Task<EnqueueJobResponse> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!policy.TryAdmit(request, now, out var admitted, out var rejection))
        {
            throw rejection!.ToException();
        }

        var job = new NewJob(
            admitted!.Id,
            admitted.RouteKey,
            admitted.Transport,
            admitted.Risk,
            admitted.Command,
            admitted.Subcommand,
            admitted.RequestFrame,
            admitted.CreatedAt,
            admitted.ExpiresAt);

        if (await store.TryAddAsync(job, cancellationToken).ConfigureAwait(false))
        {
            return new EnqueueJobResponse(job.Id, now);
        }

        var existing = await store.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null ||
            !string.Equals(existing.RouteKey, job.RouteKey, StringComparison.Ordinal) ||
            existing.Transport != job.Transport ||
            existing.Risk != job.Risk ||
            existing.Command != job.Command ||
            existing.Subcommand != job.Subcommand ||
            (request.ExpiresAt is not null && existing.ExpiresAt != job.ExpiresAt) ||
            !existing.RequestFrame.AsSpan().SequenceEqual(job.RequestFrame))
        {
            throw new JobGatewayException(
                "idempotency_conflict",
                "Client request id is already associated with different request data.");
        }

        return new EnqueueJobResponse(job.Id, existing.CreatedAt, Replayed: true);
    }

    public async Task<JobView?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var job = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return job?.ToView();
    }

    public async Task<LeaseView?> AcquireAsync(
        AcquireLeaseRequest request,
        CancellationToken cancellationToken)
    {
        var rejection = policy.ValidateLeaseRequest(request);
        if (rejection is not null)
        {
            throw rejection.ToException();
        }

        var lease = await store.TryAcquireAsync(
            request.WorkerId,
            request.RouteKeys.ToHashSet(StringComparer.Ordinal),
            TimeSpan.FromSeconds(request.LeaseSeconds),
            policy.Options.MaximumAttempts,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return lease is null
            ? null
            : new LeaseView(lease.LeaseToken, lease.LeaseUntil, lease.Job.ToView());
    }

    public async Task MarkExecutingAsync(
        string id,
        LeaseActionRequest request,
        CancellationToken cancellationToken)
    {
        Ensure(JobAdmissionPolicy.ValidateLeaseIdentity(request.WorkerId, request.LeaseToken));
        var result = await store.MarkExecutingAsync(
            id,
            request.WorkerId,
            request.LeaseToken,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        EnsureMutation(id, result);
    }

    public async Task CompleteAsync(
        string id,
        CompleteJobRequest request,
        CancellationToken cancellationToken)
    {
        Ensure(JobAdmissionPolicy.ValidateLeaseIdentity(request.WorkerId, request.LeaseToken));

        var job = await store.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new JobGatewayException("not_found", $"Job '{id}' does not exist.");
        Ensure(JobAdmissionPolicy.ValidateResponseFrame(job.RequestFrame, request.ResponseFrame));

        var result = await store.CompleteAsync(
            id,
            request.WorkerId,
            request.LeaseToken,
            request.ResponseFrame,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        EnsureMutation(id, result);
    }

    public async Task FailAsync(
        string id,
        FailJobRequest request,
        CancellationToken cancellationToken)
    {
        Ensure(JobAdmissionPolicy.ValidateFailure(request));
        var result = await store.FailAsync(
            id,
            request.WorkerId,
            request.LeaseToken,
            request.Disposition,
            request.Error,
            policy.Options.MaximumAttempts,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        EnsureMutation(id, result);
    }

    private static void Ensure(PolicyRejection? rejection)
    {
        if (rejection is not null)
        {
            throw rejection.ToException();
        }
    }

    private static void EnsureMutation(string id, StoreMutationResult result)
    {
        switch (result)
        {
            case StoreMutationResult.Success:
                return;
            case StoreMutationResult.NotFound:
                throw new JobGatewayException("not_found", $"Job '{id}' does not exist.");
            default:
                throw new JobGatewayException(
                    "lease_conflict",
                    "Lease is stale or job state is incompatible.");
        }
    }
}
