using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TapirSLMP.Contracts;
using TapirSLMP.Protocol;

namespace TapirSLMP.Liberator;

public sealed class LiberatorService(
    IJobWorkerGateway freezer,
    SlmpTargetTransport target,
    LiberatorOptions options,
    TimeProvider timeProvider,
    ILogger<LiberatorService> logger) : BackgroundService
{
    private static readonly TimeSpan PermanentFailureBackoff = TimeSpan.FromSeconds(15);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, options.Parallelism)
            .Select(index => RunWorkerAsync($"{options.WorkerIdPrefix}-{index}", stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(string workerId, CancellationToken stoppingToken)
    {
        var routeKeys = options.Routes.Keys.Order(StringComparer.Ordinal).ToArray();

        // A liberator commonly starts before the freezer it polls. Until the first
        // successful call, treat failures as a warning rather than an error so that an
        // ordinary startup ordering does not fill the log with errors.
        var reachedFreezerOnce = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await freezer.AcquireAsync(
                    new AcquireLeaseRequest(workerId, routeKeys, options.LeaseSeconds),
                    stoppingToken).ConfigureAwait(false);
                if (!reachedFreezerOnce)
                {
                    reachedFreezerOnce = true;
                    logger.LogInformation("Liberator worker {WorkerId} reached the freezer.", workerId);
                }

                if (lease is null)
                {
                    await Task.Delay(options.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessAsync(workerId, lease, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A rejection the freezer will keep making - an unknown route key, a bad
                // token - is a configuration error. Retrying it at the poll interval would
                // flood the log, so back off and let the message stay readable.
                var permanent = exception is JobGatewayException { IsTransient: false };
                if (reachedFreezerOnce)
                {
                    logger.LogError(exception, "Liberator worker {WorkerId} iteration failed.", workerId);
                }
                else
                {
                    logger.LogWarning(
                        exception,
                        "Liberator worker {WorkerId} cannot reach the freezer yet; retrying.",
                        workerId);
                }

                var delay = permanent ? PermanentFailureBackoff : options.PollInterval;
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(
        string workerId,
        LeaseView lease,
        CancellationToken cancellationToken)
    {
        var job = lease.Job;
        if (!options.Routes.TryGetValue(job.RouteKey, out var route))
        {
            await ReportFailureAsync(
                workerId,
                lease,
                FailureDisposition.Failed,
                "No configured PLC route matches the job.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var requiredLifetime = route.ConnectTimeout + route.ResponseTimeout + TimeSpan.FromSeconds(5);
        if (job.ExpiresAt <= timeProvider.GetUtcNow() + requiredLifetime)
        {
            await ReportFailureAsync(
                workerId,
                lease,
                FailureDisposition.Failed,
                "Job does not have enough remaining lifetime for a safe PLC exchange.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var parseStatus = SlmpFrameCodec.TryParseRequest(
            job.RequestFrame,
            out var request,
            out var consumed,
            out var parseError);
        if (parseStatus != SlmpFrameParseStatus.Complete ||
            request is null ||
            consumed != job.RequestFrame.Length)
        {
            await ReportFailureAsync(
                workerId,
                lease,
                FailureDisposition.Failed,
                parseError ?? "Freezer supplied a malformed request frame.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var executionBegan = false;
        try
        {
            var response = await target.ExchangeAsync(
                route,
                job.Transport,
                request,
                async callbackToken =>
                {
                    await freezer.MarkExecutingAsync(
                        job.Id,
                        new LeaseActionRequest(workerId, lease.LeaseToken),
                        callbackToken).ConfigureAwait(false);
                    executionBegan = true;
                },
                cancellationToken).ConfigureAwait(false);

            await freezer.CompleteAsync(
                job.Id,
                new CompleteJobRequest(workerId, lease.LeaseToken, response),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var disposition = executionBegan && job.Risk == JobRisk.StateChanging
                ? FailureDisposition.OutcomeUnknown
                : exception is InvalidDataException or InvalidOperationException
                    ? FailureDisposition.Failed
                    : FailureDisposition.Retry;
            await ReportFailureAsync(
                workerId,
                lease,
                disposition,
                LimitError(exception.Message),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReportFailureAsync(
        string workerId,
        LeaseView lease,
        FailureDisposition disposition,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            await freezer.FailAsync(
                lease.Job.Id,
                new FailJobRequest(workerId, lease.LeaseToken, disposition, LimitError(error)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (JobGatewayException exception)
        {
            logger.LogWarning(
                exception,
                "Could not record failure for freezer job {JobId}; its lease may be stale.",
                lease.Job.Id);
        }
    }

    private static string LimitError(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Unspecified PLC transport failure."
            : value.Length <= 1024 ? value : value[..1024];
}
