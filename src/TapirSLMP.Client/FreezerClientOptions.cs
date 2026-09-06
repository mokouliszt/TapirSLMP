using Microsoft.Extensions.Configuration;

namespace TapirSLMP.Client;

public sealed class FreezerClientOptions
{
    public Uri BaseAddress { get; set; } = new("https://127.0.0.1:7443");

    public string Token { get; set; } = string.Empty;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool AllowInsecureHttpForLoopback { get; set; }

    /// <summary>
    /// Binds from a configuration section. The section name is a parameter so that a host
    /// application embedding more than one client, or already using "Freezer" for something
    /// else, can point this at its own key.
    /// </summary>
    public static FreezerClientOptions FromConfiguration(
        IConfiguration configuration,
        string sectionName = "Freezer")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(sectionName);
        return new FreezerClientOptions
        {
            BaseAddress = new Uri(section["BaseAddress"] ?? "https://127.0.0.1:7443"),
            Token = section["Token"] ?? string.Empty,
            RequestTimeout = TimeSpan.FromSeconds(section.GetValue("RequestTimeoutSeconds", 15)),
            AllowInsecureHttpForLoopback = section.GetValue("AllowInsecureHttpForLoopback", false),
        };
    }
}
