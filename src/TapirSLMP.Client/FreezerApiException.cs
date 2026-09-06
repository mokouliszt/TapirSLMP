using System.Net;
using TapirSLMP.Contracts;

namespace TapirSLMP.Client;

public sealed class FreezerApiException : JobGatewayException
{
    public FreezerApiException(HttpStatusCode statusCode, string message)
        : this(statusCode, "freezer_api_error", message)
    {
    }

    public FreezerApiException(HttpStatusCode statusCode, string code, string message)
        : base(code, message, IsTransientStatus(statusCode))
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500 || code is 408 or 429 or (>= 200 and <= 299);
    }
}
