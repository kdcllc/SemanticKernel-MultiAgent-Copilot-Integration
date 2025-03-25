using Microsoft.Kiota.Abstractions.Authentication;

namespace caps.util;

internal class AccessTokenProvider(string accessToken) : IAccessTokenProvider
{
    public Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        // get the token and return it in your own way
        return Task.FromResult(accessToken);
    }

    public AllowedHostsValidator AllowedHostsValidator { get; } = new AllowedHostsValidator();
}
