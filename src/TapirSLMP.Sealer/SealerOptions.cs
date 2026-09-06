using System.Net;
using Microsoft.Extensions.Configuration;
using TapirSLMP.Contracts;

namespace TapirSLMP.Sealer;

public enum SealerListenMode
{
    Tcp,
    Udp,
    Both,
}

/// <summary>
/// One socket endpoint pinned to one route key. A client selects which PLC it talks to by
/// choosing which listener it connects to; it can never name a PLC address itself.
/// </summary>
public sealed class SealerListener
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    public int Port { get; init; } = 5000;

    public SealerListenMode ListenMode { get; init; } = SealerListenMode.Tcp;

    public string RouteKey { get; init; } = "plc1";

    public bool ListensOn(SlmpTransportKind transport) => ListenMode switch
    {
        SealerListenMode.Both => true,
        SealerListenMode.Tcp => transport == SlmpTransportKind.Tcp,
        SealerListenMode.Udp => transport == SlmpTransportKind.Udp,
        _ => false,
    };

    public void Validate()
    {
        if (Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException($"Sealer listener port {Port} is invalid.");
        }

        if (!SealerOptions.IsSafeIdentifier(RouteKey, 128))
        {
            throw new InvalidOperationException($"Sealer listener route key '{RouteKey}' is invalid.");
        }
    }

    public override string ToString() => $"{ListenAddress}:{Port} ({ListenMode}) -> {RouteKey}";
}

public sealed class SealerOptions
{
    /// <summary>
    /// One entry per PLC. Timeouts and the concurrency limit below are shared by all of them.
    /// </summary>
    public IReadOnlyList<SealerListener> Listeners { get; init; } = [new SealerListener()];

    public TimeSpan FrameReceiveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Applies per listener, not across all of them.</summary>
    public int MaximumConcurrentRequests { get; init; } = 128;

    /// <summary>
    /// Binds from configuration. Two shapes are accepted. A <c>Listeners</c> array configures
    /// one endpoint per PLC:
    /// <code>
    /// Sealer:Listeners:0:Port=5000
    /// Sealer:Listeners:0:RouteKey=plc1
    /// Sealer:Listeners:1:Port=5001
    /// Sealer:Listeners:1:RouteKey=plc2
    /// </code>
    /// If that array is absent, the flat <c>Port</c>, <c>ListenAddress</c>, <c>ListenMode</c>,
    /// and <c>RouteKey</c> keys configure a single listener, which is the common case.
    /// </summary>
    public static SealerOptions FromConfiguration(
        IConfiguration configuration,
        string sectionName = "Sealer")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(sectionName);
        var listenerSections = section.GetSection("Listeners").GetChildren().ToArray();
        var listeners = listenerSections.Length > 0
            ? listenerSections
                .Select(child => ReadListener(child, $"{sectionName}:Listeners:{child.Key}"))
                .ToArray()
            : [ReadListener(section, sectionName)];

        var result = new SealerOptions
        {
            Listeners = listeners,
            FrameReceiveTimeout = TimeSpan.FromSeconds(section.GetValue("FrameReceiveTimeoutSeconds", 30)),
            JobTimeout = TimeSpan.FromSeconds(section.GetValue("JobTimeoutSeconds", 60)),
            PollInterval = TimeSpan.FromMilliseconds(section.GetValue("PollIntervalMilliseconds", 50)),
            MaximumConcurrentRequests = section.GetValue("MaximumConcurrentRequests", 128),
        };
        result.Validate();
        return result;
    }

    private static SealerListener ReadListener(IConfiguration section, string path)
    {
        var addressText = section["ListenAddress"] ?? "127.0.0.1";
        if (!IPAddress.TryParse(addressText, out var address))
        {
            throw new InvalidOperationException($"{path}:ListenAddress must be an IP address.");
        }

        if (!Enum.TryParse<SealerListenMode>(section["ListenMode"] ?? "Tcp", true, out var mode))
        {
            throw new InvalidOperationException($"{path}:ListenMode must be Tcp, Udp, or Both.");
        }

        return new SealerListener
        {
            ListenAddress = address,
            Port = section.GetValue("Port", 5000),
            ListenMode = mode,
            RouteKey = section["RouteKey"] ?? "plc1",
        };
    }

    public void Validate()
    {
        if (Listeners.Count is < 1 or > 256)
        {
            throw new InvalidOperationException("Sealer must define between 1 and 256 listeners.");
        }

        foreach (var listener in Listeners)
        {
            listener.Validate();
        }

        // TCP and UDP may share a port, but two listeners must not claim the same address,
        // port, and protocol: the second bind would fail at startup, and a route key
        // silently shadowed here would be hard to diagnose.
        foreach (var transport in new[] { SlmpTransportKind.Tcp, SlmpTransportKind.Udp })
        {
            var duplicate = Listeners
                .Where(listener => listener.ListensOn(transport))
                .GroupBy(listener => (listener.ListenAddress, listener.Port))
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException(
                    $"Sealer listeners conflict on {duplicate.Key.ListenAddress}:{duplicate.Key.Port} for {transport}.");
            }
        }

        if (FrameReceiveTimeout <= TimeSpan.Zero ||
            JobTimeout <= TimeSpan.Zero ||
            PollInterval < TimeSpan.FromMilliseconds(10) ||
            PollInterval > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException("Sealer timeout or poll interval setting is invalid.");
        }

        if (MaximumConcurrentRequests is < 1 or > 10_000)
        {
            throw new InvalidOperationException("Sealer:MaximumConcurrentRequests is invalid.");
        }
    }

    internal static bool IsSafeIdentifier(string value, int maximumLength) =>
        value.Length is >= 1 && value.Length <= maximumLength &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    /// <summary>
    /// Verifies that a single gateway call cannot outlive the job deadline. Call this when
    /// the relay talks to a remote freezer over HTTP.
    /// </summary>
    public void ValidateAgainstGatewayTimeout(TimeSpan gatewayRequestTimeout)
    {
        if (gatewayRequestTimeout <= TimeSpan.Zero || gatewayRequestTimeout >= JobTimeout)
        {
            throw new InvalidOperationException(
                "Freezer request timeout must be positive and shorter than the sealer job timeout.");
        }
    }
}
