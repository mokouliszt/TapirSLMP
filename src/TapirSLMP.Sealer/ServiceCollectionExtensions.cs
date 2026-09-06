using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TapirSLMP.Contracts;

namespace TapirSLMP.Sealer;

public static class ServiceCollectionExtensions
{
    public const string DefaultSectionName = "Sealer";

    /// <summary>
    /// Registers <see cref="ISlmpRelay"/>. Call this when the application drives the relay
    /// itself; it does not start any listener. Pass the route key on each
    /// <see cref="ISlmpRelay.RelayAsync"/> call.
    /// </summary>
    public static IServiceCollection AddTapirSealerRelay(
        this IServiceCollection services,
        SealerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISlmpRelay, SlmpRelay>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="ISlmpRelay"/> plus one hosted listener per entry in
    /// <see cref="SealerOptions.Listeners"/>. A deployment with several PLCs exposes one
    /// port per route key, and clients pick the PLC by choosing the port they connect to.
    /// </summary>
    public static IServiceCollection AddTapirSealer(
        this IServiceCollection services,
        SealerOptions options)
    {
        services.AddTapirSealerRelay(options);
        foreach (var listener in options.Listeners)
        {
            var pinned = listener;
            if (pinned.ListensOn(SlmpTransportKind.Tcp))
            {
                services.AddSingleton<IHostedService>(provider => new TcpSealerService(
                    provider.GetRequiredService<ISlmpRelay>(),
                    options,
                    pinned,
                    provider.GetRequiredService<ILogger<TcpSealerService>>()));
            }

            if (pinned.ListensOn(SlmpTransportKind.Udp))
            {
                services.AddSingleton<IHostedService>(provider => new UdpSealerService(
                    provider.GetRequiredService<ISlmpRelay>(),
                    options,
                    pinned,
                    provider.GetRequiredService<ILogger<UdpSealerService>>()));
            }
        }

        return services;
    }


    public static IServiceCollection AddTapirSealer(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DefaultSectionName) =>
        services.AddTapirSealer(SealerOptions.FromConfiguration(configuration, sectionName));
}
