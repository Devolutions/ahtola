namespace Ahtola;

internal static class AhtolaRemoteTransportSecurity
{
    private const int MaximumRedirects = 5;

    public static void Validate(
        Uri endpoint,
        string? authToken,
        bool remoteEncryptionConfigured)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || endpoint.IsLoopback)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            throw new InvalidOperationException(
                "Auth Token requires an HTTPS remote Ahtola URL unless the host is localhost or loopback.");
        }

        if (remoteEncryptionConfigured)
        {
            throw new InvalidOperationException(
                "Remote encryption requires an HTTPS remote Ahtola URL unless the host is localhost or loopback.");
        }
    }

    public static void ValidateRedirectContract(
        bool automaticRedirectsDisabled,
        bool remoteEncryptionConfigured)
    {
        if (remoteEncryptionConfigured && !automaticRedirectsDisabled)
        {
            throw new InvalidOperationException(
                "Remote encryption requires an HTTP transport that guarantees automatic redirects are disabled.");
        }
    }

    public static HttpClient CreateRedirectSafeHttpClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false });

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri initialUri,
        Func<Uri, HttpRequestMessage> requestFactory,
        string? authToken,
        bool remoteEncryptionConfigured,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(initialUri);
        ArgumentNullException.ThrowIfNull(requestFactory);

        var requestUri = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            Validate(requestUri, authToken, remoteEncryptionConfigured);

            using var request = requestFactory(requestUri);
            var response = await client
                .SendAsync(request, completionOption, cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
                return response;

            using (response)
            {
                if (response.StatusCode is not (System.Net.HttpStatusCode.TemporaryRedirect
                    or System.Net.HttpStatusCode.PermanentRedirect))
                {
                    throw new InvalidOperationException(
                        "Remote Ahtola requests follow only HTTP 307 and 308 redirects so the request method and body are preserved.");
                }

                var location = response.Headers.Location
                    ?? throw new InvalidOperationException(
                        "The remote Ahtola redirect response did not include a Location header.");

                if (redirectCount >= MaximumRedirects)
                {
                    throw new InvalidOperationException(
                        $"The remote Ahtola request exceeded the maximum of {MaximumRedirects} redirects.");
                }

                var destination = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
                Validate(destination, authToken, remoteEncryptionConfigured);
                ValidateRedirectOrigin(
                    requestUri,
                    destination,
                    credentialsConfigured: !string.IsNullOrWhiteSpace(authToken) || remoteEncryptionConfigured);
                requestUri = destination;
            }
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private static void ValidateRedirectOrigin(
        Uri source,
        Uri destination,
        bool credentialsConfigured)
    {
        if (!credentialsConfigured
            || source.Scheme.Equals(destination.Scheme, StringComparison.OrdinalIgnoreCase)
                && source.IdnHost.Equals(destination.IdnHost, StringComparison.OrdinalIgnoreCase)
                && source.Port == destination.Port)
        {
            return;
        }

        throw new InvalidOperationException(
            "Credential-bearing remote Ahtola requests cannot redirect to a different origin.");
    }
}
