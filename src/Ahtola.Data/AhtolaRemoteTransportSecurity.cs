namespace Ahtola;

internal static class AhtolaRemoteTransportSecurity
{
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
}
