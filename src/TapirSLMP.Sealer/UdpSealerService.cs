using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using TapirSLMP.Contracts;
using TapirSLMP.Protocol;

namespace TapirSLMP.Sealer;

public sealed class UdpSealerService(
    ISlmpRelay relay,
    SealerOptions options,
    SealerListener listener,
    ILogger<UdpSealerService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(options.MaximumConcurrentRequests);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!listener.ListensOn(SlmpTransportKind.Udp))
        {
            return;
        }

        using var udp = new UdpClient(new IPEndPoint(listener.ListenAddress, listener.Port));
        var requests = new ConcurrentDictionary<long, Task>();
        var sequence = 0L;
        logger.LogInformation(
            "UDP sealer listening on {Address}:{Port} for route {RouteKey}.",
            listener.ListenAddress,
            listener.Port,
            listener.RouteKey);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var datagram = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                var key = Interlocked.Increment(ref sequence);
                var task = HandleDatagramAndReleaseAsync(udp, datagram, stoppingToken);
                requests[key] = task;
                _ = task.ContinueWith(
                    completed =>
                    {
                        requests.TryRemove(key, out _);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(requests.Values).ConfigureAwait(false);
        }
    }

    private async Task HandleDatagramAndReleaseAsync(
        UdpClient udp,
        UdpReceiveResult datagram,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleDatagramAsync(udp, datagram, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task HandleDatagramAsync(
        UdpClient udp,
        UdpReceiveResult datagram,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] response;
            try
            {
                response = await relay.RelayAsync(
                    datagram.Buffer,
                    listener.RouteKey,
                    SlmpTransportKind.Udp,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "UDP SLMP relay failed for {RemoteAddress}.",
                    datagram.RemoteEndPoint.Address);
                var status = SlmpFrameCodec.TryParseRequest(datagram.Buffer, out var request, out _, out _);
                if (status != SlmpFrameParseStatus.Complete || request is null)
                {
                    return;
                }

                response = SlmpFrameCodec.BuildErrorResponse(request, 0xCEE0);
            }

            await udp.SendAsync(response, datagram.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException or ObjectDisposedException)
        {
            logger.LogWarning(exception, "Failed to return a UDP SLMP response.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        _concurrency.Dispose();
        base.Dispose();
    }
}
