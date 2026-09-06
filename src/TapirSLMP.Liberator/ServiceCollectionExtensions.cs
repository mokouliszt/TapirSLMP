using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace TapirSLMP.Liberator;

public static class ServiceCollectionExtensions
{
    public const string DefaultSectionName = "Liberator";

    /// <summary>
    /// Registers the liberator worker loop as a hosted service. An
    /// <see cref="Contracts.IJobWorkerGateway"/> must already be registered — either the
    /// HTTP one from <c>TapirSLMP.Client</c> or the in-process one from <c>TapirSLMP.Storage</c>.
    /// </summary>
    public static IServiceCollection AddTapirLiberator(
        this IServiceCollection services,
        LiberatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SlmpTargetTransport>();
        services.AddHostedService<LiberatorService>();
        return services;
    }


    public static IServiceCollection AddTapirLiberator(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DefaultSectionName) =>
        services.AddTapirLiberator(LiberatorOptions.FromConfiguration(configuration, sectionName));
}
