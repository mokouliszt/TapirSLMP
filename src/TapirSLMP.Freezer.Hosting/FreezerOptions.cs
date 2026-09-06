using Microsoft.Extensions.Configuration;
using TapirSLMP.Contracts;
namespace TapirSLMP.Freezer.Hosting;

public sealed class FreezerOptions
{
    public string Provider { get; init; } = "Sqlite";

    public string SqliteConnectionString { get; init; } =
        "Data Source=data/tapirslmp.db;Cache=Shared;Pooling=True";

    public string PostgresConnectionString { get; init; } = string.Empty;

    public string SealerToken { get; init; } = string.Empty;

    public string LiberatorToken { get; init; } = string.Empty;

    public bool AllowStateChangingCommands { get; init; }

    public int DefaultJobTtlSeconds { get; init; } = 60;

    public int MaximumJobTtlSeconds { get; init; } = 300;

    public int MaximumAttempts { get; init; } = 3;

    public int MaximumLeaseSeconds { get; init; } = 120;

    /// <summary>
    /// Route keys this freezer serves, from <c>Freezer:KnownRouteKeys:0</c> and so on.
    /// Leaving it empty accepts any syntactically valid route key. Listing the routes makes
    /// a mistyped key fail immediately, on both submission and lease acquisition, instead of
    /// stalling until the job's TTL elapses. The list must agree with the route maps
    /// configured on the sealers and liberators; the freezer has no way to discover them.
    /// </summary>
    public IReadOnlySet<string> KnownRouteKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public int TerminalRetentionHours { get; init; } = 168;

    public static FreezerOptions FromConfiguration(
        IConfiguration configuration,
        string sectionName = "Freezer")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(sectionName);
        return new FreezerOptions
        {
            Provider = section["Provider"] ?? "Sqlite",
            SqliteConnectionString = section["SqliteConnectionString"] ??
                "Data Source=data/tapirslmp.db;Cache=Shared;Pooling=True",
            PostgresConnectionString = section["PostgresConnectionString"] ?? string.Empty,
            SealerToken = section["SealerToken"] ?? string.Empty,
            LiberatorToken = section["LiberatorToken"] ?? string.Empty,
            AllowStateChangingCommands = section.GetValue("AllowStateChangingCommands", false),
            DefaultJobTtlSeconds = section.GetValue("DefaultJobTtlSeconds", 60),
            MaximumJobTtlSeconds = section.GetValue("MaximumJobTtlSeconds", 300),
            MaximumAttempts = section.GetValue("MaximumAttempts", 3),
            MaximumLeaseSeconds = section.GetValue("MaximumLeaseSeconds", 120),
            TerminalRetentionHours = section.GetValue("TerminalRetentionHours", 168),
            KnownRouteKeys = section.GetSection("KnownRouteKeys").GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.Ordinal),
        };
    }

    /// <summary>Projects the transport-independent subset consumed by <see cref="JobAdmissionPolicy"/>.</summary>
    public JobPolicyOptions ToPolicyOptions() => new()
    {
        AllowStateChangingCommands = AllowStateChangingCommands,
        DefaultJobTtlSeconds = DefaultJobTtlSeconds,
        MaximumJobTtlSeconds = MaximumJobTtlSeconds,
        MaximumAttempts = MaximumAttempts,
        MaximumLeaseSeconds = MaximumLeaseSeconds,
        KnownRouteKeys = KnownRouteKeys,
    };

    public void Validate()
    {
        if (!string.Equals(Provider, "Sqlite", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Freezer:Provider must be Sqlite or Postgres.");
        }

        TokenAuthentication.ValidateConfiguredToken(SealerToken, "Freezer:SealerToken");
        TokenAuthentication.ValidateConfiguredToken(LiberatorToken, "Freezer:LiberatorToken");
        if (string.Equals(SealerToken, LiberatorToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sealer and liberator must use distinct bearer tokens.");
        }

        ToPolicyOptions().Validate();

        if (TerminalRetentionHours is < 1 or > 87_600)
        {
            throw new InvalidOperationException("Freezer:TerminalRetentionHours must be between 1 and 87600.");
        }

        if (string.Equals(Provider, "Postgres", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(PostgresConnectionString))
        {
            throw new InvalidOperationException(
                "Freezer:PostgresConnectionString is required when Provider is Postgres.");
        }
    }
}
