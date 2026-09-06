using TapirSLMP.Contracts;
using Xunit;

namespace TapirSLMP.Contracts.Tests;

public sealed class JobAdmissionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Frame(ushort command, ushort subcommand = 0x0002)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)0x0010));
        body.AddRange(BitConverter.GetBytes(command));
        body.AddRange(BitConverter.GetBytes(subcommand));
        body.AddRange([0x2C, 0x01, 0x00, 0x00, 0xA8]); // D300
        body.Add(0x00);
        body.AddRange(BitConverter.GetBytes((ushort)1));

        var frame = new List<byte> { 0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00 };
        frame.AddRange(BitConverter.GetBytes((ushort)body.Count));
        frame.AddRange(body);
        return [.. frame];
    }

    private static EnqueueJobRequest Request(byte[] frame, string id = "req-1") =>
        new(id, "plc1", SlmpTransportKind.Tcp, frame);

    private static JobAdmissionPolicy Policy(bool allowWrites) =>
        new(new JobPolicyOptions { AllowStateChangingCommands = allowWrites });

    [Fact]
    public void ReadCommandsAreAdmitted()
    {
        var admitted = Policy(false).TryAdmit(Request(Frame(0x0401)), Now, out var job, out var rejection);

        Assert.True(admitted);
        Assert.Null(rejection);
        Assert.Equal(JobRisk.ReadOnly, job!.Risk);
        Assert.Equal(0x0401, job.Command);
        Assert.Equal("job_req-1", job.Id);
    }

    [Fact]
    public void WriteCommandsAreRejectedWhenStateChangesAreDisabled()
    {
        var admitted = Policy(false).TryAdmit(Request(Frame(0x1401)), Now, out _, out var rejection);

        Assert.False(admitted);
        Assert.Equal("state_change_disabled", rejection!.Code);
        Assert.Equal(PolicyRejectionKind.Forbidden, rejection.Kind);
    }

    [Fact]
    public void WriteCommandsAreAdmittedWhenStateChangesAreEnabled()
    {
        var admitted = Policy(true).TryAdmit(Request(Frame(0x1401)), Now, out var job, out _);

        Assert.True(admitted);
        Assert.Equal(JobRisk.StateChanging, job!.Risk);
    }

    [Fact]
    public void UnknownCommandsAreTreatedAsStateChanging()
    {
        var admitted = Policy(false).TryAdmit(Request(Frame(0x9999)), Now, out _, out var rejection);

        Assert.False(admitted);
        Assert.Equal("state_change_disabled", rejection!.Code);
    }

    [Fact]
    public void ExpiryBeyondTheMaximumTtlIsRejected()
    {
        var policy = new JobAdmissionPolicy(new JobPolicyOptions { MaximumJobTtlSeconds = 60 });
        var request = new EnqueueJobRequest(
            "req-1", "plc1", SlmpTransportKind.Tcp, Frame(0x0401), Now.AddSeconds(61));

        Assert.False(policy.TryAdmit(request, Now, out _, out var rejection));
        Assert.Equal("invalid_expiry", rejection!.Code);
    }

    [Fact]
    public void DefaultTtlIsAppliedWhenNoExpiryIsSupplied()
    {
        var policy = new JobAdmissionPolicy(new JobPolicyOptions { DefaultJobTtlSeconds = 30 });

        Assert.True(policy.TryAdmit(Request(Frame(0x0401)), Now, out var job, out _));
        Assert.Equal(Now.AddSeconds(30), job!.ExpiresAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void UnsafeClientRequestIdsAreRejected(string clientRequestId)
    {
        var admitted = Policy(false)
            .TryAdmit(Request(Frame(0x0401), clientRequestId), Now, out _, out var rejection);

        Assert.False(admitted);
        Assert.Equal("invalid_client_request_id", rejection!.Code);
    }

    [Fact]
    public void TruncatedFramesAreRejected()
    {
        var frame = Frame(0x0401);
        var request = Request(frame[..(frame.Length - 2)]);

        Assert.False(Policy(false).TryAdmit(request, Now, out _, out var rejection));
        Assert.Equal("invalid_frame", rejection!.Code);
    }

    [Fact]
    public void LeaseDurationMustFitInsideTheConfiguredMaximum()
    {
        var policy = new JobAdmissionPolicy(new JobPolicyOptions { MaximumLeaseSeconds = 30 });

        Assert.Null(policy.ValidateLeaseRequest(new AcquireLeaseRequest("w1", ["plc1"], 30)));
        Assert.Equal(
            "invalid_lease_duration",
            policy.ValidateLeaseRequest(new AcquireLeaseRequest("w1", ["plc1"], 31))!.Code);
    }

    [Fact]
    public void DuplicateRouteKeysAreRejected()
    {
        var rejection = Policy(false)
            .ValidateLeaseRequest(new AcquireLeaseRequest("w1", ["plc1", "plc1"], 30));

        Assert.Equal("invalid_lease_request", rejection!.Code);
    }
}

public sealed class KnownRouteKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JobAdmissionPolicy Policy(params string[] knownRoutes) =>
        new(new JobPolicyOptions
        {
            KnownRouteKeys = knownRoutes.ToHashSet(StringComparer.Ordinal),
        });

    private static EnqueueJobRequest Request(string routeKey) =>
        new("req-1", routeKey, SlmpTransportKind.Tcp, JobAdmissionPolicyTests.Frame(0x0401));

    [Fact]
    public void AnEmptyListAcceptsAnyValidRouteKey()
    {
        Assert.True(Policy().TryAdmit(Request("anything"), Now, out _, out _));
    }

    [Fact]
    public void AKnownRouteKeyIsAdmitted()
    {
        Assert.True(Policy("plc1", "plc2").TryAdmit(Request("plc2"), Now, out _, out _));
    }

    [Fact]
    public void AMistypedRouteKeyIsRejectedAtSubmission()
    {
        var admitted = Policy("plc1", "plc2").TryAdmit(Request("plc02"), Now, out _, out var rejection);

        Assert.False(admitted);
        Assert.Equal("unknown_route_key", rejection!.Code);
        Assert.Equal(PolicyRejectionKind.BadRequest, rejection.Kind);
    }

    [Fact]
    public void RouteKeyMatchingIsCaseSensitive()
    {
        Assert.False(Policy("plc1").TryAdmit(Request("PLC1"), Now, out _, out var rejection));
        Assert.Equal("unknown_route_key", rejection!.Code);
    }

    [Fact]
    public void ALiberatorLeasingAnUnknownRouteIsRejected()
    {
        var rejection = Policy("plc1")
            .ValidateLeaseRequest(new AcquireLeaseRequest("w1", ["plc1", "plc02"], 30));

        Assert.Equal("unknown_route_key", rejection!.Code);
    }

    [Fact]
    public void ALiberatorLeasingKnownRoutesIsAccepted()
    {
        Assert.Null(Policy("plc1", "plc2")
            .ValidateLeaseRequest(new AcquireLeaseRequest("w1", ["plc1", "plc2"], 30)));
    }

    [Fact]
    public void AnInvalidEntryInTheListIsRejectedAtStartup()
    {
        var options = new JobPolicyOptions
        {
            KnownRouteKeys = new HashSet<string>(StringComparer.Ordinal) { "-bad" },
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
