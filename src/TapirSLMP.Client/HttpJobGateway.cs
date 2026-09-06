using TapirSLMP.Contracts;

namespace TapirSLMP.Client;

/// <summary>
/// Job gateway backed by the freezer's HTTP API. Use this when the sealer or liberator
/// runs in a different network zone from the freezer.
/// </summary>
public sealed class HttpJobGateway(FreezerClient client) : IJobSubmissionGateway, IJobWorkerGateway
{
    public Task<EnqueueJobResponse> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken) =>
        client.EnqueueAsync(request, cancellationToken);

    public Task<JobView?> GetAsync(string id, CancellationToken cancellationToken) =>
        client.GetAsync(id, cancellationToken);

    public Task<LeaseView?> AcquireAsync(
        AcquireLeaseRequest request,
        CancellationToken cancellationToken) =>
        client.AcquireAsync(request, cancellationToken);

    public Task MarkExecutingAsync(
        string id,
        LeaseActionRequest request,
        CancellationToken cancellationToken) =>
        client.MarkExecutingAsync(id, request, cancellationToken);

    public Task CompleteAsync(
        string id,
        CompleteJobRequest request,
        CancellationToken cancellationToken) =>
        client.CompleteAsync(id, request, cancellationToken);

    public Task FailAsync(
        string id,
        FailJobRequest request,
        CancellationToken cancellationToken) =>
        client.FailAsync(id, request, cancellationToken);
}
