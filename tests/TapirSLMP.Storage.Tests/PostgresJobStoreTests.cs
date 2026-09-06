using TapirSLMP.Contracts;
using TapirSLMP.Storage;
using TapirSLMP.Storage.Postgres;
using Xunit;

namespace TapirSLMP.Storage.Tests;

public sealed class PostgresJobStoreTests
{
    [Fact]
    public async Task CanEnqueueAndLeaseWhenIntegrationDatabaseIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("TAPIRSLMP_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var store = new PostgresJobStore(new PostgresJobStoreOptions
        {
            ConnectionString = connectionString,
        });
        await store.InitializeAsync(default);
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.TryAddAsync(new NewJob(
            $"job-{suffix}",
            $"route-{suffix}",
            SlmpTransportKind.Tcp,
            JobRisk.ReadOnly,
            0x0401,
            0,
            [0x50, 0x00],
            now,
            now.AddMinutes(1)), default));

        var lease = await store.TryAcquireAsync(
            $"worker-{suffix}",
            [$"route-{suffix}"],
            TimeSpan.FromSeconds(30),
            3,
            now,
            default);

        Assert.NotNull(lease);
    }
}
