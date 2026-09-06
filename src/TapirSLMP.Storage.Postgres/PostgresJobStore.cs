using System.Data;
using Npgsql;
using TapirSLMP.Contracts;
using TapirSLMP.Storage;

namespace TapirSLMP.Storage.Postgres;

public sealed class PostgresJobStore : IJobStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresJobStore(PostgresJobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("PostgreSQL connection string must not be empty.", nameof(options));
        }

        var connectionString = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            SearchPath = "tapir_slmp",
            IncludeErrorDetail = false,
            LogParameters = false,
            PersistSecurityInfo = false,
        };
        _dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var advisoryLock = connection.CreateCommand();
        advisoryLock.Transaction = transaction;
        advisoryLock.CommandText = "SELECT pg_advisory_xact_lock(736586274451921015);";
        await advisoryLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE SCHEMA IF NOT EXISTS tapir_slmp;

            CREATE TABLE IF NOT EXISTS tapir_slmp.routes (
                route_key text PRIMARY KEY
            );

            CREATE TABLE IF NOT EXISTS tapir_slmp.jobs (
                id text PRIMARY KEY,
                route_key text NOT NULL REFERENCES tapir_slmp.routes(route_key),
                transport text NOT NULL,
                status text NOT NULL,
                risk text NOT NULL,
                command integer NOT NULL,
                subcommand integer NOT NULL,
                request_frame bytea NOT NULL,
                response_frame bytea NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                created_at timestamp with time zone NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                lease_until timestamp with time zone NULL,
                lease_token text NULL,
                worker_id text NULL,
                completed_at timestamp with time zone NULL,
                error text NULL
            );

            CREATE INDEX IF NOT EXISTS ix_jobs_fifo
                ON tapir_slmp.jobs(status, created_at, id);
            CREATE INDEX IF NOT EXISTS ix_jobs_expiry
                ON tapir_slmp.jobs(status, expires_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_route
                ON tapir_slmp.jobs(route_key)
                WHERE status IN ('Leased', 'Executing');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryAddAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var route = connection.CreateCommand())
        {
            route.Transaction = transaction;
            route.CommandText = """
                INSERT INTO tapir_slmp.routes(route_key)
                VALUES (@routeKey)
                ON CONFLICT DO NOTHING;
                """;
            route.Parameters.AddWithValue("routeKey", job.RouteKey);
            await route.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int inserted;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tapir_slmp.jobs (
                    id, route_key, transport, status, risk, command, subcommand,
                    request_frame, attempt_count, created_at, expires_at)
                VALUES (
                    @id, @routeKey, @transport, 'Queued', @risk, @command, @subcommand,
                    @requestFrame, 0, @createdAt, @expiresAt)
                ON CONFLICT (id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("id", job.Id);
            command.Parameters.AddWithValue("routeKey", job.RouteKey);
            command.Parameters.AddWithValue("transport", job.Transport.ToString());
            command.Parameters.AddWithValue("risk", job.Risk.ToString());
            command.Parameters.AddWithValue("command", (int)job.Command);
            command.Parameters.AddWithValue("subcommand", (int)job.Subcommand);
            command.Parameters.AddWithValue("requestFrame", job.RequestFrame);
            command.Parameters.AddWithValue("createdAt", job.CreatedAt.UtcDateTime);
            command.Parameters.AddWithValue("expiresAt", job.ExpiresAt.UtcDateTime);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    public async Task<StoredJob?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM tapir_slmp.jobs WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    public async Task SweepAsync(
        int maximumAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExpireAndRecoverAsync(
            connection,
            transaction,
            now,
            maximumAttempts,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneAsync(
        DateTimeOffset completedBefore,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM tapir_slmp.jobs
            WHERE status IN ('Completed', 'Failed', 'Expired', 'OutcomeUnknown')
              AND completed_at < @completedBefore;
            """;
        command.Parameters.AddWithValue("completedBefore", completedBefore.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcquiredLease?> TryAcquireAsync(
        string workerId,
        IReadOnlyCollection<string> routeKeys,
        TimeSpan leaseDuration,
        int maximumAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (routeKeys.Count == 0)
        {
            return null;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);

        await ExpireAndRecoverAsync(
            connection,
            transaction,
            now,
            maximumAttempts,
            cancellationToken).ConfigureAwait(false);

        string? routeKey;
        await using (var route = connection.CreateCommand())
        {
            route.Transaction = transaction;
            route.CommandText = """
                SELECT r.route_key
                FROM tapir_slmp.routes AS r
                WHERE r.route_key = ANY(@routeKeys)
                  AND EXISTS (
                      SELECT 1
                      FROM tapir_slmp.jobs AS queued
                      WHERE queued.route_key = r.route_key
                        AND queued.status = 'Queued'
                        AND queued.expires_at > @now
                        AND queued.attempt_count < @maximumAttempts)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM tapir_slmp.jobs AS active
                      WHERE active.route_key = r.route_key
                        AND active.status IN ('Leased', 'Executing'))
                ORDER BY (
                    SELECT MIN(queued.created_at)
                    FROM tapir_slmp.jobs AS queued
                    WHERE queued.route_key = r.route_key
                      AND queued.status = 'Queued')
                LIMIT 1
                FOR UPDATE OF r SKIP LOCKED;
                """;
            route.Parameters.AddWithValue("routeKeys", routeKeys.ToArray());
            route.Parameters.AddWithValue("now", now.UtcDateTime);
            route.Parameters.AddWithValue("maximumAttempts", maximumAttempts);
            routeKey = (string?)await route.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (routeKey is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        string? id;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id
                FROM tapir_slmp.jobs
                WHERE route_key = @routeKey
                  AND status = 'Queued'
                  AND expires_at > @now
                  AND attempt_count < @maximumAttempts
                ORDER BY created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED;
                """;
            select.Parameters.AddWithValue("routeKey", routeKey);
            select.Parameters.AddWithValue("now", now.UtcDateTime);
            select.Parameters.AddWithValue("maximumAttempts", maximumAttempts);
            id = (string?)await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (id is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var leaseToken = StoreUtilities.NewLeaseToken();
        var leaseUntil = now + leaseDuration;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE tapir_slmp.jobs
                SET status = 'Leased',
                    attempt_count = attempt_count + 1,
                    lease_until = @leaseUntil,
                    lease_token = @leaseToken,
                    worker_id = @workerId,
                    error = NULL
                WHERE id = @id AND status = 'Queued';
                """;
            update.Parameters.AddWithValue("leaseUntil", leaseUntil.UtcDateTime);
            update.Parameters.AddWithValue("leaseToken", leaseToken);
            update.Parameters.AddWithValue("workerId", workerId);
            update.Parameters.AddWithValue("id", id);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var job = await GetWithinTransactionAsync(connection, transaction, id, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job is null ? null : new AcquiredLease(job, leaseToken, leaseUntil);
    }

    public Task<StoreMutationResult> MarkExecutingAsync(
        string id,
        string workerId,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            """
            UPDATE tapir_slmp.jobs
            SET status = 'Executing'
            WHERE id = @id
              AND status = 'Leased'
              AND worker_id = @workerId
              AND lease_token = @leaseToken
              AND lease_until > @now
              AND expires_at > @now;
            """,
            workerId,
            leaseToken,
            now,
            null,
            cancellationToken);

    public Task<StoreMutationResult> CompleteAsync(
        string id,
        string workerId,
        string leaseToken,
        byte[] responseFrame,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            """
            UPDATE tapir_slmp.jobs
            SET status = 'Completed',
                response_frame = @payload,
                completed_at = @now,
                lease_until = NULL,
                lease_token = NULL,
                worker_id = NULL,
                error = NULL
            WHERE id = @id
              AND status = 'Executing'
              AND worker_id = @workerId
              AND lease_token = @leaseToken
              AND lease_until > @now;
            """,
            workerId,
            leaseToken,
            now,
            responseFrame,
            cancellationToken);

    public async Task<StoreMutationResult> FailAsync(
        string id,
        string workerId,
        string leaseToken,
        FailureDisposition disposition,
        string error,
        int maximumAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tapir_slmp.jobs
            SET status = CASE
                    WHEN @disposition = 'OutcomeUnknown' THEN 'OutcomeUnknown'
                    WHEN @disposition = 'Retry'
                         AND NOT (status = 'Executing' AND risk = 'StateChanging')
                         AND attempt_count < @maximumAttempts
                         AND expires_at > @now THEN 'Queued'
                    WHEN @disposition = 'Retry'
                         AND status = 'Executing'
                         AND risk = 'StateChanging' THEN 'OutcomeUnknown'
                    ELSE 'Failed'
                END,
                completed_at = CASE
                    WHEN @disposition = 'Retry'
                         AND NOT (status = 'Executing' AND risk = 'StateChanging')
                         AND attempt_count < @maximumAttempts
                         AND expires_at > @now THEN NULL
                    ELSE @now
                END,
                lease_until = NULL,
                lease_token = NULL,
                worker_id = NULL,
                error = @error
            WHERE id = @id
              AND status IN ('Leased', 'Executing')
              AND worker_id = @workerId
              AND lease_token = @leaseToken
              AND lease_until > @now;
            """;
        command.Parameters.AddWithValue("disposition", disposition.ToString());
        command.Parameters.AddWithValue("maximumAttempts", maximumAttempts);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("workerId", workerId);
        command.Parameters.AddWithValue("leaseToken", leaseToken);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1
            ? StoreMutationResult.Success
            : await ExistsAsync(connection, id, cancellationToken).ConfigureAwait(false)
                ? StoreMutationResult.Conflict
                : StoreMutationResult.NotFound;
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static async Task ExpireAndRecoverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tapir_slmp.jobs
            SET status = 'Expired', completed_at = @now, error = 'Job expired before execution.'
            WHERE status = 'Queued' AND expires_at <= @now;

            UPDATE tapir_slmp.jobs
            SET status = CASE
                    WHEN status = 'Executing' AND risk = 'StateChanging' THEN 'OutcomeUnknown'
                    WHEN expires_at <= @now THEN 'Expired'
                    WHEN attempt_count >= @maximumAttempts THEN 'Failed'
                    ELSE 'Queued'
                END,
                completed_at = CASE
                    WHEN status = 'Executing' AND risk = 'StateChanging' THEN @now
                    WHEN expires_at <= @now THEN @now
                    WHEN attempt_count >= @maximumAttempts THEN @now
                    ELSE NULL
                END,
                lease_until = NULL,
                lease_token = NULL,
                worker_id = NULL,
                error = CASE
                    WHEN status = 'Executing' AND risk = 'StateChanging'
                        THEN 'Lease expired after execution began; PLC outcome is unknown.'
                    WHEN expires_at <= @now THEN 'Job expired while leased.'
                    WHEN attempt_count >= @maximumAttempts THEN 'Maximum attempt count reached.'
                    ELSE 'Lease expired before a confirmed result; job was requeued.'
                END
            WHERE status IN ('Leased', 'Executing') AND lease_until <= @now;
            """;
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("maximumAttempts", maximumAttempts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoreMutationResult> MutateAsync(
        string id,
        string sql,
        string workerId,
        string leaseToken,
        DateTimeOffset now,
        byte[]? payload,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("workerId", workerId);
        command.Parameters.AddWithValue("leaseToken", leaseToken);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        if (payload is not null)
        {
            command.Parameters.AddWithValue("payload", payload);
        }

        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1
            ? StoreMutationResult.Success
            : await ExistsAsync(connection, id, cancellationToken).ConfigureAwait(false)
                ? StoreMutationResult.Conflict
                : StoreMutationResult.NotFound;
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM tapir_slmp.jobs WHERE id = @id);";
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private static async Task<StoredJob?> GetWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM tapir_slmp.jobs WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    private static StoredJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Enum.Parse<SlmpTransportKind>(reader.GetString(2)),
        Enum.Parse<JobStatus>(reader.GetString(3)),
        Enum.Parse<JobRisk>(reader.GetString(4)),
        checked((ushort)reader.GetInt32(5)),
        checked((ushort)reader.GetInt32(6)),
        reader.GetFieldValue<byte[]>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8),
        reader.GetInt32(9),
        AsOffset(reader.GetFieldValue<DateTime>(10)),
        AsOffset(reader.GetFieldValue<DateTime>(11)),
        reader.IsDBNull(12) ? null : AsOffset(reader.GetFieldValue<DateTime>(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : AsOffset(reader.GetFieldValue<DateTime>(15)),
        reader.IsDBNull(16) ? null : reader.GetString(16));

    private static DateTimeOffset AsOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private const string Columns = """
        id, route_key, transport, status, risk, command, subcommand,
        request_frame, response_frame, attempt_count, created_at, expires_at,
        lease_until, lease_token, worker_id, completed_at, error
        """;
}
