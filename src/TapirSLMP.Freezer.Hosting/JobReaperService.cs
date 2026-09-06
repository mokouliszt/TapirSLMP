using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TapirSLMP.Storage;

namespace TapirSLMP.Freezer.Hosting;

public sealed class JobReaperService(
    IJobStore store,
    FreezerOptions options,
    TimeProvider timeProvider,
    ILogger<JobReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), timeProvider);
        var nextPrune = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                await store.SweepAsync(
                    options.MaximumAttempts,
                    now,
                    stoppingToken).ConfigureAwait(false);
                if (now >= nextPrune)
                {
                    await store.PruneAsync(
                        now.AddHours(-options.TerminalRetentionHours),
                        stoppingToken).ConfigureAwait(false);
                    nextPrune = now.AddMinutes(10);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to sweep expired freezer jobs.");
            }
        }
    }
}
