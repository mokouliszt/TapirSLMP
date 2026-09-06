using System.Net;
using Microsoft.Extensions.Configuration;
using TapirSLMP.Contracts;
using TapirSLMP.Sealer;
using Xunit;

namespace TapirSLMP.Sealer.Tests;

public sealed class SealerOptionsTests
{
    private static SealerOptions Bind(Dictionary<string, string?> values) =>
        SealerOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void FlatKeysStillConfigureASingleListener()
    {
        var options = Bind(new()
        {
            ["Sealer:Port"] = "5000",
            ["Sealer:RouteKey"] = "plc1",
            ["Sealer:ListenMode"] = "Both",
        });

        var listener = Assert.Single(options.Listeners);
        Assert.Equal(5000, listener.Port);
        Assert.Equal("plc1", listener.RouteKey);
        Assert.True(listener.ListensOn(SlmpTransportKind.Tcp));
        Assert.True(listener.ListensOn(SlmpTransportKind.Udp));
    }

    [Fact]
    public void ListenerArrayConfiguresOneEndpointPerPlc()
    {
        var options = Bind(new()
        {
            ["Sealer:Listeners:0:Port"] = "5000",
            ["Sealer:Listeners:0:RouteKey"] = "plc1",
            ["Sealer:Listeners:1:Port"] = "5001",
            ["Sealer:Listeners:1:RouteKey"] = "plc2",
            ["Sealer:Listeners:1:ListenMode"] = "Udp",
        });

        Assert.Equal(2, options.Listeners.Count);
        Assert.Equal("plc1", options.Listeners[0].RouteKey);
        Assert.Equal(5001, options.Listeners[1].Port);
        Assert.False(options.Listeners[1].ListensOn(SlmpTransportKind.Tcp));
        Assert.True(options.Listeners[1].ListensOn(SlmpTransportKind.Udp));
    }

    [Fact]
    public void TheListenerArrayWinsOverFlatKeys()
    {
        var options = Bind(new()
        {
            ["Sealer:Port"] = "9999",
            ["Sealer:RouteKey"] = "ignored",
            ["Sealer:Listeners:0:Port"] = "5000",
            ["Sealer:Listeners:0:RouteKey"] = "plc1",
        });

        var listener = Assert.Single(options.Listeners);
        Assert.Equal(5000, listener.Port);
        Assert.Equal("plc1", listener.RouteKey);
    }

    [Fact]
    public void TwoListenersOnTheSamePortAndProtocolAreRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Bind(new()
        {
            ["Sealer:Listeners:0:Port"] = "5000",
            ["Sealer:Listeners:0:RouteKey"] = "plc1",
            ["Sealer:Listeners:1:Port"] = "5000",
            ["Sealer:Listeners:1:RouteKey"] = "plc2",
        }));

        Assert.Contains("conflict", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TcpAndUdpMaySharePort()
    {
        var options = Bind(new()
        {
            ["Sealer:Listeners:0:Port"] = "5000",
            ["Sealer:Listeners:0:RouteKey"] = "plc1",
            ["Sealer:Listeners:0:ListenMode"] = "Tcp",
            ["Sealer:Listeners:1:Port"] = "5000",
            ["Sealer:Listeners:1:RouteKey"] = "plc2",
            ["Sealer:Listeners:1:ListenMode"] = "Udp",
        });

        Assert.Equal(2, options.Listeners.Count);
    }

    [Fact]
    public void AnUnsafeRouteKeyIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Bind(new()
        {
            ["Sealer:Listeners:0:Port"] = "5000",
            ["Sealer:Listeners:0:RouteKey"] = "-bad key",
        }));
    }

    [Fact]
    public void ListenersMayBindDifferentAddresses()
    {
        var options = Bind(new()
        {
            ["Sealer:Listeners:0:ListenAddress"] = "127.0.0.1",
            ["Sealer:Listeners:0:Port"] = "5000",
            ["Sealer:Listeners:0:RouteKey"] = "plc1",
            ["Sealer:Listeners:1:ListenAddress"] = "127.0.0.2",
            ["Sealer:Listeners:1:Port"] = "5000",
            ["Sealer:Listeners:1:RouteKey"] = "plc2",
        });

        Assert.Equal(IPAddress.Parse("127.0.0.2"), options.Listeners[1].ListenAddress);
    }
}
