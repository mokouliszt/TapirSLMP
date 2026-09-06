using TapirSLMP.Contracts;
using TapirSLMP.Storage;
using TapirSLMP.Storage.Sqlite;
using Xunit;

namespace TapirSLMP.Storage.Tests;

/// <summary>
/// The in-process gateway bypasses the HTTP endpoints, so these tests exist to prove the
/// admission policy still runs on that path. A regression here would silently remove the
/// read-only guard for embedded deployments.
/// </summary>
public sealed class InProcessJobGatewayTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"tapirslmp-inproc-{Guid.NewGuid():N}.db");

    private static byte[] Frame(ushort command) => FrameFactory.Build(command);

    private async Task<InProcessJobGateway> CreateAsync(bool allowWrites)
    {
        var store = new SqliteJobStore(new SqliteJobStoreOptions
        {
            ConnectionString = $"Data Source={_databasePath};Pooling=False",
        });
        await store.InitializeAsync(CancellationToken.None);
        var policy = new JobAdmissionPolicy(new JobPolicyOptions
        {
            AllowStateChangingCommands = allowWrites,
        });
        return new InProcessJobGateway(
            store,
            policy,
            new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task ReadOnlyGuardStillAppliesWithoutTheHttpLayer()
    {
        var gateway = await CreateAsync(allowWrites: false);

        var exception = await Assert.ThrowsAsync<JobGatewayException>(() =>
            gateway.EnqueueAsync(
                new EnqueueJobRequest("w1", "plc1", SlmpTransportKind.Tcp, Frame(0x1401)),
                CancellationToken.None));

        Assert.Equal("state_change_disabled", exception.Code);
    }

    [Fact]
    public async Task ReadCommandsAreEnqueuedAndReadBack()
    {
        var gateway = await CreateAsync(allowWrites: false);

        var enqueued = await gateway.EnqueueAsync(
            new EnqueueJobRequest("r1", "plc1", SlmpTransportKind.Tcp, Frame(0x0401)),
            CancellationToken.None);

        Assert.False(enqueued.Replayed);
        var job = await gateway.GetAsync(enqueued.Id, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Queued, job!.Status);
        Assert.Equal(JobRisk.ReadOnly, job.Risk);
    }

    [Fact]
    public async Task ReplayingTheSameRequestIsIdempotent()
    {
        var gateway = await CreateAsync(allowWrites: false);
        var request = new EnqueueJobRequest("r2", "plc1", SlmpTransportKind.Tcp, Frame(0x0401));

        var first = await gateway.EnqueueAsync(request, CancellationToken.None);
        var second = await gateway.EnqueueAsync(request, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ReusingAClientRequestIdWithDifferentDataConflicts()
    {
        var gateway = await CreateAsync(allowWrites: true);
        await gateway.EnqueueAsync(
            new EnqueueJobRequest("r3", "plc1", SlmpTransportKind.Tcp, Frame(0x0401)),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JobGatewayException>(() =>
            gateway.EnqueueAsync(
                new EnqueueJobRequest("r3", "plc1", SlmpTransportKind.Tcp, Frame(0x0403)),
                CancellationToken.None));

        Assert.Equal("idempotency_conflict", exception.Code);
    }

    [Fact]
    public async Task LeasesFlowThroughTheGateway()
    {
        var gateway = await CreateAsync(allowWrites: false);
        var enqueued = await gateway.EnqueueAsync(
            new EnqueueJobRequest("r4", "plc1", SlmpTransportKind.Tcp, Frame(0x0401)),
            CancellationToken.None);

        var lease = await gateway.AcquireAsync(
            new AcquireLeaseRequest("worker-1", ["plc1"], 30),
            CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(enqueued.Id, lease!.Job.Id);

        await gateway.MarkExecutingAsync(
            lease.Job.Id,
            new LeaseActionRequest("worker-1", lease.LeaseToken),
            CancellationToken.None);

        var job = await gateway.GetAsync(lease.Job.Id, CancellationToken.None);
        Assert.Equal(JobStatus.Executing, job!.Status);
    }

    [Fact]
    public async Task CompletingWithAnUncorrelatedResponseIsRejected()
    {
        var gateway = await CreateAsync(allowWrites: false);
        var enqueued = await gateway.EnqueueAsync(
            new EnqueueJobRequest("r5", "plc1", SlmpTransportKind.Tcp, Frame(0x0401)),
            CancellationToken.None);
        var lease = await gateway.AcquireAsync(
            new AcquireLeaseRequest("worker-1", ["plc1"], 30),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JobGatewayException>(() =>
            gateway.CompleteAsync(
                enqueued.Id,
                new CompleteJobRequest("worker-1", lease!.LeaseToken, [0x01, 0x02, 0x03]),
                CancellationToken.None));

        Assert.Equal("invalid_response_frame", exception.Code);
    }

    [Fact]
    public async Task AStaleLeaseTokenCannotCompleteAJob()
    {
        var gateway = await CreateAsync(allowWrites: false);
        var enqueued = await gateway.EnqueueAsync(
            new EnqueueJobRequest("r6", "plc1", SlmpTransportKind.Tcp, Frame(0x0401)),
            CancellationToken.None);
        await gateway.AcquireAsync(new AcquireLeaseRequest("worker-1", ["plc1"], 30), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JobGatewayException>(() =>
            gateway.MarkExecutingAsync(
                enqueued.Id,
                new LeaseActionRequest("worker-1", new string('z', 44)),
                CancellationToken.None));

        Assert.Equal("lease_conflict", exception.Code);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class FrameFactory
{
    public static byte[] Build(ushort command, ushort subcommand = 0x0002)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)0x0010));
        body.AddRange(BitConverter.GetBytes(command));
        body.AddRange(BitConverter.GetBytes(subcommand));
        body.AddRange([0x2C, 0x01, 0x00, 0x00, 0xA8]);
        body.Add(0x00);
        body.AddRange(BitConverter.GetBytes((ushort)1));

        var frame = new List<byte> { 0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00 };
        frame.AddRange(BitConverter.GetBytes((ushort)body.Count));
        frame.AddRange(body);
        return [.. frame];
    }
}
