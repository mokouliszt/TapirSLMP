using Microsoft.Extensions.Configuration;
using TapirSLMP.Contracts;
using TapirSLMP.Protocol;

namespace TapirSLMP.Liberator;

public sealed class LiberatorRoute
{
    public required string RouteKey { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; } = 5007;

    public bool AllowTcp { get; init; } = true;

    public bool AllowUdp { get; init; }

    public bool PinSlmpRoute { get; init; } = true;

    public SlmpRoute ExpectedSlmpRoute { get; init; } = new(0, 0xFF, 0x03FF, 0);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool Allows(SlmpTransportKind transport) => transport switch
    {
        SlmpTransportKind.Tcp => AllowTcp,
        SlmpTransportKind.Udp => AllowUdp,
        _ => false,
    };
}

public sealed class LiberatorOptions
{
    public required string WorkerIdPrefix { get; init; }

    public required IReadOnlyDictionary<string, LiberatorRoute> Routes { get; init; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public int LeaseSeconds { get; init; } = 60;

    public int Parallelism { get; init; } = 1;

    public static LiberatorOptions FromConfiguration(
        IConfiguration configuration,
        string sectionName = "Liberator")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(sectionName);
        var routes = new Dictionary<string, LiberatorRoute>(StringComparer.Ordinal);
        foreach (var routeSection in section.GetSection("Routes").GetChildren())
        {
            var route = new LiberatorRoute
            {
                RouteKey = routeSection.Key,
                Host = routeSection["Host"] ?? string.Empty,
                Port = routeSection.GetValue("Port", 5007),
                AllowTcp = routeSection.GetValue("AllowTcp", true),
                AllowUdp = routeSection.GetValue("AllowUdp", false),
                PinSlmpRoute = routeSection.GetValue("PinSlmpRoute", true),
                ExpectedSlmpRoute = new SlmpRoute(
                    checked((byte)routeSection.GetValue("NetworkNumber", 0)),
                    checked((byte)routeSection.GetValue("PcNumber", 255)),
                    checked((ushort)routeSection.GetValue("DestinationModuleIo", 1023)),
                    checked((byte)routeSection.GetValue("DestinationModuleStation", 0))),
                ConnectTimeout = TimeSpan.FromSeconds(routeSection.GetValue("ConnectTimeoutSeconds", 5)),
                ResponseTimeout = TimeSpan.FromSeconds(routeSection.GetValue("ResponseTimeoutSeconds", 5)),
            };
            ValidateRoute(route);
            if (!routes.TryAdd(route.RouteKey, route))
            {
                throw new InvalidOperationException($"Duplicate liberator route: {route.RouteKey}");
            }
        }

        if (routes.Count == 0)
        {
            throw new InvalidOperationException("At least one Liberator:Routes entry is required.");
        }

        var configuredPrefix = section["WorkerIdPrefix"];
        var result = new LiberatorOptions
        {
            WorkerIdPrefix = string.IsNullOrWhiteSpace(configuredPrefix)
                ? $"liberator-{Guid.CreateVersion7():N}"
                : configuredPrefix,
            Routes = routes,
            PollInterval = TimeSpan.FromMilliseconds(section.GetValue("PollIntervalMilliseconds", 100)),
            LeaseSeconds = section.GetValue("LeaseSeconds", 60),
            Parallelism = section.GetValue("Parallelism", 1),
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (WorkerIdPrefix.Length is < 1 or > 120 ||
            WorkerIdPrefix.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new InvalidOperationException("Liberator:WorkerIdPrefix contains unsupported characters.");
        }

        if (PollInterval < TimeSpan.FromMilliseconds(10) || PollInterval > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("Liberator:PollIntervalMilliseconds is invalid.");
        }

        if (LeaseSeconds is < 5 or > 3600 || Parallelism is < 1 or > 64)
        {
            throw new InvalidOperationException("Liberator lease or parallelism setting is invalid.");
        }

        var leaseDuration = TimeSpan.FromSeconds(LeaseSeconds);
        if (Routes.Values.Any(route =>
                route.ConnectTimeout + route.ResponseTimeout + TimeSpan.FromSeconds(5) >= leaseDuration))
        {
            throw new InvalidOperationException(
                "Every route's connect and response timeout plus five seconds must be shorter than the freezer lease.");
        }
    }

    private static void ValidateRoute(LiberatorRoute route)
    {
        if (!IsSafeIdentifier(route.RouteKey, 128) ||
            string.IsNullOrWhiteSpace(route.Host) || route.Host.Length > 253 ||
            route.Port is < 1 or > 65535 ||
            (!route.AllowTcp && !route.AllowUdp) ||
            route.ConnectTimeout <= TimeSpan.Zero ||
            route.ConnectTimeout > TimeSpan.FromHours(1) ||
            route.ResponseTimeout <= TimeSpan.Zero ||
            route.ResponseTimeout > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException($"Liberator route '{route.RouteKey}' is invalid.");
        }
    }

    private static bool IsSafeIdentifier(string value, int maximumLength) =>
        value.Length is >= 1 && value.Length <= maximumLength &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    /// <summary>
    /// Verifies that connect, gateway, and PLC-response timeouts fit inside the lease with
    /// five seconds to spare, so a worker cannot still be running after its lease expires.
    /// </summary>
    public void ValidateAgainstGatewayTimeout(TimeSpan gatewayRequestTimeout)
    {
        if (gatewayRequestTimeout <= TimeSpan.Zero || gatewayRequestTimeout > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                "Freezer request timeout must be greater than zero and at most one hour.");
        }

        var leaseDuration = TimeSpan.FromSeconds(LeaseSeconds);
        if (Routes.Values.Any(route =>
                route.ConnectTimeout + gatewayRequestTimeout + route.ResponseTimeout +
                    TimeSpan.FromSeconds(5) >= leaseDuration))
        {
            throw new InvalidOperationException(
                "Liberator lease must exceed connect, freezer-request, and PLC-response timeouts plus five seconds.");
        }
    }
}
