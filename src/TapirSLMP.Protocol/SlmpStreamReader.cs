using System.Buffers.Binary;

namespace TapirSLMP.Protocol;

public static class SlmpStreamReader
{
    public static Task<byte[]> ReadRequestAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ReadFrameAsync(stream, timeout, request: true, cancellationToken);

    public static Task<byte[]> ReadResponseAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ReadFrameAsync(stream, timeout, request: false, cancellationToken);

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        TimeSpan timeout,
        bool request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var prefix = new byte[2];
            await stream.ReadExactlyAsync(prefix, timeoutSource.Token).ConfigureAwait(false);
            var subheader = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
            var headerLength = (request, subheader) switch
            {
                (true, 0x0050) => 9,
                (true, 0x0054) => 13,
                (false, 0x00D0) => 9,
                (false, 0x00D4) => 13,
                _ => throw new InvalidDataException(
                    $"Unexpected SLMP {(request ? "request" : "response")} subheader 0x{subheader:X4}."),
            };

            var header = new byte[headerLength];
            prefix.CopyTo(header, 0);
            await stream.ReadExactlyAsync(header.AsMemory(2), timeoutSource.Token).ConfigureAwait(false);

            var bodyLengthOffset = headerLength - 2;
            var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(bodyLengthOffset));
            var minimumBodyLength = request ? 6 : 2;
            if (bodyLength < minimumBodyLength)
            {
                throw new InvalidDataException(
                    $"SLMP body length {bodyLength} is smaller than {minimumBodyLength}.");
            }

            var frame = new byte[headerLength + bodyLength];
            header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(
                frame.AsMemory(headerLength, bodyLength),
                timeoutSource.Token).ConfigureAwait(false);

            SlmpFrameParseStatus status;
            int consumed;
            string? error;
            if (request)
            {
                status = SlmpFrameCodec.TryParseRequest(frame, out _, out consumed, out error);
            }
            else
            {
                status = SlmpFrameCodec.TryParseResponse(frame, out _, out consumed, out error);
            }

            if (status != SlmpFrameParseStatus.Complete || consumed != frame.Length)
            {
                throw new InvalidDataException(error ?? "The SLMP frame is incomplete or malformed.");
            }

            return frame;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while receiving an SLMP frame.");
        }
    }
}
