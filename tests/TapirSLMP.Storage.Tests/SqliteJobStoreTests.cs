using Microsoft.Data.Sqlite;
using TapirSLMP.Contracts;
using TapirSLMP.Storage;
using TapirSLMP.Storage.Sqlite;
using Xunit;

namespace TapirSLMP.Storage.Tests;

public sealed class SqliteJobStoreTests
{
    [Fact]
    public async Task SameRouteIsLeasedInFifoOrder()
    {
        var fixture = await SqliteFixture.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            Assert.True(await fixture.Store.TryAddAsync(CreateJob("job-1", "route-a", JobRisk.ReadOnly, now), default));
            Assert.True(await fixture.Store.TryAddAsync(CreateJob("job-2", "route-a", JobRisk.ReadOnly, now.AddMilliseconds(1)), default));
            Assert.False(await fixture.Store.TryAddAsync(CreateJob("job-1", "route-a", JobRisk.ReadOnly, now), default));

            var first = await fixture.Store.TryAcquireAsync(
                "worker-1", ["route-a"], TimeSpan.FromSeconds(30), 3, now, default);
            var blocked = await fixture.Store.TryAcquireAsync(
                "worker-2", ["route-a"], TimeSpan.FromSeconds(30), 3, now, default);

            Assert.NotNull(first);
            Assert.Equal("job-1", first!.Job.Id);
            Assert.Null(blocked);

            Assert.Equal(StoreMutationResult.Success, await fixture.Store.MarkExecutingAsync(
                first!.Job.Id, "worker-1", first!.LeaseToken, now, default));
            Assert.Equal(StoreMutationResult.Success, await fixture.Store.CompleteAsync(
                first!.Job.Id,
                "worker-1",
                first!.LeaseToken,
                Convert.FromHexString("D00000FFFF030002000000"),
                now,
                default));

            var second = await fixture.Store.TryAcquireAsync(
                "worker-2", ["route-a"], TimeSpan.FromSeconds(30), 3, now, default);
            Assert.Equal("job-2", second!.Job.Id);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task StateChangingExecutingJobCannotBeRequeued()
    {
        var fixture = await SqliteFixture.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            Assert.True(await fixture.Store.TryAddAsync(CreateJob("job-write", "route-a", JobRisk.StateChanging, now), default));
            var lease = await fixture.Store.TryAcquireAsync(
                "worker-1", ["route-a"], TimeSpan.FromSeconds(30), 3, now, default);
            Assert.NotNull(lease);
            await fixture.Store.MarkExecutingAsync(
                lease!.Job.Id, "worker-1", lease!.LeaseToken, now, default);

            var result = await fixture.Store.FailAsync(
                lease!.Job.Id,
                "worker-1",
                lease!.LeaseToken,
                FailureDisposition.Retry,
                "connection lost",
                3,
                now,
                default);

            Assert.Equal(StoreMutationResult.Success, result);
            Assert.Equal(JobStatus.OutcomeUnknown, (await fixture.Store.GetAsync("job-write", default))!.Status);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ExpiredReadLeaseIsRequeued()
    {
        var fixture = await SqliteFixture.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            Assert.True(await fixture.Store.TryAddAsync(CreateJob("job-read", "route-a", JobRisk.ReadOnly, now), default));
            var lease = await fixture.Store.TryAcquireAsync(
                "worker-1", ["route-a"], TimeSpan.FromSeconds(5), 3, now, default);
            Assert.NotNull(lease);
            await fixture.Store.MarkExecutingAsync(
                lease!.Job.Id, "worker-1", lease!.LeaseToken, now, default);

            await fixture.Store.SweepAsync(3, now.AddSeconds(6), default);

            Assert.Equal(JobStatus.Queued, (await fixture.Store.GetAsync("job-read", default))!.Status);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static NewJob CreateJob(
        string id,
        string route,
        JobRisk risk,
        DateTimeOffset createdAt) => new(
        id,
        route,
        SlmpTransportKind.Tcp,
        risk,
        risk == JobRisk.ReadOnly ? (ushort)0x0401 : (ushort)0x1401,
        0,
        [0x50, 0x00],
        createdAt,
        createdAt.AddMinutes(5));

    private sealed class SqliteFixture : IDisposable
    {
        private SqliteFixture(string directory, SqliteJobStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }

        public SqliteJobStore Store { get; }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tapirslmp-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var store = new SqliteJobStore(new SqliteJobStoreOptions
            {
                ConnectionString = $"Data Source={Path.Combine(directory, "jobs.db")};Pooling=False",
            });
            await store.InitializeAsync(default);
            return new SqliteFixture(directory, store);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
