using Microsoft.AspNetCore.Http;
using TapirSLMP.Freezer;
using TapirSLMP.Protocol;
using Xunit;

namespace TapirSLMP.Freezer.Hosting.Tests;

public sealed class SecurityPolicyTests
{
    [Fact]
    public void DistinctLongTokensAreRequired()
    {
        var token = new string('a', 32);
        var options = new FreezerOptions
        {
            SealerToken = token,
            LiberatorToken = token,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void BearerTokenIsComparedSuccessfully()
    {
        var context = new DefaultHttpContext();
        var token = new string('z', 32);
        context.Request.Headers["Authorization"] = $"Bearer {token}";

        Assert.True(TokenAuthentication.IsAuthorized(context.Request, token));
        Assert.False(TokenAuthentication.IsAuthorized(context.Request, new string('y', 32)));
    }

    [Fact]
    public void UnknownCommandsAreTreatedAsStateChanging()
    {
        Assert.Equal(SlmpCommandRisk.StateChanging, SlmpCommandClassifier.Classify(0x9999));
    }
}
