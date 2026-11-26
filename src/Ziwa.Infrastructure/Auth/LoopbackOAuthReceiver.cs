using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Auth;

namespace Ziwa.Infrastructure.Auth;

public sealed record OAuthTokenResult(string AccessToken, string RefreshToken);

public sealed class LoopbackOAuthReceiver : IAsyncDisposable
{
    private const int MaxExchangeBodyBytes = 64 * 1024;
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TaskCompletionSource<OAuthTokenResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _allowedOrigins;
    private readonly Task _acceptLoopTask;
    private int _exchangedFlag;
    private bool _disposed;

    public LoopbackOAuthReceiver()
    {
        byte[] stateBytes = RandomNumberGenerator.GetBytes(16);
        ExpectedState = Convert.ToHexStringLower(stateBytes);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        CallbackUri = new Uri($"http://127.0.0.1:{Port}/callback", UriKind.Absolute);

        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"http://127.0.0.1:{Port}",
            $"http://localhost:{Port}",
        };

        _acceptLoopTask = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public Uri CallbackUri { get; }
    public string ExpectedState { get; }

    public static Uri BuildSupabaseGoogleAuthorizeUri(Uri supabaseBaseUri, Uri callbackUri)
    {
        ArgumentNullException.ThrowIfNull(supabaseBaseUri);
        ArgumentNullException.ThrowIfNull(callbackUri);
        // Do NOT forward a client state parameter to GoTrue (GoTrue treats state as flow_state UUID).
        string encodedRedirect = Uri.EscapeDataString(callbackUri.AbsoluteUri);
        return new Uri(supabaseBaseUri, $"/auth/v1/authorize?provider=google&redirect_to={encodedRedirect}");
    }

    public async Task<OAuthTokenResult> WaitForTokensAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var reg = linked.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(cancellationToken);
            }
            else
            {
                _tcs.TrySetException(new AuthException(
                    AuthErrorCode.OAuthTimeout,
                    "انتهى وقت تسجيل الدخول (2 دقيقة). يرجى المحاولة مرة أخرى"));
            }
        });

        return await _tcs.Task;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdownCts.Token);
                _ = HandleConnectionAsync(client);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using TcpClient ownedClient = client;
        ownedClient.ReceiveTimeout = 10_000;
        ownedClient.SendTimeout = 10_000;

        try
        {
            await using NetworkStream stream = ownedClient.GetStream();
            ParsedHttpRequest? request = await ReadHttpRequestAsync(stream, _shutdownCts.Token);
            if (request is null)
            {
                return;
            }

            if (string.Equals(request.Path, "/callback", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                string html = BuildCallbackHtml(ExpectedState);
                await WriteHttpResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", html, origin: null);
                return;
            }

            if (string.Equals(request.Path, "/exchange", StringComparison.OrdinalIgnoreCase))
            {
                await HandleExchangeAsync(stream, request);
                return;
            }

            await WriteHttpResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not Found", origin: null);
        }
        catch
        {
        }
    }

    private async Task HandleExchangeAsync(NetworkStream stream, ParsedHttpRequest request)
    {
        string? origin = request.Headers.TryGetValue("origin", out string? rawOrigin) ? rawOrigin : null;
        if (string.IsNullOrWhiteSpace(origin) || !_allowedOrigins.Contains(origin))
        {
            await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Forbidden Origin", origin: null);
            return;
        }

        if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHttpResponseAsync(stream, 200, "OK", "text/plain; charset=utf-8", string.Empty, origin);
            return;
        }

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHttpResponseAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8", "Method Not Allowed", origin);
            return;
        }

        var form = HttpUtility.ParseQueryString(request.BodyText);
        string? suppliedState = form["state"];
        if (!FixedTimeEquals(suppliedState, ExpectedState))
        {
            await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Invalid state", origin);
            return;
        }

        if (Interlocked.CompareExchange(ref _exchangedFlag, 1, 0) != 0)
        {
            await WriteHttpResponseAsync(stream, 409, "Conflict", "text/plain; charset=utf-8", "Already exchanged", origin);
            return;
        }

        string accessToken = form["access_token"] ?? string.Empty;
        string refreshToken = form["refresh_token"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            _tcs.TrySetException(new AuthException(AuthErrorCode.OAuthFailed, "لم يتم استلام رمز الدخول من Google"));
            await WriteHttpResponseAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Missing access_token", origin);
            return;
        }

        _tcs.TrySetResult(new OAuthTokenResult(accessToken.Trim(), refreshToken.Trim()));
        await WriteHttpResponseAsync(stream, 200, "OK", "text/plain; charset=utf-8", "OK", origin);
    }

    private static async Task<ParsedHttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var headerBuffer = new MemoryStream();
        byte[] single = new byte[1024];
        int headerEndIdx = -1;

        while (headerEndIdx < 0 && headerBuffer.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(single, ct);
            if (read == 0)
            {
                return null;
            }

            headerBuffer.Write(single, 0, read);
            byte[] spanBytes = headerBuffer.GetBuffer();
            int len = (int)headerBuffer.Length;
            for (int i = 3; i < len; i++)
            {
                if (spanBytes[i - 3] == '\r' && spanBytes[i - 2] == '\n' && spanBytes[i - 1] == '\r' && spanBytes[i] == '\n')
                {
                    headerEndIdx = i + 1;
                    break;
                }
            }
        }

        if (headerEndIdx < 0)
        {
            return null;
        }

        byte[] raw = headerBuffer.ToArray();
        string headerText = Encoding.ASCII.GetString(raw, 0, headerEndIdx);
        string[] lines = headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        string[] requestLineParts = lines[0].Split(' ', 3);
        if (requestLineParts.Length < 2)
        {
            return null;
        }

        string method = requestLineParts[0].Trim();
        string rawTarget = requestLineParts[1].Trim();
        int qIdx = rawTarget.IndexOf('?');
        string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim().ToLowerInvariant()] = lines[i][(colon + 1)..].Trim();
            }
        }

        int contentLength = 0;
        if (headers.TryGetValue("content-length", out string? clStr) &&
            int.TryParse(clStr, out int parsedCl) &&
            parsedCl > 0)
        {
            if (parsedCl > MaxExchangeBodyBytes)
            {
                return null;
            }

            contentLength = parsedCl;
        }

        string bodyText = string.Empty;
        if (contentLength > 0)
        {
            byte[] bodyBytes = new byte[contentLength];
            int alreadyRead = Math.Min(contentLength, raw.Length - headerEndIdx);
            if (alreadyRead > 0)
            {
                Buffer.BlockCopy(raw, headerEndIdx, bodyBytes, 0, alreadyRead);
            }

            int offset = alreadyRead;
            while (offset < contentLength)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), ct);
                if (n == 0)
                {
                    break;
                }

                offset += n;
            }

            bodyText = Encoding.UTF8.GetString(bodyBytes, 0, offset);
        }

        return new ParsedHttpRequest(method, path, headers, bodyText);
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string contentType,
        string body,
        string? origin)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        sb.Append("Connection: close\r\n");
        if (!string.IsNullOrEmpty(origin))
        {
            sb.Append($"Access-Control-Allow-Origin: {origin}\r\n");
            sb.Append("Access-Control-Allow-Methods: POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        }

        sb.Append("\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes);
        }

        await stream.FlushAsync();
    }

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        byte[] bytesA = Encoding.UTF8.GetBytes(a);
        byte[] bytesB = Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }

    private static string BuildCallbackHtml(string expectedState) =>
        $$"""
        <!DOCTYPE html>
        <html lang="ar" dir="rtl">
        <head>
            <meta charset="UTF-8">
            <title>جاري تسجيل الدخول...</title>
        </head>
        <body style="font-family: system-ui, sans-serif; text-align: center; margin-top: 50px; background-color: #0f172a; color: #f8fafc;">
            <div style="margin: auto; background-color: #1e293b; border: 1px solid #334155; padding: 30px; border-radius: 16px; max-width: 420px;">
                <h2 id="msg">جاري مصادقة الحساب، يرجى الانتظار...</h2>
                <p style="color: #94a3b8;">يمكنك العودة إلى تطبيق Ziwa Studio بعد نجاح المصادقة.</p>
            </div>
            <script>
                const hash = window.location.hash;
                const params = new URLSearchParams(window.location.search);
                const err = params.get('error_description') || params.get('error');
                const setMsg = (t) => { document.getElementById('msg').innerText = t; };
                if (hash) {
                    const body = hash.substring(1).replace(/(^|&)state=[^&]*/i, '$1') + '&state=' + encodeURIComponent('{{expectedState}}');
                    fetch('/exchange', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body })
                        .then(r => setMsg(r.ok ? 'تم تسجيل الدخول بنجاح! يمكنك إغلاق هذه الصفحة والعودة إلى Ziwa Studio.' : 'فشلت المصادقة (' + r.status + ')'))
                        .catch(() => setMsg('تعذر الاتصال بالتطبيق المحلي. أعد المحاولة من التطبيق.'));
                } else if (err) {
                    setMsg('فشلت المصادقة: ' + decodeURIComponent(err.replace(/\+/g, ' ')));
                } else {
                    setMsg('الرابط غير صحيح أو منتهي الصلاحية.');
                }
            </script>
        </body>
        </html>
        """;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownCts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            await _acceptLoopTask;
        }
        catch
        {
        }

        _shutdownCts.Dispose();
    }

    private sealed record ParsedHttpRequest(
        string Method,
        string Path,
        Dictionary<string, string> Headers,
        string BodyText);
}
