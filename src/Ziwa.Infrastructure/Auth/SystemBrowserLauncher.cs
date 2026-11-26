using System.Diagnostics;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Auth;

namespace Ziwa.Infrastructure.Auth;

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task OpenUrlAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthException(AuthErrorCode.OAuthFailed, "رابط المتصفح غير مدعوم لأسباب أمنية");
        }

        var psi = new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        };
        Process.Start(psi);
        return Task.CompletedTask;
    }
}
