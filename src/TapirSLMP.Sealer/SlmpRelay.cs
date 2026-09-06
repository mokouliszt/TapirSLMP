using TapirSLMP.Contracts;
using TapirSLMP.Protocol;

namespace TapirSLMP.Sealer;

/// <summary>
/// Submits an SLMP request frame to the freezer and waits for the correlated response.
/// This is the embeddable entry point: an application can call it directly instead of
/// routing its own traffic through the TCP or UDP listener.
/// </summary>
public interface ISlmpRelay
{
    Task<byte[]> RelayAsync(
        byte[] rawRequest,
        string routeKey,
        SlmpTransportKind transport,
        CancellationToken cancellationToken);
}

public sealed class SlmpRelay(
    IJobSubmissionGateway freezer,
    SealerOptions options,
    TimeProvider timeProvider) : ISlmpRelay
{
    public async Task<byte[]> RelayAsync(
        byte[] rawRequest,
        string routeKey,
        SlmpTransportKind transport,
        CancellationToken cancellationToken)
    {
        var parseStatus = SlmpFrameCodec.TryParseRequest(
            rawRequest,
            out var request,
            out var consumed,
            out var error);
        if (parseStatus != SlmpFrameParseStatus.Complete ||
            request is null ||
            consumed != rawRequest.Length)
        {
            throw new InvalidDataException(error ?? "Expected exactly one binary SLMP request frame.");
        }

        var deadline = timeProvider.GetUtcNow() + options.JobTimeout;
        var clientRequestId = $"req-{Guid.CreateVersion7():N}";
        var enqueueRequest = new EnqueueJobRequest(
            clientRequestId,
            routeKey,
            transport,
            rawRequest,
            deadline);
        var submitted = await SubmitIdempotentlyAsync(
            enqueueRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);

        while (timeProvider.GetUtcNow() < deadline)
        {
            JobView? job;
            try
            {
                job = await freezer.GetAsync(submitted.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                await DelayBeforeRetryAsync(deadline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (job is null)
            {
                throw new InvalidOperationException("Freezer no longer contains the submitted job.");
            }
            switch (job.Status)
            {
                case JobStatus.Completed when job.ResponseFrame is not null:
                    return job.ResponseFrame;
                case JobStatus.Completed:
                    throw new InvalidDataException("Completed freezer job has no response frame.");
                case JobStatus.Failed:
                case JobStatus.Expired:
                case JobStatus.OutcomeUnknown:
                    throw new InvalidOperationException(
                        $"Freezer job ended as {job.Status}: {job.Error ?? "no detail"}");
            }

            await Task.Delay(options.PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the liberator response.");
    }

    private async Task<EnqueueJobResponse> SubmitIdempotentlyAsync(
        EnqueueJobRequest request,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var expectedJobId = JobIdentifiers.FromClientRequestId(request.ClientRequestId);
        while (timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                return await freezer.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                try
                {
                    var existing = await freezer.GetAsync(expectedJobId, cancellationToken).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        return new EnqueueJobResponse(expectedJobId, existing.CreatedAt, Replayed: true);
                    }
                }
                catch (Exception lookupException) when (IsTransient(lookupException, cancellationToken))
                {
                }

                await DelayBeforeRetryAsync(deadline, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("Timed out submitting the SLMP request to the freezer.");
    }

    private async Task DelayBeforeRetryAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        var delay = remaining < options.PollInterval ? remaining : options.PollInterval;
        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        {
            return true;
        }

        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is JobGatewayException gatewayException)
        {
            return gatewayException.IsTransient;
        }

        return false;
    }
}
