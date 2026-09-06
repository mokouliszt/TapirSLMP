using System.Net;
using System.Text;
using TapirSLMP.Contracts;
using TapirSLMP.Client;
using Xunit;

namespace TapirSLMP.Client.Tests;

public sealed class FreezerClientTests
{
    [Fact]
    public async Task EnqueueUsesBearerTokenAndReadsResponse()
    {
        var handler = new StubHandler(request =>
        {
            var authorization = request.Headers.Authorization
                ?? throw new InvalidOperationException("Authorization header was not set.");
            Assert.Equal("Bearer", authorization.Scheme);
            Assert.Equal(new string('a', 32), authorization.Parameter);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"id\":\"job-1\",\"createdAt\":\"2026-01-01T00:00:00Z\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var client = CreateClient(handler);

        var result = await client.EnqueueAsync(
            new EnqueueJobRequest("request-1", "plc1", SlmpTransportKind.Tcp, [1, 2]),
            default);

        Assert.Equal("job-1", result.Id);
    }

    [Fact]
    public async Task NoContentLeaseReturnsNull()
    {
        using var client = CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        var result = await client.AcquireAsync(
            new AcquireLeaseRequest("worker-1", ["plc1"]),
            default);

        Assert.Null(result);
    }

    [Fact]
    public void PlainHttpToRemoteHostIsRejected()
    {
        var options = new FreezerClientOptions
        {
            BaseAddress = new Uri("http://example.invalid"),
            Token = new string('a', 32),
            AllowInsecureHttpForLoopback = true,
        };

        using var httpClient = new HttpClient(new StubHandler(_ => throw new InvalidOperationException()));
        Assert.Throws<ArgumentException>(() => new FreezerClient(httpClient, options));
    }

    private static FreezerClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new FreezerClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5000"),
            Token = new string('a', 32),
            AllowInsecureHttpForLoopback = true,
        });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
