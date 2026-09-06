using System.Buffers.Binary;

namespace TapirSLMP.Protocol;

public static class SlmpFrameCodec
{
    public const int MaximumFrameLength = 13 + ushort.MaxValue;

    private const ushort Request3ESubheader = 0x0050;
    private const ushort Response3ESubheader = 0x00D0;
    private const ushort Request4ESubheader = 0x0054;
    private const ushort Response4ESubheader = 0x00D4;

    public static SlmpFrameParseStatus TryParseRequest(
        ReadOnlySpan<byte> data,
        out SlmpRequestFrame? frame,
        out int consumed,
        out string? error)
    {
        frame = null;
        consumed = 0;
        error = null;

        if (data.Length < 2)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        var subheader = BinaryPrimitives.ReadUInt16LittleEndian(data);
        return subheader switch
        {
            Request3ESubheader => Parse3ERequest(data, out frame, out consumed, out error),
            Request4ESubheader => Parse4ERequest(data, out frame, out consumed, out error),
            _ => Invalid($"Unsupported request subheader 0x{subheader:X4}.", out error),
        };
    }

    public static SlmpFrameParseStatus TryParseResponse(
        ReadOnlySpan<byte> data,
        out SlmpResponseFrame? frame,
        out int consumed,
        out string? error)
    {
        frame = null;
        consumed = 0;
        error = null;

        if (data.Length < 2)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        var subheader = BinaryPrimitives.ReadUInt16LittleEndian(data);
        return subheader switch
        {
            Response3ESubheader => Parse3EResponse(data, out frame, out consumed, out error),
            Response4ESubheader => Parse4EResponse(data, out frame, out consumed, out error),
            _ => Invalid($"Unsupported response subheader 0x{subheader:X4}.", out error),
        };
    }

    public static bool IsCorrelated(
        SlmpRequestFrame request,
        SlmpResponseFrame response,
        out string? error)
    {
        if (request.Format != response.Format)
        {
            error = "Response frame format does not match the request.";
            return false;
        }

        if (request.Route != response.Route)
        {
            error = "Response route does not match the request.";
            return false;
        }

        if (request.Format == SlmpFrameFormat.Binary4E &&
            request.SerialNumber != response.SerialNumber)
        {
            error = "Response serial number does not match the request.";
            return false;
        }

        error = null;
        return true;
    }

    public static byte[] BuildErrorResponse(SlmpRequestFrame request, ushort endCode)
    {
        var is4E = request.Format == SlmpFrameFormat.Binary4E;
        var response = new byte[is4E ? 15 : 11];
        BinaryPrimitives.WriteUInt16LittleEndian(
            response,
            is4E ? Response4ESubheader : Response3ESubheader);

        var routeOffset = is4E ? 6 : 2;
        var lengthOffset = is4E ? 11 : 7;
        var endCodeOffset = is4E ? 13 : 9;

        if (is4E)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(2), request.SerialNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), 0);
        }

        WriteRoute(response.AsSpan(routeOffset), request.Route);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(lengthOffset), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(endCodeOffset), endCode);
        return response;
    }

    private static SlmpFrameParseStatus Parse3ERequest(
        ReadOnlySpan<byte> data,
        out SlmpRequestFrame? frame,
        out int consumed,
        out string? error)
    {
        frame = null;
        consumed = 0;
        error = null;
        if (data.Length < 9)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(data[7..]);
        if (bodyLength < 6)
        {
            return Invalid("A binary 3E request body must contain timer, command, and subcommand.", out error);
        }

        var totalLength = 9 + bodyLength;
        if (data.Length < totalLength)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        var raw = data[..totalLength].ToArray();
        frame = new SlmpRequestFrame(
            SlmpFrameFormat.Binary3E,
            0,
            ReadRoute(data[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[9..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[11..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[13..]),
            raw);
        consumed = totalLength;
        return SlmpFrameParseStatus.Complete;
    }

    private static SlmpFrameParseStatus Parse4ERequest(
        ReadOnlySpan<byte> data,
        out SlmpRequestFrame? frame,
        out int consumed,
        out string? error)
    {
        frame = null;
        consumed = 0;
        error = null;
        if (data.Length < 13)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 0)
        {
            return Invalid("The binary 4E reserved field must be zero.", out error);
        }

        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(data[11..]);
        if (bodyLength < 6)
        {
            return Invalid("A binary 4E request body must contain timer, command, and subcommand.", out error);
        }

        var totalLength = 13 + bodyLength;
        if (data.Length < totalLength)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        var raw = data[..totalLength].ToArray();
        frame = new SlmpRequestFrame(
            SlmpFrameFormat.Binary4E,
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            ReadRoute(data[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[13..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[15..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[17..]),
            raw);
        consumed = totalLength;
        return SlmpFrameParseStatus.Complete;
    }

    private static SlmpFrameParseStatus Parse3EResponse(
        ReadOnlySpan<byte> data,
        out SlmpResponseFrame? frame,
        out int consumed,
        out string? error)
    {
        frame = null;
        consumed = 0;
        error = null;
        if (data.Length < 9)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(data[7..]);
        if (bodyLength < 2)
        {
            return Invalid("A binary 3E response body must contain an end code.", out error);
        }

        var totalLength = 9 + bodyLength;
        if (data.Length < totalLength)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        frame = new SlmpResponseFrame(
            SlmpFrameFormat.Binary3E,
            0,
            ReadRoute(data[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[9..]),
            data[..totalLength].ToArray());
        consumed = totalLength;
        return SlmpFrameParseStatus.Complete;
    }

    private static SlmpFrameParseStatus Parse4EResponse(
        ReadOnlySpan<byte> data,
        out SlmpResponseFrame? frame,
        out int consumed,
        out string? error)
    {
        frame = null;
        consumed = 0;
        error = null;
        if (data.Length < 13)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 0)
        {
            return Invalid("The binary 4E reserved field must be zero.", out error);
        }

        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(data[11..]);
        if (bodyLength < 2)
        {
            return Invalid("A binary 4E response body must contain an end code.", out error);
        }

        var totalLength = 13 + bodyLength;
        if (data.Length < totalLength)
        {
            return SlmpFrameParseStatus.NeedMoreData;
        }

        frame = new SlmpResponseFrame(
            SlmpFrameFormat.Binary4E,
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            ReadRoute(data[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[13..]),
            data[..totalLength].ToArray());
        consumed = totalLength;
        return SlmpFrameParseStatus.Complete;
    }

    private static SlmpRoute ReadRoute(ReadOnlySpan<byte> data) => new(
        data[0],
        data[1],
        BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
        data[4]);

    private static void WriteRoute(Span<byte> destination, SlmpRoute route)
    {
        destination[0] = route.NetworkNumber;
        destination[1] = route.PcNumber;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], route.DestinationModuleIo);
        destination[4] = route.DestinationModuleStation;
    }

    private static SlmpFrameParseStatus Invalid(string message, out string? error)
    {
        error = message;
        return SlmpFrameParseStatus.Invalid;
    }
}
