using System.Net;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.Ai;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase4ModalAiUpscalerTests
{
    [Fact]
    public async Task EnhanceImageAsync_SendsJwtAndUuidV4Idempotency_PrechecksSupabaseAndRegisters2xAsset()
    {
        using var store = new AssetStore();
        AssetId sourceId = store.RegisterAsset(CreateSolidPngBytes(120, 160, new SKColor(180, 130, 100, 255)));

        var requests = new List<RecordedHttpRequest>();
        byte[] upscaledJpeg = CreateSolidJpegBytes(240, 320, new SKColor(184, 134, 104, 255));
        string responseImageUrl = "data:image/jpeg;base64," + Convert.ToBase64String(upscaledJpeg);

        using var handler = new DelegateHttpMessageHandler(async (req, bodyText, ct) =>
        {
            await Task.Yield();
            requests.Add(new RecordedHttpRequest(
                req.Method,
                req.RequestUri!,
                req.Headers.Authorization?.ToString(),
                req.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? idem) ? idem.FirstOrDefault() : null,
                req.Headers.TryGetValues("X-Request-Id", out IEnumerable<string>? xreq) ? xreq.FirstOrDefault() : null,
                bodyText));

            if (req.RequestUri!.AbsolutePath.EndsWith("/rest/v1/rpc/check_and_record_ai_usage", StringComparison.Ordinal))
            {
                return CreateJsonResponse(HttpStatusCode.OK, """{"success":true,"used_today":2,"daily_limit":15,"check_only":true}""");
            }

            string modalJson = JsonSerializer.Serialize(new
            {
                success = true,
                usage_recorded = true,
                execution_seconds = 3.4,
                output_scale = 2,
                image = responseImageUrl,
            });
            return CreateJsonResponse(HttpStatusCode.OK, modalJson);
        });

        // Deliberate insecure http:// URL must fall back to validated default https:// Modal URL (SEC-10)
        var options = new CloudAiOptions
        {
            ModalEndpointUrl = "http://insecure.attacker.invalid/enhance",
            SupabaseUrl = CloudAiOptions.DefaultSupabaseUrl,
            SupabasePublishableKey = "sb_publishable_test_key",
            CustomHttpHandler = handler,
        };

        using var upscaler = new ModalAiUpscaler(store, options);
        var session = new CloudAuthSessionSnapshot(
            AccessToken: "user-jwt-token-secret",
            UserId: "11111111-2222-4333-8444-555555555555",
            Email: "studio@ziwa.app",
            Plan: "pro");

        CloudUpscaleResult result = await upscaler.EnhanceImageAsync(sourceId, session, fidelity: 0.72);

        Assert.Equal(2, requests.Count);
        RecordedHttpRequest rpcCall = requests[0];
        RecordedHttpRequest modalCall = requests[1];

        Assert.Equal(Uri.UriSchemeHttps, rpcCall.Uri.Scheme);
        Assert.Equal("Bearer user-jwt-token-secret", rpcCall.Authorization);

        Assert.Equal(new Uri(CloudAiOptions.DefaultModalEndpointUrl), modalCall.Uri);
        Assert.Equal(Uri.UriSchemeHttps, modalCall.Uri.Scheme);
        Assert.Equal("Bearer user-jwt-token-secret", modalCall.Authorization);
        Assert.False(string.IsNullOrWhiteSpace(modalCall.IdempotencyKey));
        Assert.Equal(modalCall.IdempotencyKey, modalCall.XRequestId);
        Assert.True(Guid.TryParse(modalCall.IdempotencyKey, out Guid parsedGuid));
        Assert.Equal(4, parsedGuid.Version);

        using JsonDocument reqJson = JsonDocument.Parse(modalCall.Body);
        Assert.Equal(modalCall.IdempotencyKey, reqJson.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(15, reqJson.RootElement.GetProperty("dailyLimit").GetInt32());

        Assert.NotEqual(sourceId, result.UpscaledAssetId);
        Assert.Equal(120, result.SourceWidthPx);
        Assert.Equal(160, result.SourceHeightPx);
        Assert.Equal(240, result.OutputWidthPx);
        Assert.Equal(320, result.OutputHeightPx);
        Assert.NotNull(result.ServerQuotaSnapshot);
        Assert.Equal(3, result.ServerQuotaSnapshot!.UsedToday);
        Assert.Equal(15, result.ServerQuotaSnapshot.DailyLimit);
    }

    [Fact]
    public async Task EnhanceImageAsync_PreservesTransparencyWhenServerReturnsOpaqueJpeg()
    {
        using var store = new AssetStore();
        byte[] cutoutPng = CreateTransparentCenterPngBytes(100, 100);
        AssetId sourceId = store.RegisterAsset(cutoutPng);

        byte[] opaque2xJpeg = CreateSolidJpegBytes(200, 200, new SKColor(220, 190, 170, 255));
        string responseImageUrl = "data:image/jpeg;base64," + Convert.ToBase64String(opaque2xJpeg);

        using var handler = new DelegateHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateJsonResponse(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new { success = true, usage_recorded = true, image = responseImageUrl }))));

        using var upscaler = new ModalAiUpscaler(store, new CloudAiOptions { CustomHttpHandler = handler });
        var session = new CloudAuthSessionSnapshot("jwt-alpha-test", Plan: "free");

        CloudUpscaleResult result = await upscaler.EnhanceImageAsync(sourceId, session);
        Assert.True(store.TryGetEncodedBytes(result.UpscaledAssetId, out byte[]? finalBytes));
        using SKBitmap finalBmp = SKBitmap.Decode(finalBytes);

        Assert.Equal(200, finalBmp.Width);
        Assert.Equal(200, finalBmp.Height);
        // Corner remains transparent while center stays opaque
        Assert.True(finalBmp.GetPixel(5, 5).Alpha < 10);
        Assert.True(finalBmp.GetPixel(100, 100).Alpha > 240);
    }

    [Fact]
    public async Task EnhanceImageAsync_RejectsOversizedImageLocallyWithoutHttpCalls()
    {
        using var store = new AssetStore();
        AssetId bigId = store.RegisterAsset(CreateSolidPngBytes(2100, 1000, new SKColor(80, 90, 100, 255)));

        int callCount = 0;
        using var handler = new DelegateHttpMessageHandler((_, _, _) =>
        {
            callCount++;
            return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, "{}"));
        });

        using var upscaler = new ModalAiUpscaler(store, new CloudAiOptions { CustomHttpHandler = handler });
        var session = new CloudAuthSessionSnapshot("jwt-secret-never-sent", Plan: "pro");

        CloudAiException ex = await Assert.ThrowsAsync<CloudAiException>(() =>
            upscaler.EnhanceImageAsync(bigId, session));

        Assert.Equal(CloudAiErrorCode.DimensionsTooLarge, ex.ErrorCode);
        Assert.Equal(0, callCount);
        Assert.DoesNotContain("jwt-secret-never-sent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnhanceImageAsync_CancellationAbortsPromptly_AndTimeoutAnd409HaveZeroAutoRetries()
    {
        using var store = new AssetStore();
        AssetId sourceId = store.RegisterAsset(CreateSolidPngBytes(64, 64, new SKColor(120, 130, 140, 255)));
        var session = new CloudAuthSessionSnapshot("jwt-timeout-check", Plan: "free");

        // 1. User cancellation aborts promptly
        int cancelCalls = 0;
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancelHandler = new DelegateHttpMessageHandler(async (_, _, ct) =>
        {
            cancelCalls++;
            requestStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return CreateJsonResponse(HttpStatusCode.OK, "{}");
        }))
        {
            using var upscaler = new ModalAiUpscaler(store, new CloudAiOptions { CustomHttpHandler = cancelHandler });
            using var cts = new CancellationTokenSource();
            var pending = upscaler.EnhanceImageAsync(sourceId, session, cancellationToken: cts.Token);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, cancelCalls);
        }

        // 2. Client timeout triggers CloudAiErrorCode.Timeout with exactly 1 call (zero auto-retry)
        int timeoutCalls = 0;
        using (var slowHandler = new DelegateHttpMessageHandler(async (_, _, ct) =>
        {
            timeoutCalls++;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return CreateJsonResponse(HttpStatusCode.OK, "{}");
        }))
        {
            var timeoutOpts = new CloudAiOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(90),
                CustomHttpHandler = slowHandler,
            };
            using var upscaler = new ModalAiUpscaler(store, timeoutOpts);
            CloudAiException ex = await Assert.ThrowsAsync<CloudAiException>(() =>
                upscaler.EnhanceImageAsync(sourceId, session));
            Assert.Equal(CloudAiErrorCode.Timeout, ex.ErrorCode);
            Assert.Equal(1, timeoutCalls);
        }

        // 3. 409 Conflict (duplicate idempotent request) maps cleanly with zero auto-retry
        int conflictCalls = 0;
        using (var conflictHandler = new DelegateHttpMessageHandler((_, _, _) =>
        {
            conflictCalls++;
            return Task.FromResult(CreateJsonResponse(
                HttpStatusCode.Conflict,
                """{"error":"طلب مكرر: تمت معالجة هذا الطلب مسبقاً","code":"duplicate_request"}"""));
        }))
        {
            using var upscaler = new ModalAiUpscaler(store, new CloudAiOptions { CustomHttpHandler = conflictHandler });
            CloudAiException ex = await Assert.ThrowsAsync<CloudAiException>(() =>
                upscaler.EnhanceImageAsync(sourceId, session));
            Assert.Equal(CloudAiErrorCode.DuplicateRequestConflict, ex.ErrorCode);
            Assert.Equal(1, conflictCalls);
        }

        // 4. Server returning usage_recorded=false is rejected safely
        using (var unrecordedHandler = new DelegateHttpMessageHandler((_, _, _) =>
        {
            string imgUrl = "data:image/jpeg;base64," + Convert.ToBase64String(CreateSolidJpegBytes(128, 128, SKColors.Gray));
            return Task.FromResult(CreateJsonResponse(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new { success = true, usage_recorded = false, image = imgUrl })));
        }))
        {
            using var upscaler = new ModalAiUpscaler(store, new CloudAiOptions { CustomHttpHandler = unrecordedHandler });
            CloudAiException ex = await Assert.ThrowsAsync<CloudAiException>(() =>
                upscaler.EnhanceImageAsync(sourceId, session));
            Assert.Equal(CloudAiErrorCode.UnrecordedUsage, ex.ErrorCode);
        }
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static byte[] CreateSolidPngBytes(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(color);
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateTransparentCenterPngBytes(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(210, 175, 150, 255) };
        canvas.DrawCircle(w * 0.5f, h * 0.5f, w * 0.25f, fill);
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateSolidJpegBytes(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bmp.Erase(color);
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    private sealed record RecordedHttpRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? IdempotencyKey,
        string? XRequestId,
        string Body);

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(
            Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string bodyText = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            return await _handler(request, bodyText, cancellationToken);
        }
    }
}
