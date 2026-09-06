using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using TapirSLMP.Contracts;
using TapirSLMP.Protocol;

namespace TapirSLMP.Sealer;

public sealed class TcpSealerService(
    ISlmpRelay relay,
    SealerOptions options,
    SealerListener listener,
    ILogger<TcpSealerService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(options.MaximumConcurrentRequests);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!listener.ListensOn(SlmpTransportKind.Tcp))
        {
            return;
        }

        var socket = new TcpListener(listener.ListenAddress, listener.Port);
        socket.Start();
        logger.LogInformation(
            "TCP sealer listening on {Address}:{Port} for route {RouteKey}.",
            listener.ListenAddress,
            listener.Port,
            listener.RouteKey);
        var clients = new HashSet<Task>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await socket.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                var task = HandleClientAndReleaseAsync(client, stoppingToken);
                lock (clients)
                {
                    clients.Add(task);
                }
                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (clients)
                        {
                            clients.Remove(completed);
                        }
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
            socket.Stop();
            Task[] remaining;
            lock (clients)
            {
                remaining = clients.ToArray();
            }
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAndReleaseAsync(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            await HandleClientAsync(client, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            try
            {
                using var stream = client.GetStream();
                while (!stoppingToken.IsCancellationRequested)
                {
                    byte[] requestBytes;
                    try
                    {
                        requestBytes = await SlmpStreamReader.ReadRequestAsync(
                            stream,
                            options.FrameReceiveTimeout,
                            stoppingToken).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException)
                    {
                        return;
                    }

                    var response = await RelayOrErrorAsync(
                        requestBytes,
                        SlmpTransportKind.Tcp,
                        remote,
                        stoppingToken).ConfigureAwait(false);
                    await stream.WriteAsync(response, stoppingToken).ConfigureAwait(false);
                    await stream.FlushAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or InvalidDataException or ObjectDisposedException)
            {
                logger.LogWarning(exception, "TCP sealer connection ended for {RemoteAddress}.", remote?.Address);
            }
        }
    }

    private async Task<byte[]> RelayOrErrorAsync(
        byte[] requestBytes,
        SlmpTransportKind transport,
        IPEndPoint? remote,
        CancellationToken cancellationToken)
    {
        try
        {
            return await relay.RelayAsync(requestBytes, listener.RouteKey, transport, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SLMP relay failed for {RemoteAddress}.", remote?.Address);
            var status = SlmpFrameCodec.TryParseRequest(requestBytes, out var request, out _, out _);
            if (status == SlmpFrameParseStatus.Complete && request is not null)
            {
                return SlmpFrameCodec.BuildErrorResponse(request, 0xCEE0);
            }

            throw;
        }
    }

    public override void Dispose()
    {
        _concurrency.Dispose();
        base.Dispose();
    }
}
