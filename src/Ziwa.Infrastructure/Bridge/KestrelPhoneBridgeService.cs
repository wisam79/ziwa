using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Bridge;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.Assets;
using Ziwa.Infrastructure.Auth;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Bridge;

public sealed class KestrelPhoneBridgeService : IPhoneBridgeService, IAsyncDisposable, IDisposable
{
    private const string SignInRequiredMessageAr =
        "للمزامنة عبر الإنترنت سجّل الدخول أولاً — وضع الشبكة المحلية (LAN) جاهز للاستخدام.";
    private const string SessionInvalidMessageAr =
        "تعذّر التحقق من جلسة حسابك في Supabase — يمكنك المتابعة بوضع الشبكة المحلية (LAN) أو إعادة تسجيل الدخول.";

    private static readonly Lazy<byte[]> EmbeddedCameraPageHtml = new(LoadEmbeddedCameraPageHtml);

    private readonly AssetStore _assetStore;
    private readonly ICloudAuthSessionProvider _cloudSessionProvider;
    private readonly SupabaseAuthOptions _authOptions;
    private readonly PhoneBridgeOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _webSocketFactory;

    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedCloudObjects = new(StringComparer.Ordinal);

    private WebApplication? _webApp;
    private CancellationTokenSource? _cloudCts;
    private long _generation;
    private string _sessionToken = string.Empty;
    private string _ipAddress = "127.0.0.1";
    private int _port;
    private string _localUrl = string.Empty;
    private string _cloudUrl = string.Empty;
    private string _pairedDevice = string.Empty;
    private int _receivedCount;
    private string _lastReceivedAtText = string.Empty;
    private string _cloudUserId = string.Empty;
    private string _cloudAccessToken = string.Empty;
    private string _cloudErrorMessage = string.Empty;
    private bool _disposed;

    public KestrelPhoneBridgeService(
        AssetStore assetStore,
        ICloudAuthSessionProvider cloudSessionProvider,
        SupabaseAuthOptions authOptions,
        PhoneBridgeOptions? options = null)
        : this(assetStore, cloudSessionProvider, authOptions, options, httpMessageHandler: null, webSocketFactory: null)
    {
    }

    internal KestrelPhoneBridgeService(
        AssetStore assetStore,
        ICloudAuthSessionProvider cloudSessionProvider,
        SupabaseAuthOptions authOptions,
        PhoneBridgeOptions? options,
        HttpMessageHandler? httpMessageHandler,
        Func<Uri, CancellationToken, Task<WebSocket>>? webSocketFactory)
    {
        _assetStore = assetStore;
        _cloudSessionProvider = cloudSessionProvider;
        _authOptions = authOptions;
        _options = options ?? new PhoneBridgeOptions();
        HttpMessageHandler? effectiveHandler = httpMessageHandler ?? authOptions.CustomHttpHandler;
        _ownsHttpClient = effectiveHandler is null;
        _httpClient = effectiveHandler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(effectiveHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        _webSocketFactory = webSocketFactory ?? ConnectDefaultWebSocketAsync;

        _cloudSessionProvider.SessionChanged += OnCloudSessionChanged;
    }

    public event Action<PhoneBridgeStatus>? StatusChanged;
    public event Action<string>? DevicePaired;
    public event Action<PhoneBridgeReceivedPhoto>? PhotoReceived;
    public event Action<string>? CloudErrorOccurred;

    public PhoneBridgeStatus CurrentStatus
    {
        get
        {
            lock (_stateSync)
            {
                return BuildStatusLocked();
            }
        }
    }

    public async Task<PhoneBridgeStartInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloudAuthSessionSnapshot? cloudSession = ResolveValidCloudSession();

            bool alreadyRunning;
            lock (_stateSync)
            {
                alreadyRunning = _webApp is not null && !string.IsNullOrEmpty(_sessionToken);
            }

            if (alreadyRunning)
            {
                PhoneBridgeStartInfo existingInfo;
                string? cloudErrorToEmit = null;
                lock (_stateSync)
                {
                    ApplyCloudIdentityLocked(cloudSession);
                    EnsureCloudSyncStartedLocked(out cloudErrorToEmit);
                    existingInfo = new PhoneBridgeStartInfo(
                        _ipAddress,
                        _port,
                        _sessionToken,
                        _localUrl,
                        _cloudUrl,
                        IsRunning: true);
                }

                if (!string.IsNullOrWhiteSpace(cloudErrorToEmit))
                {
                    CloudErrorOccurred?.Invoke(cloudErrorToEmit);
                }

                RaiseStatusChanged();
                return existingInfo;
            }

            string bindIp = _options.BindLoopbackOnly ? "127.0.0.1" : DiscoverLocalIpv4();
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

            (WebApplication app, int boundPort) = await StartKestrelListenerAsync(bindIp, cancellationToken).ConfigureAwait(false);
            string localUrl = PhoneBridgeUrlPolicy.FormatLocalPairingUrl(bindIp, boundPort, token);

            string? startupCloudError;
            PhoneBridgeStartInfo startInfo;
            lock (_stateSync)
            {
                Interlocked.Increment(ref _generation);
                _processedCloudObjects.Clear();
                _webApp = app;
                _ipAddress = bindIp;
                _port = boundPort;
                _sessionToken = token;
                _localUrl = localUrl;
                _pairedDevice = string.Empty;
                _receivedCount = 0;
                _lastReceivedAtText = string.Empty;
                _cloudErrorMessage = string.Empty;

                ApplyCloudIdentityLocked(cloudSession);
                EnsureCloudSyncStartedLocked(out startupCloudError);

                startInfo = new PhoneBridgeStartInfo(
                    _ipAddress,
                    _port,
                    _sessionToken,
                    _localUrl,
                    _cloudUrl,
                    IsRunning: true);
            }

            if (!string.IsNullOrWhiteSpace(startupCloudError))
            {
                CloudErrorOccurred?.Invoke(startupCloudError);
            }

            RaiseStatusChanged();
            return startInfo;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreLockedAsync(CancellationToken cancellationToken)
    {
        WebApplication? appToStop;
        CancellationTokenSource? cloudCtsToCancel;
        string prefixToClean = string.Empty;
        string tokenToClean = string.Empty;

        lock (_stateSync)
        {
            Interlocked.Increment(ref _generation);
            cloudCtsToCancel = _cloudCts;
            _cloudCts = null;

            if (PhoneBridgeUrlPolicy.TryBuildCloudObjectPrefix(_cloudUserId, _sessionToken, out string prefix))
            {
                prefixToClean = prefix;
                tokenToClean = _cloudAccessToken;
            }

            appToStop = _webApp;
            _webApp = null;
            _sessionToken = string.Empty;
            _port = 0;
            _localUrl = string.Empty;
            _cloudUrl = string.Empty;
            _pairedDevice = string.Empty;
            _receivedCount = 0;
            _lastReceivedAtText = string.Empty;
            _cloudUserId = string.Empty;
            _cloudAccessToken = string.Empty;
            _cloudErrorMessage = string.Empty;
            _processedCloudObjects.Clear();
        }

        if (cloudCtsToCancel is not null)
        {
            try
            {
                await cloudCtsToCancel.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cloudCtsToCancel.Dispose();
            }
        }

        if (appToStop is not null)
        {
            using var stopTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopTimeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await appToStop.StopAsync(stopTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await appToStop.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrEmpty(prefixToClean) && !string.IsNullOrEmpty(tokenToClean))
        {
            await TryDeleteCloudPrefixAsync(prefixToClean, tokenToClean).ConfigureAwait(false);
        }

        RaiseStatusChanged();
    }

    private async Task<(WebApplication App, int Port)> StartKestrelListenerAsync(
        string bindIp,
        CancellationToken cancellationToken)
    {
        int preferredPort = _options.PreferredPort;
        var candidates = new List<(IPAddress Ip, int Port)>();

        if (IPAddress.TryParse(bindIp, out IPAddress? parsedIp))
        {
            candidates.Add((parsedIp, preferredPort));
            if (!_options.BindLoopbackOnly && !IPAddress.IsLoopback(parsedIp) && preferredPort > 0)
            {
                candidates.Add((IPAddress.Any, preferredPort));
            }

            if (preferredPort != 0)
            {
                candidates.Add((parsedIp, 0));
            }
        }
        else
        {
            candidates.Add((IPAddress.Loopback, preferredPort));
            if (preferredPort != 0)
            {
                candidates.Add((IPAddress.Loopback, 0));
            }
        }

        Exception? lastException = null;
        foreach ((IPAddress ip, int port) in candidates)
        {
            WebApplication? app = null;
            try
            {
                app = BuildKestrelWebApp(ip, port);
                await app.StartAsync(cancellationToken).ConfigureAwait(false);

                IServerAddressesFeature? addrFeature = app.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>();
                string? boundAddress = addrFeature?.Addresses.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(boundAddress) && Uri.TryCreate(boundAddress, UriKind.Absolute, out Uri? boundUri))
                {
                    return (app, boundUri.Port);
                }

                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                if (app is not null)
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException("تعذر بدء خادم جسر الهاتف المحلي على أي منفذ متاح.", lastException);
    }

    private WebApplication BuildKestrelWebApp(IPAddress bindIp, int port)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = _options.MaxUploadBytes;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            kestrel.Listen(bindIp, port);
        });
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = _options.MaxUploadBytes;
            form.MemoryBufferThreshold = 10 * 1024 * 1024;
        });

        WebApplication app = builder.Build();
        app.MapGet("/", HandleMobilePageAsync);
        app.MapGet("/cam", HandleMobilePageAsync);
        app.MapGet("/ping", HandlePingAsync);
        app.MapGet("/manifest.json", HandleManifestAsync);
        app.MapPost("/upload", HandleUploadAsync);
        app.MapPost("/api/upload", HandleUploadAsync);
        return app;
    }

    private static Task HandleMobilePageAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self';";
        return context.Response.Body.WriteAsync(EmbeddedCameraPageHtml.Value, context.RequestAborted).AsTask();
    }

    private static Task HandlePingAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new
        {
            status = "ok",
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, context.RequestAborted);
    }

    private static Task HandleManifestAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/manifest+json; charset=utf-8";
        context.Response.Headers.CacheControl = "public, max-age=86400";
        return context.Response.WriteAsJsonAsync(new
        {
            name = "Ziwa Studio Camera",
            short_name = "Ziwa Cam",
            start_url = "/cam",
            display = "standalone",
            orientation = "portrait",
            background_color = "#090b10",
            theme_color = "#090b10",
        }, context.RequestAborted);
    }

    private async Task HandleUploadAsync(HttpContext context)
    {
        long capturedGeneration = Interlocked.Read(ref _generation);
        if (!TokenMatches(context.Request))
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized: invalid session token").ConfigureAwait(false);
            return;
        }

        IHttpMaxRequestBodySizeFeature? bodyLimitFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimitFeature is { IsReadOnly: false })
        {
            bodyLimitFeature.MaxRequestBodySize = _options.MaxUploadBytes;
        }

        if (context.Request.ContentLength is long contentLength && contentLength > _options.MaxUploadBytes)
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status413PayloadTooLarge, "Payload too large").ConfigureAwait(false);
            return;
        }

        byte[] payloadBytes;
        string? fileName = null;

        try
        {
            if (context.Request.HasFormContentType)
            {
                IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                IFormFile? formFile = form.Files.GetFile("photo") ?? form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (formFile is null || formFile.Length <= 0)
                {
                    await WriteRejectResponseAsync(context, StatusCodes.Status400BadRequest, "Missing photo file in request").ConfigureAwait(false);
                    return;
                }

                if (formFile.Length > _options.MaxUploadBytes)
                {
                    await WriteRejectResponseAsync(context, StatusCodes.Status413PayloadTooLarge, "File exceeds max upload size").ConfigureAwait(false);
                    return;
                }

                fileName = Path.GetFileNameWithoutExtension(formFile.FileName);
                await using Stream fileStream = formFile.OpenReadStream();
                (bool withinLimit, byte[]? readBytes) = await ReadBoundedBytesAsync(
                    fileStream,
                    _options.MaxUploadBytes,
                    context.RequestAborted).ConfigureAwait(false);

                if (!withinLimit || readBytes is null)
                {
                    await WriteRejectResponseAsync(context, StatusCodes.Status413PayloadTooLarge, "File exceeds max upload size").ConfigureAwait(false);
                    return;
                }

                payloadBytes = readBytes;
            }
            else
            {
                (bool withinLimit, byte[]? readBytes) = await ReadBoundedBytesAsync(
                    context.Request.Body,
                    _options.MaxUploadBytes,
                    context.RequestAborted).ConfigureAwait(false);

                if (!withinLimit || readBytes is null)
                {
                    await WriteRejectResponseAsync(context, StatusCodes.Status413PayloadTooLarge, "Payload exceeds max upload size").ConfigureAwait(false);
                    return;
                }

                payloadBytes = readBytes;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status413PayloadTooLarge, "Payload exceeds max upload size").ConfigureAwait(false);
            return;
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status413PayloadTooLarge, "Payload exceeds max upload size").ConfigureAwait(false);
            return;
        }
        catch (BadHttpRequestException)
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status400BadRequest, "Malformed upload payload").ConfigureAwait(false);
            return;
        }
        catch (InvalidDataException)
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status400BadRequest, "Malformed multipart payload").ConfigureAwait(false);
            return;
        }

        if (payloadBytes.Length == 0)
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status400BadRequest, "Empty upload payload").ConfigureAwait(false);
            return;
        }

        if (!IsSupportedImageMagicBytes(payloadBytes))
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status415UnsupportedMediaType, "Invalid content type: only images are accepted").ConfigureAwait(false);
            return;
        }

        if (!TryDecodeImageBounds(payloadBytes, out int widthPx, out int heightPx))
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status400BadRequest, "Corrupt or unreadable image payload").ConfigureAwait(false);
            return;
        }

        if (!GenerationMatches(capturedGeneration))
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status503ServiceUnavailable, "Session ended during upload").ConfigureAwait(false);
            return;
        }

        AssetId assetId = _assetStore.RegisterAsset(payloadBytes);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        bool committed = TryRecordPhotoArrival(
            capturedGeneration,
            new PhoneBridgeReceivedPhoto(assetId, widthPx, heightPx, PhoneBridgePhotoSource.LocalLan, now, fileName),
            now,
            out PhoneBridgeStatus? updatedStatus);

        if (!committed)
        {
            await WriteRejectResponseAsync(context, StatusCodes.Status503ServiceUnavailable, "Session ended during upload").ConfigureAwait(false);
            return;
        }

        if (updatedStatus is not null)
        {
            StatusChanged?.Invoke(updatedStatus);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new
        {
            status = "success",
            assetId = assetId.Value,
            width = widthPx,
            height = heightPx,
        }, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteRejectResponseAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.Connection = "close";
        await context.Response.WriteAsync(message, context.RequestAborted).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private bool TokenMatches(HttpRequest request)
    {
        string? candidate = request.Headers["X-Bridge-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = request.Query["token"].FirstOrDefault();
        }

        string activeToken;
        lock (_stateSync)
        {
            activeToken = _sessionToken;
        }

        return PhoneBridgeTokenValidator.FixedTimeEquals(candidate, activeToken);
    }

    private bool GenerationMatches(long capturedGeneration)
    {
        lock (_stateSync)
        {
            return _webApp is not null && Interlocked.Read(ref _generation) == capturedGeneration;
        }
    }

    private bool TryRecordPhotoArrival(
        long capturedGeneration,
        PhoneBridgeReceivedPhoto photo,
        DateTimeOffset receivedAtUtc,
        out PhoneBridgeStatus? updatedStatus)
    {
        lock (_stateSync)
        {
            if (_webApp is null || Interlocked.Read(ref _generation) != capturedGeneration)
            {
                updatedStatus = null;
                return false;
            }

            _receivedCount++;
            _lastReceivedAtText = receivedAtUtc.ToLocalTime().ToString("HH:mm:ss");
            updatedStatus = BuildStatusLocked();
        }

        PhotoReceived?.Invoke(photo);
        return true;
    }

    private CloudAuthSessionSnapshot? ResolveValidCloudSession()
    {
        CloudAuthSessionSnapshot? snapshot = _cloudSessionProvider.GetCurrentSession();
        if (snapshot is null ||
            string.IsNullOrWhiteSpace(snapshot.AccessToken) ||
            string.IsNullOrWhiteSpace(snapshot.UserId) ||
            !Guid.TryParse(snapshot.UserId, out _))
        {
            return null;
        }

        return snapshot;
    }

    private void ApplyCloudIdentityLocked(CloudAuthSessionSnapshot? session)
    {
        if (!_options.EnableCloudSync)
        {
            _cloudUserId = string.Empty;
            _cloudAccessToken = string.Empty;
            _cloudUrl = string.Empty;
            _cloudErrorMessage = string.Empty;
            return;
        }

        Uri? supabaseBaseUri = _authOptions.ResolveValidatedBaseUri();
        if (supabaseBaseUri is null || string.IsNullOrWhiteSpace(_authOptions.SupabasePublishableKey))
        {
            _cloudUserId = string.Empty;
            _cloudAccessToken = string.Empty;
            _cloudUrl = string.Empty;
            _cloudErrorMessage = SignInRequiredMessageAr;
            return;
        }

        if (session is null || string.IsNullOrWhiteSpace(session.UserId) || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            _cloudUserId = string.Empty;
            _cloudAccessToken = string.Empty;
            _cloudUrl = string.Empty;
            _cloudErrorMessage = SignInRequiredMessageAr;
            return;
        }

        _cloudUserId = session.UserId.Trim();
        _cloudAccessToken = session.AccessToken.Trim();
        _cloudUrl = PhoneBridgeUrlPolicy.FormatCloudPairingUrl(_options.CloudCameraBaseUrl, _cloudUserId, _sessionToken);
        _cloudErrorMessage = string.Empty;
    }

    private void EnsureCloudSyncStartedLocked(out string? errorToEmit)
    {
        errorToEmit = null;
        if (!_options.EnableCloudSync || _cloudCts is not null)
        {
            return;
        }

        if (!PhoneBridgeUrlPolicy.TryBuildCloudObjectPrefix(_cloudUserId, _sessionToken, out string prefix) ||
            string.IsNullOrWhiteSpace(_cloudAccessToken))
        {
            if (!string.IsNullOrWhiteSpace(_cloudErrorMessage))
            {
                errorToEmit = _cloudErrorMessage;
            }

            return;
        }

        Uri? supabaseUri = _authOptions.ResolveValidatedBaseUri();
        string publishableKey = _authOptions.SupabasePublishableKey ?? string.Empty;
        if (supabaseUri is null || string.IsNullOrWhiteSpace(publishableKey))
        {
            return;
        }

        _cloudCts = new CancellationTokenSource();
        CancellationToken ct = _cloudCts.Token;
        long capturedGeneration = Interlocked.Read(ref _generation);
        string sessionToken = _sessionToken;
        string accessToken = _cloudAccessToken;

        _ = Task.Run(() => RunRealtimeSyncLoopAsync(
            supabaseUri,
            publishableKey,
            sessionToken,
            prefix,
            accessToken,
            capturedGeneration,
            ct), CancellationToken.None);

        _ = Task.Run(() => RunCloudFallbackPollLoopAsync(
            supabaseUri,
            publishableKey,
            sessionToken,
            prefix,
            accessToken,
            capturedGeneration,
            ct), CancellationToken.None);
    }

    private void DisableCloudSync(long expectedGeneration, string message)
    {
        CancellationTokenSource? ctsToCancel = null;
        bool shouldEmit = false;

        lock (_stateSync)
        {
            if (Interlocked.Read(ref _generation) != expectedGeneration)
            {
                return;
            }

            ctsToCancel = _cloudCts;
            _cloudCts = null;
            _cloudAccessToken = string.Empty;
            _cloudUrl = string.Empty;
            if (!string.Equals(_cloudErrorMessage, message, StringComparison.Ordinal))
            {
                _cloudErrorMessage = message;
                shouldEmit = true;
            }
        }

        if (ctsToCancel is not null)
        {
            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                ctsToCancel.Dispose();
            }
        }

        if (shouldEmit && !string.IsNullOrWhiteSpace(message))
        {
            CloudErrorOccurred?.Invoke(message);
        }

        RaiseStatusChanged();
    }

    private async Task RunRealtimeSyncLoopAsync(
        Uri supabaseUri,
        string publishableKey,
        string sessionToken,
        string objectPrefix,
        string userJwt,
        long capturedGeneration,
        CancellationToken ct)
    {
        string wsScheme = string.Equals(supabaseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var wsUriBuilder = new UriBuilder(supabaseUri)
        {
            Scheme = wsScheme,
            Port = supabaseUri.IsDefaultPort ? -1 : supabaseUri.Port,
            Path = "/realtime/v1/websocket",
            Query = $"apikey={Uri.EscapeDataString(publishableKey)}&vsn=1.0.0",
        };
        Uri wsUri = wsUriBuilder.Uri;
        string topic = $"realtime:phone-bridge:{sessionToken}";
        TimeSpan backoff = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested && GenerationMatches(capturedGeneration))
        {
            WebSocket? socket = null;
            using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dialCts.CancelAfter(TimeSpan.FromSeconds(10));
                socket = await _webSocketFactory(wsUri, dialCts.Token).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);

                string joinJson = JsonSerializer.Serialize(new
                {
                    topic,
                    @event = "phx_join",
                    payload = new
                    {
                        config = new
                        {
                            broadcast = new { self = false, ack = false },
                        },
                    },
                    @ref = "1",
                    join_ref = "1",
                });
                await SendWebSocketTextAsync(socket, joinJson, ct).ConfigureAwait(false);

                Task heartbeatTask = RunRealtimeHeartbeatAsync(socket, _options.RealtimeHeartbeatInterval, hbCts.Token);
                try
                {
                    await ReadRealtimeFramesAsync(
                        socket,
                        topic,
                        objectPrefix,
                        userJwt,
                        capturedGeneration,
                        ct).ConfigureAwait(false);
                }
                finally
                {
                    await hbCts.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await heartbeatTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (backoff < TimeSpan.FromSeconds(10))
                {
                    backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(10).Ticks, backoff.Ticks * 2));
                }
            }
            finally
            {
                if (socket is not null)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        try
                        {
                            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", closeCts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    socket.Dispose();
                }
            }
        }
    }

    private async Task ReadRealtimeFramesAsync(
        WebSocket socket,
        string topic,
        string objectPrefix,
        string userJwt,
        long capturedGeneration,
        CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var frameBuffer = new MemoryStream();

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open && GenerationMatches(capturedGeneration))
        {
            frameBuffer.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (frameBuffer.Length + result.Count > 256 * 1024)
                {
                    return;
                }

                frameBuffer.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text || frameBuffer.Length == 0)
            {
                continue;
            }

            HandleRealtimeEnvelope(
                socket,
                topic,
                frameBuffer.GetBuffer().AsSpan(0, (int)frameBuffer.Length),
                objectPrefix,
                userJwt,
                capturedGeneration,
                ct);
        }
    }

    private void HandleRealtimeEnvelope(
        WebSocket socket,
        string topic,
        ReadOnlySpan<byte> jsonUtf8,
        string objectPrefix,
        string userJwt,
        long capturedGeneration,
        CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonUtf8.ToArray());
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("event", out JsonElement outerEvent) ||
                !string.Equals(outerEvent.GetString(), "broadcast", StringComparison.Ordinal))
            {
                return;
            }

            if (!root.TryGetProperty("payload", out JsonElement outerPayload) ||
                outerPayload.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!outerPayload.TryGetProperty("event", out JsonElement innerEventEl) ||
                !outerPayload.TryGetProperty("payload", out JsonElement innerPayloadEl) ||
                innerPayloadEl.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string? broadcastEvent = innerEventEl.GetString();
            if (string.Equals(broadcastEvent, "paired", StringComparison.Ordinal))
            {
                string? device = innerPayloadEl.TryGetProperty("device", out JsonElement devEl)
                    ? devEl.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(device))
                {
                    RecordDevicePaired(capturedGeneration, device.Trim());
                    _ = TrySendPairedAckAsync(socket, topic, ct);
                }
            }
            else if (string.Equals(broadcastEvent, "photo-ready", StringComparison.Ordinal))
            {
                string? path = innerPayloadEl.TryGetProperty("path", out JsonElement pathEl)
                    ? pathEl.GetString()
                    : null;
                if (PhoneBridgeUrlPolicy.IsSafeCloudObjectPath(path, objectPrefix))
                {
                    _ = Task.Run(() => DownloadAndCommitCloudPhotoAsync(
                        path!,
                        userJwt,
                        capturedGeneration,
                        ct), CancellationToken.None);
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static async Task TrySendPairedAckAsync(WebSocket socket, string topic, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            string ackJson = JsonSerializer.Serialize(new
            {
                topic,
                @event = "broadcast",
                payload = new
                {
                    type = "broadcast",
                    @event = "paired-ack",
                    payload = new { device = "Ziwa Studio" },
                },
                @ref = "ack",
            });
            await SendWebSocketTextAsync(socket, ackJson, ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task RunRealtimeHeartbeatAsync(WebSocket socket, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            string hbJson = JsonSerializer.Serialize(new
            {
                topic = "phoenix",
                @event = "heartbeat",
                payload = new { },
                @ref = "hb",
            });
            await SendWebSocketTextAsync(socket, hbJson, ct).ConfigureAwait(false);
        }
    }

    private async Task RunCloudFallbackPollLoopAsync(
        Uri supabaseUri,
        string publishableKey,
        string sessionToken,
        string objectPrefix,
        string userJwt,
        long capturedGeneration,
        CancellationToken ct)
    {
        Uri listUri = new(supabaseUri, "/storage/v1/object/list/phone-bridge");
        TimeSpan normalInterval = _options.CloudPollInterval > TimeSpan.Zero
            ? _options.CloudPollInterval
            : TimeSpan.FromSeconds(4);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested && GenerationMatches(capturedGeneration))
        {
            TimeSpan nextDelay = normalInterval;
            try
            {
                PruneProcessedCloudCache();

                using var req = new HttpRequestMessage(HttpMethod.Post, listUri);
                req.Headers.TryAddWithoutValidation("apikey", publishableKey);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { prefix = objectPrefix + "/", limit = 10 }),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    nextDelay = TimeSpan.FromSeconds(15);
                }
                else if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    DisableCloudSync(capturedGeneration, SessionInvalidMessageAr);
                    return;
                }
                else if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in doc.RootElement.EnumerateArray())
                        {
                            if (ct.IsCancellationRequested || !GenerationMatches(capturedGeneration))
                            {
                                break;
                            }

                            string? name = item.TryGetProperty("name", out JsonElement nameEl)
                                ? nameEl.GetString()
                                : null;
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                continue;
                            }

                            string objectPath = $"{objectPrefix}/{name}";
                            if (!PhoneBridgeUrlPolicy.IsSafeCloudObjectPath(objectPath, objectPrefix))
                            {
                                continue;
                            }

                            if (name.EndsWith(".handshake.json", StringComparison.OrdinalIgnoreCase) ||
                                name.EndsWith(".handshake", StringComparison.OrdinalIgnoreCase))
                            {
                                if (_processedCloudObjects.TryAdd(objectPath, DateTimeOffset.UtcNow))
                                {
                                    await HandleCloudHandshakeObjectAsync(
                                        supabaseUri,
                                        publishableKey,
                                        objectPath,
                                        userJwt,
                                        capturedGeneration,
                                        ct).ConfigureAwait(false);
                                }

                                continue;
                            }

                            if (name.StartsWith('.'))
                            {
                                await TryDeleteCloudObjectsAsync(supabaseUri, publishableKey, [objectPath], userJwt).ConfigureAwait(false);
                                continue;
                            }

                            await DownloadAndCommitCloudPhotoAsync(objectPath, userJwt, capturedGeneration, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(nextDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleCloudHandshakeObjectAsync(
        Uri supabaseUri,
        string publishableKey,
        string objectPath,
        string userJwt,
        long capturedGeneration,
        CancellationToken ct)
    {
        try
        {
            Uri downUri = new(supabaseUri, $"/storage/v1/object/phone-bridge/{objectPath}");
            using var req = new HttpRequestMessage(HttpMethod.Get, downUri);
            req.Headers.TryAddWithoutValidation("apikey", publishableKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);

            using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("device", out JsonElement devEl))
                {
                    string? device = devEl.GetString();
                    if (!string.IsNullOrWhiteSpace(device))
                    {
                        RecordDevicePaired(capturedGeneration, device.Trim());
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            await TryDeleteCloudObjectsAsync(supabaseUri, publishableKey, [objectPath], userJwt).ConfigureAwait(false);
        }
    }

    private async Task DownloadAndCommitCloudPhotoAsync(
        string objectPath,
        string userJwt,
        long capturedGeneration,
        CancellationToken ct)
    {
        Uri? supabaseUri = _authOptions.ResolveValidatedBaseUri();
        string publishableKey = _authOptions.SupabasePublishableKey ?? string.Empty;
        if (supabaseUri is null || string.IsNullOrWhiteSpace(publishableKey))
        {
            return;
        }

        if (!_processedCloudObjects.TryAdd(objectPath, DateTimeOffset.UtcNow))
        {
            return;
        }

        try
        {
            Uri downUri = new(supabaseUri, $"/storage/v1/object/phone-bridge/{objectPath}");
            using var req = new HttpRequestMessage(HttpMethod.Get, downUri);
            req.Headers.TryAddWithoutValidation("apikey", publishableKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);

            using HttpResponseMessage resp = await _httpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _processedCloudObjects.TryRemove(objectPath, out _);
                return;
            }

            if (resp.Content.Headers.ContentLength is long cl && cl > _options.MaxUploadBytes)
            {
                await TryDeleteCloudObjectsAsync(supabaseUri, publishableKey, [objectPath], userJwt).ConfigureAwait(false);
                return;
            }

            await using Stream bodyStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            (bool withinLimit, byte[]? bytes) = await ReadBoundedBytesAsync(bodyStream, _options.MaxUploadBytes, ct).ConfigureAwait(false);

            if (!withinLimit)
            {
                await TryDeleteCloudObjectsAsync(supabaseUri, publishableKey, [objectPath], userJwt).ConfigureAwait(false);
                return;
            }

            // RV-Bridge-01: empty or truncated body is treated as transient — do not delete cloud original
            if (bytes is null || bytes.Length == 0)
            {
                _processedCloudObjects.TryRemove(objectPath, out _);
                return;
            }

            if (resp.Content.Headers.ContentLength is long expectedLength && bytes.Length < expectedLength)
            {
                _processedCloudObjects.TryRemove(objectPath, out _);
                return;
            }

            if (!IsSupportedImageMagicBytes(bytes) || !TryDecodeImageBounds(bytes, out int widthPx, out int heightPx))
            {
                await TryDeleteCloudObjectsAsync(supabaseUri, publishableKey, [objectPath], userJwt).ConfigureAwait(false);
                return;
            }

            if (!GenerationMatches(capturedGeneration))
            {
                return;
            }

            AssetId assetId = _assetStore.RegisterAsset(bytes);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string? displayName = Path.GetFileNameWithoutExtension(objectPath);

            bool committed = TryRecordPhotoArrival(
                capturedGeneration,
                new PhoneBridgeReceivedPhoto(assetId, widthPx, heightPx, PhoneBridgePhotoSource.CloudRelay, now, displayName),
                now,
                out PhoneBridgeStatus? updatedStatus);

            if (committed && updatedStatus is not null)
            {
                StatusChanged?.Invoke(updatedStatus);
            }

            await TryDeleteCloudObjectsAsync(supabaseUri, publishableKey, [objectPath], userJwt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _processedCloudObjects.TryRemove(objectPath, out _);
        }
        catch
        {
            _processedCloudObjects.TryRemove(objectPath, out _);
        }
    }

    private async Task TryDeleteCloudPrefixAsync(string objectPrefix, string userJwt)
    {
        Uri? supabaseUri = _authOptions.ResolveValidatedBaseUri();
        string publishableKey = _authOptions.SupabasePublishableKey ?? string.Empty;
        if (supabaseUri is null || string.IsNullOrWhiteSpace(publishableKey))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Uri listUri = new(supabaseUri, "/storage/v1/object/list/phone-bridge");
            using var listReq = new HttpRequestMessage(HttpMethod.Post, listUri);
            listReq.Headers.TryAddWithoutValidation("apikey", publishableKey);
            listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);
            listReq.Content = new StringContent(
                JsonSerializer.Serialize(new { prefix = objectPrefix + "/", limit = 100 }),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage listResp = await _httpClient.SendAsync(listReq, cts.Token).ConfigureAwait(false);
            if (!listResp.IsSuccessStatusCode)
            {
                return;
            }

            string json = await listResp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var prefixes = new List<string>();
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string? name = item.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string path = $"{objectPrefix}/{name}";
                    if (PhoneBridgeUrlPolicy.IsSafeCloudObjectPath(path, objectPrefix))
                    {
                        prefixes.Add(path);
                    }
                }
            }

            if (prefixes.Count > 0)
            {
                await TryDeleteCloudObjectsAsync(supabaseUri, publishableKey, prefixes, userJwt).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private async Task TryDeleteCloudObjectsAsync(
        Uri supabaseUri,
        string publishableKey,
        IReadOnlyList<string> objectPaths,
        string userJwt)
    {
        if (objectPaths.Count == 0 || string.IsNullOrWhiteSpace(userJwt))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Uri deleteUri = new(supabaseUri, "/storage/v1/object/phone-bridge");
            using var req = new HttpRequestMessage(HttpMethod.Delete, deleteUri);
            req.Headers.TryAddWithoutValidation("apikey", publishableKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { prefixes = objectPaths }),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage _ = await _httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void RecordDevicePaired(long capturedGeneration, string deviceName)
    {
        PhoneBridgeStatus? updatedStatus;
        lock (_stateSync)
        {
            if (_webApp is null || Interlocked.Read(ref _generation) != capturedGeneration)
            {
                return;
            }

            _pairedDevice = deviceName;
            updatedStatus = BuildStatusLocked();
        }

        DevicePaired?.Invoke(deviceName);
        StatusChanged?.Invoke(updatedStatus);
    }

    private void PruneProcessedCloudCache()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        foreach (KeyValuePair<string, DateTimeOffset> entry in _processedCloudObjects)
        {
            if (entry.Value < cutoff)
            {
                _processedCloudObjects.TryRemove(entry.Key, out _);
            }
        }
    }

    private void OnCloudSessionChanged()
    {
        CloudAuthSessionSnapshot? snapshot = ResolveValidCloudSession();
        long capturedGeneration;
        lock (_stateSync)
        {
            if (_webApp is null)
            {
                return;
            }

            capturedGeneration = Interlocked.Read(ref _generation);
        }

        if (snapshot is null)
        {
            DisableCloudSync(capturedGeneration, SignInRequiredMessageAr);
        }
    }

    private PhoneBridgeStatus BuildStatusLocked()
    {
        bool running = _webApp is not null;
        return running
            ? new PhoneBridgeStatus(
                IsRunning: true,
                LocalUrl: _localUrl,
                CloudUrl: _cloudUrl,
                PairedDevice: _pairedDevice,
                ReceivedCount: _receivedCount,
                LastReceivedAtText: _lastReceivedAtText,
                CloudErrorMessage: _cloudErrorMessage)
            : PhoneBridgeStatus.Stopped;
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(CurrentStatus);

    private static async Task<(bool WithinLimit, byte[]? Bytes)> ReadBoundedBytesAsync(
        Stream stream,
        long maxBytes,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return (false, null);
            }

            ms.Write(buffer, 0, read);
        }

        return (true, ms.ToArray());
    }

    private static bool IsSupportedImageMagicBytes(ReadOnlySpan<byte> header)
    {
        if (FileImageAssetImporter.HasSupportedRasterMagicBytes(header))
        {
            return true;
        }

        // GIF87a / GIF89a
        return header.Length >= 6 &&
               header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' &&
               header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9') &&
               header[5] == (byte)'a';
    }

    private static bool TryDecodeImageBounds(byte[] bytes, out int widthPx, out int heightPx)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is not null && codec.Info.Width > 0 && codec.Info.Height > 0)
            {
                widthPx = codec.Info.Width;
                heightPx = codec.Info.Height;
                return true;
            }
        }
        catch
        {
        }

        widthPx = 0;
        heightPx = 0;
        return false;
    }

    internal static string DiscoverLocalIpv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint { Address: { } routedIp } &&
                routedIp.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(routedIp))
            {
                return routedIp.ToString();
            }
        }
        catch
        {
        }

        string? preferredIp = null;
        string? fallbackIp = null;
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                string lower = nic.Name.ToLowerInvariant();
                bool isVirtual =
                    lower.Contains("vethernet", StringComparison.Ordinal) ||
                    lower.Contains("wsl", StringComparison.Ordinal) ||
                    lower.Contains("docker", StringComparison.Ordinal) ||
                    lower.Contains("virtual", StringComparison.Ordinal) ||
                    lower.Contains("vbox", StringComparison.Ordinal) ||
                    lower.Contains("vmnet", StringComparison.Ordinal) ||
                    lower.Contains("tailscale", StringComparison.Ordinal) ||
                    lower.Contains("zerotier", StringComparison.Ordinal) ||
                    lower.Contains("tap", StringComparison.Ordinal) ||
                    lower.Contains("tun", StringComparison.Ordinal);

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    IPAddress ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                    {
                        continue;
                    }

                    byte[] octets = ip.GetAddressBytes();
                    bool isLanRange = (octets[0] == 192 && octets[1] == 168) || octets[0] == 10;
                    string ipStr = ip.ToString();
                    if (!isVirtual && isLanRange)
                    {
                        return ipStr;
                    }

                    if (!isVirtual && preferredIp is null)
                    {
                        preferredIp = ipStr;
                    }
                    else if (fallbackIp is null)
                    {
                        fallbackIp = ipStr;
                    }
                }
            }
        }
        catch
        {
        }

        return preferredIp ?? fallbackIp ?? "127.0.0.1";
    }

    private static async Task<WebSocket> ConnectDefaultWebSocketAsync(Uri uri, CancellationToken ct)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(uri, ct).ConfigureAwait(false);
        return client;
    }

    private static ValueTask SendWebSocketTextAsync(WebSocket socket, string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static byte[] LoadEmbeddedCameraPageHtml()
    {
        Assembly asm = typeof(KestrelPhoneBridgeService).Assembly;
        const string resourceName = "Ziwa.Infrastructure.Bridge.Embedded.PhoneCameraPage.html";
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resourceName}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cloudSessionProvider.SessionChanged -= OnCloudSessionChanged;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _lifecycleGate.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
