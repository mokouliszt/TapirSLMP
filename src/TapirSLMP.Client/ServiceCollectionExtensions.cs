using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TapirSLMP.Contracts;

namespace TapirSLMP.Client;

public static class ServiceCollectionExtensions
{
    public const string DefaultSectionName = "Freezer";

    /// <summary>
    /// Registers <see cref="FreezerClient"/> and an <see cref="HttpJobGateway"/> bound to it.
    /// The gateway is registered for both the submission and worker roles; a sealer only
    /// resolves the former and a liberator only the latter, so a token with one scope still
    /// fails at the freezer.
    /// </summary>
    public static IServiceCollection AddTapirFreezerClient(
        this IServiceCollection services,
        FreezerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddHttpClient<FreezerClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.AddSingleton<HttpJobGateway>();
        services.AddSingleton<IJobSubmissionGateway>(provider =>
            provider.GetRequiredService<HttpJobGateway>());
        services.AddSingleton<IJobWorkerGateway>(provider =>
            provider.GetRequiredService<HttpJobGateway>());
        return services;
    }

    public static IServiceCollection AddTapirFreezerClient(
        this IServiceCollection services,
        Action<FreezerClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new FreezerClientOptions();
        configure(options);
        return services.AddTapirFreezerClient(options);
    }

    public static IServiceCollection AddTapirFreezerClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DefaultSectionName) =>
        services.AddTapirFreezerClient(FreezerClientOptions.FromConfiguration(configuration, sectionName));
}
