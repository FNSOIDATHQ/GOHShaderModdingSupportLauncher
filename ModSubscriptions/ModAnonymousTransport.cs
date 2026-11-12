using System.Net;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal static class ModAnonymousTransport
{
    // Use the native Windows HTTP backend consistently in the published application.
    // Keep cookies, credentials and redirects under the same restrictions as the adapters.
    internal static WinHttpHandler Create(bool compressedContent = true) => new()
    {
        CookieUsePolicy = CookieUsePolicy.IgnoreCookies,
        ServerCredentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        AutomaticRedirection = false,
        AutomaticDecompression = compressedContent ? DecompressionMethods.GZip | DecompressionMethods.Deflate : DecompressionMethods.None,
        WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
        SendTimeout = TimeSpan.FromSeconds(30),
        ReceiveHeadersTimeout = TimeSpan.FromSeconds(30),
        ReceiveDataTimeout = TimeSpan.FromSeconds(30)
    };
}
