namespace TapirSLMP.Contracts;

/// <summary>
/// Submission side of the freezer, used by a sealer. Implemented over HTTP by
/// <c>TapirSLMP.Client</c> and directly over a job store by <c>TapirSLMP.Storage</c>.
/// </summary>
public interface IJobSubmissionGateway
{
    Task<EnqueueJobResponse> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken);

    Task<JobView?> GetAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// Worker side of the freezer, used by a liberator.
/// </summary>
public interface IJobWorkerGateway
{
    Task<LeaseView?> AcquireAsync(
        AcquireLeaseRequest request,
        CancellationToken cancellationToken);

    Task MarkExecutingAsync(
        string id,
        LeaseActionRequest request,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        string id,
        CompleteJobRequest request,
        CancellationToken cancellationToken);

    Task FailAsync(
        string id,
        FailJobRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Failure raised by a job gateway. <see cref="IsTransient"/> tells a caller whether
/// retrying the same operation is meaningful.
/// </summary>
public class JobGatewayException : Exception
{
    public JobGatewayException(string code, string message, bool isTransient = false)
        : base(message)
    {
        Code = code;
        IsTransient = isTransient;
    }

    public JobGatewayException(string code, string message, bool isTransient, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        IsTransient = isTransient;
    }

    public string Code { get; }

    public bool IsTransient { get; }
}
