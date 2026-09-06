using System.Net;
using System.Net.Sockets;
using TapirSLMP.Contracts;
using TapirSLMP.Protocol;

namespace TapirSLMP.Liberator;

public sealed class SlmpTargetTransport
{
    public async Task<byte[]> ExchangeAsync(
        LiberatorRoute route,
        SlmpTransportKind transport,
        SlmpRequestFrame request,
        Func<CancellationToken, Task> beforeSend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(beforeSend);

        if (!route.Allows(transport))
        {
            throw new InvalidOperationException(
                $"Transport {transport} is disabled for route '{route.RouteKey}'.");
        }

        if (route.PinSlmpRoute && request.Route != route.ExpectedSlmpRoute)
        {
            throw new InvalidOperationException(
                $"SLMP route {request.Route} does not match the pinned route {route.ExpectedSlmpRoute}.");
        }

        var endpoint = await ResolveAsync(route, cancellationToken).ConfigureAwait(false);
        return transport switch
        {
            SlmpTransportKind.Tcp => await ExchangeTcpAsync(
                endpoint,
                route,
                request,
                beforeSend,
                cancellationToken).ConfigureAwait(false),
            SlmpTransportKind.Udp => await ExchangeUdpAsync(
                endpoint,
                route,
                request,
                beforeSend,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported transport {transport}."),
        };
    }

    private static async Task<byte[]> ExchangeTcpAsync(
        IPEndPoint endpoint,
        LiberatorRoute route,
        SlmpRequestFrame request,
        Func<CancellationToken, Task> beforeSend,
        CancellationToken cancellationToken)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(route.ConnectTimeout);
        using var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(endpoint.Address, endpoint.Port, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to PLC route '{route.RouteKey}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException)
        {
            throw new IOException($"Failed to connect to PLC route '{route.RouteKey}'.", exception);
        }

        using var stream = client.GetStream();
        await beforeSend(cancellationToken).ConfigureAwait(false);
        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(route.ResponseTimeout);
        try
        {
            await stream.WriteAsync(request.RawFrame, responseTimeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(responseTimeout.Token).ConfigureAwait(false);

            while (true)
            {
                var rawResponse = await SlmpStreamReader.ReadResponseAsync(
                    stream,
                    route.ResponseTimeout,
                    responseTimeout.Token).ConfigureAwait(false);
                var status = SlmpFrameCodec.TryParseResponse(
                    rawResponse,
                    out var response,
                    out var consumed,
                    out var error);
                if (status != SlmpFrameParseStatus.Complete || response is null || consumed != rawResponse.Length)
                {
                    throw new InvalidDataException(error ?? "PLC returned a malformed SLMP response.");
                }

                if (SlmpFrameCodec.IsCorrelated(request, response, out _))
                {
                    return rawResponse;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token fired, not the caller's: this is our own response deadline.
            // Without this the job records "The operation was canceled." and loses the route.
            throw new TimeoutException($"Timed out exchanging SLMP with PLC route '{route.RouteKey}'.");
        }
    }

    private static async Task<byte[]> ExchangeUdpAsync(
        IPEndPoint endpoint,
        LiberatorRoute route,
        SlmpRequestFrame request,
        Func<CancellationToken, Task> beforeSend,
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(endpoint.AddressFamily);
        try
        {
            udp.Connect(endpoint);
            await beforeSend(cancellationToken).ConfigureAwait(false);
            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(route.ResponseTimeout);
            await udp.SendAsync(request.RawFrame, responseTimeout.Token).ConfigureAwait(false);

            while (true)
            {
                var datagram = await udp.ReceiveAsync(responseTimeout.Token).ConfigureAwait(false);
                var status = SlmpFrameCodec.TryParseResponse(
                    datagram.Buffer,
                    out var response,
                    out var consumed,
                    out var error);
                if (status == SlmpFrameParseStatus.Invalid)
                {
                    throw new InvalidDataException(error);
                }

                if (status == SlmpFrameParseStatus.Complete &&
                    response is not null &&
                    consumed == datagram.Buffer.Length &&
                    SlmpFrameCodec.IsCorrelated(request, response, out _))
                {
                    return datagram.Buffer;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out exchanging SLMP with PLC route '{route.RouteKey}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException)
        {
            throw new IOException($"UDP exchange failed for PLC route '{route.RouteKey}'.", exception);
        }
    }

    private static async Task<IPEndPoint> ResolveAsync(
        LiberatorRoute route,
        CancellationToken cancellationToken)
    {
        try
        {
            if (IPAddress.TryParse(route.Host, out var literal))
            {
                return new IPEndPoint(literal, route.Port);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(route.ConnectTimeout);
            var addresses = await Dns.GetHostAddressesAsync(route.Host, timeout.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(candidate =>
                    candidate.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                ?? throw new IOException($"No usable address was found for PLC route '{route.RouteKey}'.");
            return new IPEndPoint(address, route.Port);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out resolving PLC route '{route.RouteKey}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException)
        {
            throw new IOException($"Failed to resolve PLC route '{route.RouteKey}'.", exception);
        }
    }
}
