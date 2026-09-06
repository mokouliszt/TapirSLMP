using TapirSLMP.Protocol;
using Xunit;

namespace TapirSLMP.Protocol.Tests;

public sealed class SlmpFrameCodecTests
{
    private const string Request3EHex =
        "500000FFFF03000E0010000104020064000000A8000200";
    private const string Request4EHex =
        "54003412000000FFFF03000E0010000104020064000000A8000200";

    [Fact]
    public void Parse3EReadRequestExtractsEnvelope()
    {
        var bytes = Convert.FromHexString(Request3EHex);

        var status = SlmpFrameCodec.TryParseRequest(bytes, out var frame, out var consumed, out var error);

        Assert.Equal(SlmpFrameParseStatus.Complete, status);
        Assert.Null(error);
        var parsed = Assert.IsType<SlmpRequestFrame>(frame);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(SlmpFrameFormat.Binary3E, parsed.Format);
        Assert.Equal(new SlmpRoute(0, 0xFF, 0x03FF, 0), parsed.Route);
        Assert.Equal(0x0010, parsed.MonitoringTimer);
        Assert.Equal(0x0401, parsed.Command);
        Assert.Equal(0x0002, parsed.Subcommand);
        Assert.Equal(SlmpCommandRisk.ReadOnly, parsed.Risk);
    }

    [Fact]
    public void Parse4ERequestExtractsSerial()
    {
        var bytes = Convert.FromHexString(Request4EHex);

        var status = SlmpFrameCodec.TryParseRequest(bytes, out var frame, out var consumed, out var error);

        Assert.Equal(SlmpFrameParseStatus.Complete, status);
        Assert.Null(error);
        var parsed = Assert.IsType<SlmpRequestFrame>(frame);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(SlmpFrameFormat.Binary4E, parsed.Format);
        Assert.Equal(0x1234, parsed.SerialNumber);
    }

    [Fact]
    public void IncompleteFrameRequestsMoreData()
    {
        var bytes = Convert.FromHexString(Request4EHex);

        var status = SlmpFrameCodec.TryParseRequest(bytes[..^1], out var frame, out _, out var error);

        Assert.Equal(SlmpFrameParseStatus.NeedMoreData, status);
        Assert.Null(frame);
        Assert.Null(error);
    }

    [Fact]
    public void NonZero4EReservedFieldIsRejected()
    {
        var bytes = Convert.FromHexString(Request4EHex);
        bytes[4] = 1;

        var status = SlmpFrameCodec.TryParseRequest(bytes, out _, out _, out var error);

        Assert.Equal(SlmpFrameParseStatus.Invalid, status);
        Assert.Contains("reserved", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResponseMustMatchRouteAndSerial()
    {
        var requestBytes = Convert.FromHexString(Request4EHex);
        SlmpFrameCodec.TryParseRequest(requestBytes, out var request, out _, out _);
        var responseBytes = Convert.FromHexString("D4003412000000FFFF0300040000003412");
        SlmpFrameCodec.TryParseResponse(responseBytes, out var response, out _, out _);

        Assert.True(SlmpFrameCodec.IsCorrelated(request!, response!, out var error));
        Assert.Null(error);

        responseBytes[2]++;
        SlmpFrameCodec.TryParseResponse(responseBytes, out response, out _, out _);
        Assert.False(SlmpFrameCodec.IsCorrelated(request!, response!, out error));
        Assert.Contains("serial", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ErrorResponseKeepsRequestIdentity()
    {
        var requestBytes = Convert.FromHexString(Request4EHex);
        SlmpFrameCodec.TryParseRequest(requestBytes, out var request, out _, out _);

        var raw = SlmpFrameCodec.BuildErrorResponse(request!, 0xCEE0);
        var status = SlmpFrameCodec.TryParseResponse(raw, out var response, out var consumed, out _);

        Assert.Equal(SlmpFrameParseStatus.Complete, status);
        Assert.Equal(raw.Length, consumed);
        Assert.Equal(0xCEE0, response!.EndCode);
        Assert.True(SlmpFrameCodec.IsCorrelated(request!, response, out _));
    }

    [Fact]
    public async Task StreamReaderDoesNotConsumeTheNextFrame()
    {
        var one = Convert.FromHexString(Request3EHex);
        await using var stream = new MemoryStream([.. one, .. one]);

        var first = await SlmpStreamReader.ReadRequestAsync(stream, TimeSpan.FromSeconds(1), default);
        var second = await SlmpStreamReader.ReadRequestAsync(stream, TimeSpan.FromSeconds(1), default);

        Assert.Equal(one, first);
        Assert.Equal(one, second);
    }

    [Theory]
    [InlineData(0x0401, SlmpCommandRisk.ReadOnly)]
    [InlineData(0x1401, SlmpCommandRisk.StateChanging)]
    [InlineData(0xFFFF, SlmpCommandRisk.StateChanging)]
    public void CommandRiskIsConservative(ushort command, SlmpCommandRisk expected)
    {
        Assert.Equal(expected, SlmpCommandClassifier.Classify(command));
    }
}
