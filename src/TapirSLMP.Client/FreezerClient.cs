using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TapirSLMP.Contracts;

namespace TapirSLMP.Client;

public sealed class FreezerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HttpClient _httpClient;

    public FreezerClient(HttpClient httpClient, FreezerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        _httpClient = httpClient;
        _httpClient.BaseAddress = options.BaseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? options.BaseAddress
            : new Uri(options.BaseAddress.AbsoluteUri + "/");
        _httpClient.Timeout = options.RequestTimeout;
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TapirSLMP/0.1");
    }

    public async Task<EnqueueJobResponse> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "v1/jobs",
            request,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<EnqueueJobResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobView?> GetAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"v1/jobs/{Uri.EscapeDataString(id)}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadRequiredAsync<JobView>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeaseView?> AcquireAsync(
        AcquireLeaseRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "v1/leases/acquire",
            request,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        return await ReadRequiredAsync<LeaseView>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task MarkExecutingAsync(
        string id,
        LeaseActionRequest request,
        CancellationToken cancellationToken) =>
        PostWithoutResultAsync(
            $"v1/jobs/{Uri.EscapeDataString(id)}/executing",
            request,
            cancellationToken);

    public Task CompleteAsync(
        string id,
        CompleteJobRequest request,
        CancellationToken cancellationToken) =>
        PostWithoutResultAsync(
            $"v1/jobs/{Uri.EscapeDataString(id)}/complete",
            request,
            cancellationToken);

    public Task FailAsync(
        string id,
        FailJobRequest request,
        CancellationToken cancellationToken) =>
        PostWithoutResultAsync(
            $"v1/jobs/{Uri.EscapeDataString(id)}/fail",
            request,
            cancellationToken);

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task PostWithoutResultAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            path,
            value,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FreezerApiException(response.StatusCode, "The freezer returned an empty response body.");
    }

    private static async Task ThrowApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ErrorResponse? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }

        throw new FreezerApiException(
            response.StatusCode,
            error?.Message ?? $"Freezer request failed with HTTP {(int)response.StatusCode}.");
    }

    private static void Validate(FreezerClientOptions options)
    {
        if (!options.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("Freezer base address must be absolute.", nameof(options));
        }

        if (!string.IsNullOrEmpty(options.BaseAddress.UserInfo) ||
            !string.IsNullOrEmpty(options.BaseAddress.Query) ||
            !string.IsNullOrEmpty(options.BaseAddress.Fragment))
        {
            throw new ArgumentException(
                "Freezer base address must not contain credentials, a query, or a fragment.",
                nameof(options));
        }

        if (!string.Equals(options.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            var loopback = string.Equals(options.BaseAddress.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(options.BaseAddress.Host, out var address) && IPAddress.IsLoopback(address);
            if (!options.AllowInsecureHttpForLoopback || !loopback ||
                !string.Equals(options.BaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Freezer must use HTTPS. Plain HTTP may only be enabled explicitly for a loopback test endpoint.",
                    nameof(options));
            }
        }

        if (options.Token.Length is < 32 or > 512 ||
            options.Token.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~')))
        {
            throw new ArgumentException(
                "Freezer token must contain 32 to 512 URL-safe characters.",
                nameof(options));
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
