using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace TapirSLMP.Freezer.Hosting;

public static class TokenAuthentication
{
    public static bool IsAuthorized(HttpRequest request, string expectedToken)
    {
        var header = request.Headers["Authorization"].ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suppliedToken = header[prefix.Length..];
        if (!IsSafeToken(suppliedToken))
        {
            return false;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    public static void ValidateConfiguredToken(string token, string settingName)
    {
        if (!IsSafeToken(token))
        {
            throw new InvalidOperationException(
                $"{settingName} must contain 32 to 512 URL-safe, non-whitespace characters.");
        }
    }

    private static bool IsSafeToken(string token) =>
        token.Length is >= 32 and <= 512 &&
        token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~');
}
