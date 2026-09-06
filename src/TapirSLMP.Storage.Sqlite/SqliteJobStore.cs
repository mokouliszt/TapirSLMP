using System.Data;
using Microsoft.Data.Sqlite;
using TapirSLMP.Contracts;
using TapirSLMP.Storage;

namespace TapirSLMP.Storage.Sqlite;

public sealed class SqliteJobStore : IJobStore
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;

    public SqliteJobStore(SqliteJobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
        if (string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "The freezer requires durable SQLite storage; in-memory databases are not supported.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new ArgumentException("SQLite Data Source must not be empty.", nameof(options));
        }

        var fullPath = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        builder.DataSource = fullPath;
        builder.ForeignKeys = true;
        _connectionString = builder.ToString();
        _busyTimeoutMilliseconds = options.BusyTimeoutMilliseconds is >= 0 and <= 300_000
            ? options.BusyTimeoutMilliseconds
            : throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT NOT NULL PRIMARY KEY,
                route_key TEXT NOT NULL,
                transport TEXT NOT NULL,
                status TEXT NOT NULL,
                risk TEXT NOT NULL,
                command INTEGER NOT NULL,
                subcommand INTEGER NOT NULL,
                request_frame BLOB NOT NULL,
                response_frame BLOB NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                lease_until TEXT NULL,
                lease_token TEXT NULL,
                worker_id TEXT NULL,
                completed_at TEXT NULL,
                error TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_jobs_fifo
                ON jobs(status, created_at, id);
            CREATE INDEX IF NOT EXISTS ix_jobs_expiry
                ON jobs(status, expires_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_route
                ON jobs(route_key)
                WHERE status IN ('Leased', 'Executing');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryAddAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO jobs (
                id, route_key, transport, status, risk, command, subcommand,
                request_frame, attempt_count, created_at, expires_at)
            VALUES (
                $id, $routeKey, $transport, 'Queued', $risk, $command, $subcommand,
                $requestFrame, 0, $createdAt, $expiresAt);
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$routeKey", job.RouteKey);
        command.Parameters.AddWithValue("$transport", job.Transport.ToString());
        command.Parameters.AddWithValue("$risk", job.Risk.ToString());
        command.Parameters.AddWithValue("$command", (int)job.Command);
        command.Parameters.AddWithValue("$subcommand", (int)job.Subcommand);
        command.Parameters.AddWithValue("$requestFrame", job.RequestFrame);
        command.Parameters.AddWithValue("$createdAt", StoreUtilities.ToDatabaseTime(job.CreatedAt));
        command.Parameters.AddWithValue("$expiresAt", StoreUtilities.ToDatabaseTime(job.ExpiresAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<StoredJob?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await ExpireAndRecoverAsync(
            connection,
            transaction,
            StoreUtilities.ToDatabaseTime(now),
            maximumAttempts,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneAsync(
        DateTimeOffset completedBefore,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM jobs
            WHERE status IN ('Completed', 'Failed', 'Expired', 'OutcomeUnknown')
              AND completed_at < $completedBefore;
            """;
        command.Parameters.AddWithValue(
            "$completedBefore",
            StoreUtilities.ToDatabaseTime(completedBefore));
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var nowText = StoreUtilities.ToDatabaseTime(now);

        await ExpireAndRecoverAsync(
            connection,
            transaction,
            nowText,
            maximumAttempts,
            cancellationToken).ConfigureAwait(false);

        var routeParameterNames = routeKeys.Select((_, index) => $"$route{index}").ToArray();
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"""
            SELECT id
            FROM jobs
            WHERE status = 'Queued'
              AND expires_at > $now
              AND attempt_count < $maximumAttempts
              AND route_key IN ({string.Join(", ", routeParameterNames)})
              AND NOT EXISTS (
                  SELECT 1
                  FROM jobs AS active
                  WHERE active.route_key = jobs.route_key
                    AND active.status IN ('Leased', 'Executing'))
            ORDER BY created_at, id
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$now", nowText);
        select.Parameters.AddWithValue("$maximumAttempts", maximumAttempts);
        var routeIndex = 0;
        foreach (var routeKey in routeKeys)
        {
            select.Parameters.AddWithValue(routeParameterNames[routeIndex++], routeKey);
        }

        var id = (string?)await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var leaseToken = StoreUtilities.NewLeaseToken();
        var leaseUntil = now + leaseDuration;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE jobs
            SET status = 'Leased',
                attempt_count = attempt_count + 1,
                lease_until = $leaseUntil,
                lease_token = $leaseToken,
                worker_id = $workerId,
                error = NULL
            WHERE id = $id AND status = 'Queued';
            """;
        update.Parameters.AddWithValue("$leaseUntil", StoreUtilities.ToDatabaseTime(leaseUntil));
        update.Parameters.AddWithValue("$leaseToken", leaseToken);
        update.Parameters.AddWithValue("$workerId", workerId);
        update.Parameters.AddWithValue("$id", id);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
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
            UPDATE jobs
            SET status = 'Executing'
            WHERE id = $id
              AND status = 'Leased'
              AND worker_id = $workerId
              AND lease_token = $leaseToken
              AND lease_until > $now
              AND expires_at > $now;
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
            UPDATE jobs
            SET status = 'Completed',
                response_frame = $payload,
                completed_at = $now,
                lease_until = NULL,
                lease_token = NULL,
                worker_id = NULL,
                error = NULL
            WHERE id = $id
              AND status = 'Executing'
              AND worker_id = $workerId
              AND lease_token = $leaseToken
              AND lease_until > $now;
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET status = CASE
                    WHEN $disposition = 'OutcomeUnknown' THEN 'OutcomeUnknown'
                    WHEN $disposition = 'Retry'
                         AND NOT (status = 'Executing' AND risk = 'StateChanging')
                         AND attempt_count < $maximumAttempts
                         AND expires_at > $now THEN 'Queued'
                    WHEN $disposition = 'Retry'
                         AND status = 'Executing'
                         AND risk = 'StateChanging' THEN 'OutcomeUnknown'
                    ELSE 'Failed'
                END,
                completed_at = CASE
                    WHEN $disposition = 'Retry'
                         AND NOT (status = 'Executing' AND risk = 'StateChanging')
                         AND attempt_count < $maximumAttempts
                         AND expires_at > $now THEN NULL
                    ELSE $now
                END,
                lease_until = NULL,
                lease_token = NULL,
                worker_id = NULL,
                error = $error
            WHERE id = $id
              AND status IN ('Leased', 'Executing')
              AND worker_id = $workerId
              AND lease_token = $leaseToken
              AND lease_until > $now;
            """;
        command.Parameters.AddWithValue("$disposition", disposition.ToString());
        command.Parameters.AddWithValue("$maximumAttempts", maximumAttempts);
        command.Parameters.AddWithValue("$now", StoreUtilities.ToDatabaseTime(now));
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workerId", workerId);
        command.Parameters.AddWithValue("$leaseToken", leaseToken);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1
            ? StoreMutationResult.Success
            : await ExistsAsync(connection, id, cancellationToken).ConfigureAwait(false)
                ? StoreMutationResult.Conflict
                : StoreMutationResult.NotFound;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA busy_timeout = {_busyTimeoutMilliseconds};
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExpireAndRecoverAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string now,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var expire = connection.CreateCommand();
        expire.Transaction = transaction;
        expire.CommandText = """
            UPDATE jobs
            SET status = 'Expired', completed_at = $now, error = 'Job expired before execution.'
            WHERE status = 'Queued' AND expires_at <= $now;

            UPDATE jobs
            SET status = CASE
                    WHEN status = 'Executing' AND risk = 'StateChanging' THEN 'OutcomeUnknown'
                    WHEN expires_at <= $now THEN 'Expired'
                    WHEN attempt_count >= $maximumAttempts THEN 'Failed'
                    ELSE 'Queued'
                END,
                completed_at = CASE
                    WHEN status = 'Executing' AND risk = 'StateChanging' THEN $now
                    WHEN expires_at <= $now THEN $now
                    WHEN attempt_count >= $maximumAttempts THEN $now
                    ELSE NULL
                END,
                lease_until = NULL,
                lease_token = NULL,
                worker_id = NULL,
                error = CASE
                    WHEN status = 'Executing' AND risk = 'StateChanging'
                        THEN 'Lease expired after execution began; PLC outcome is unknown.'
                    WHEN expires_at <= $now THEN 'Job expired while leased.'
                    WHEN attempt_count >= $maximumAttempts THEN 'Maximum attempt count reached.'
                    ELSE 'Lease expired before a confirmed result; job was requeued.'
                END
            WHERE status IN ('Leased', 'Executing') AND lease_until <= $now;
            """;
        expire.Parameters.AddWithValue("$now", now);
        expire.Parameters.AddWithValue("$maximumAttempts", maximumAttempts);
        await expire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workerId", workerId);
        command.Parameters.AddWithValue("$leaseToken", leaseToken);
        command.Parameters.AddWithValue("$now", StoreUtilities.ToDatabaseTime(now));
        if (payload is not null)
        {
            command.Parameters.AddWithValue("$payload", payload);
        }

        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1
            ? StoreMutationResult.Success
            : await ExistsAsync(connection, id, cancellationToken).ConfigureAwait(false)
                ? StoreMutationResult.Conflict
                : StoreMutationResult.NotFound;
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM jobs WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<StoredJob?> GetWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    private static StoredJob ReadJob(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Enum.Parse<SlmpTransportKind>(reader.GetString(2)),
        Enum.Parse<JobStatus>(reader.GetString(3)),
        Enum.Parse<JobRisk>(reader.GetString(4)),
        checked((ushort)reader.GetInt64(5)),
        checked((ushort)reader.GetInt64(6)),
        (byte[])reader[7],
        reader.IsDBNull(8) ? null : (byte[])reader[8],
        reader.GetInt32(9),
        StoreUtilities.FromDatabaseTime(reader.GetString(10)),
        StoreUtilities.FromDatabaseTime(reader.GetString(11)),
        reader.IsDBNull(12) ? null : StoreUtilities.FromDatabaseTime(reader.GetString(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : StoreUtilities.FromDatabaseTime(reader.GetString(15)),
        reader.IsDBNull(16) ? null : reader.GetString(16));

    private const string Columns = """
        id, route_key, transport, status, risk, command, subcommand,
        request_frame, response_frame, attempt_count, created_at, expires_at,
        lease_until, lease_token, worker_id, completed_at, error
        """;
}
