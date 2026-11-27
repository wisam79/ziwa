using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Bridge;
using Ziwa.Infrastructure.Ai;
using Ziwa.Infrastructure.Auth;
using Ziwa.Infrastructure.Bridge;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase5PhoneBridgeTests
{
    [Fact]
    public async Task LocalBridgeServer_EnforcesTokenSizeLimitMagicBytesAndImportsValidImages()
    {
        var assetStore = new AssetStore();
        var sessionStore = new CloudAuthSessionStore();
        var authOptions = new SupabaseAuthOptions
        {
            SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
            SupabasePublishableKey = "sb_publishable_test",
        };
        var bridgeOptions = new PhoneBridgeOptions
        {
            PreferredPort = 0,
            MaxUploadBytes = 64 * 1024,
            BindLoopbackOnly = true,
            EnableCloudSync = false,
        };

        await using var bridge = new KestrelPhoneBridgeService(assetStore, sessionStore, authOptions, bridgeOptions);
        var received = new List<PhoneBridgeReceivedPhoto>();
        bridge.PhotoReceived += received.Add;

        PhoneBridgeStartInfo startInfo = await bridge.StartAsync();
        Assert.True(startInfo.IsRunning);
        Assert.True(startInfo.Port > 0);
        Assert.Equal(32, startInfo.Token.Length);
        Assert.StartsWith("http://127.0.0.1:", startInfo.LocalUrl, StringComparison.Ordinal);

        // Idempotent second StartAsync returns same session
        PhoneBridgeStartInfo second = await bridge.StartAsync();
        Assert.Equal(startInfo.Port, second.Port);
        Assert.Equal(startInfo.Token, second.Token);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{startInfo.Port}") };

        // 1. Mobile page (/cam and /) + ping + manifest
        using HttpResponseMessage camResp = await http.GetAsync($"/cam?token={startInfo.Token}");
        Assert.Equal(HttpStatusCode.OK, camResp.StatusCode);
        string camHtml = await camResp.Content.ReadAsStringAsync();
        Assert.Contains("Ziwa Studio", camHtml, StringComparison.Ordinal);
        Assert.Contains("capture=\"environment\"", camHtml, StringComparison.Ordinal);
        Assert.Equal("nosniff", camResp.Headers.GetValues("X-Content-Type-Options").Single());

        using HttpResponseMessage pingResp = await http.GetAsync("/ping");
        Assert.Equal(HttpStatusCode.OK, pingResp.StatusCode);

        using HttpResponseMessage manifestResp = await http.GetAsync("/manifest.json");
        Assert.Equal(HttpStatusCode.OK, manifestResp.StatusCode);

        // 2. Reject missing/invalid token -> 401
        using (var noTokenForm = new MultipartFormDataContent())
        {
            noTokenForm.Add(new ByteArrayContent(CreateSamplePng(8, 6)), "photo", "shot.png");
            using HttpResponseMessage unauthResp = await http.PostAsync("/upload", noTokenForm);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);
        }

        using (var wrongTokenForm = new MultipartFormDataContent())
        {
            wrongTokenForm.Add(new ByteArrayContent(CreateSamplePng(8, 6)), "photo", "shot.png");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = wrongTokenForm };
            req.Headers.Add("X-Bridge-Token", "wrong-token");
            using HttpResponseMessage wrongResp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongResp.StatusCode);
        }

        // 3. Reject oversized payload -> 413
        byte[] oversized = new byte[bridgeOptions.MaxUploadBytes + 4096];
        oversized[0] = 0x89;
        oversized[1] = 0x50;
        oversized[2] = 0x4E;
        oversized[3] = 0x47;
        using (var bigForm = new MultipartFormDataContent())
        {
            bigForm.Add(new ByteArrayContent(oversized), "photo", "huge.png");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = bigForm };
            req.Headers.Add("X-Bridge-Token", startInfo.Token);
            using HttpResponseMessage bigResp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigResp.StatusCode);
        }

        // 4. Reject non-image file (even with .jpg extension) -> 415
        using (var textForm = new MultipartFormDataContent())
        {
            textForm.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("<script>alert(1)</script>")), "photo", "fake.jpg");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = textForm };
            req.Headers.Add("X-Bridge-Token", startInfo.Token);
            using HttpResponseMessage nonImgResp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonImgResp.StatusCode);
        }

        // 5. Reject spoofed magic bytes with corrupt image body -> 400
        using (var corruptForm = new MultipartFormDataContent())
        {
            corruptForm.Add(new ByteArrayContent([0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02]), "photo", "broken.jpg");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = corruptForm };
            req.Headers.Add("X-Bridge-Token", startInfo.Token);
            using HttpResponseMessage corruptResp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, corruptResp.StatusCode);
        }

        // 6. Accept valid PNG with valid X-Bridge-Token -> 200 + AssetStore + PhotoReceived
        byte[] validPng = CreateSamplePng(12, 9);
        using (var okForm = new MultipartFormDataContent())
        {
            var fileContent = new ByteArrayContent(validPng);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            okForm.Add(fileContent, "photo", "camera_shot.png");

            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/upload") { Content = okForm };
            req.Headers.Add("X-Bridge-Token", startInfo.Token);
            using HttpResponseMessage okResp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        }

        Assert.Single(received);
        Assert.Equal(12, received[0].WidthPx);
        Assert.Equal(9, received[0].HeightPx);
        Assert.Equal(PhoneBridgePhotoSource.LocalLan, received[0].SourceKind);
        Assert.True(assetStore.TryGetMetadata(received[0].AssetId, out _));
        Assert.Equal(1, bridge.CurrentStatus.ReceivedCount);

        await bridge.StopAsync();
        Assert.False(bridge.CurrentStatus.IsRunning);
    }

    [Fact]
    public async Task CloudBridge_PairsViaRealtimeAndDownloadsThenDeletesPhotosSafely()
    {
        var assetStore = new AssetStore();
        const string userId = "11111111-2222-4333-8444-555555555555";
        var sessionStore = new CloudAuthSessionStore(
            new CloudAuthSessionSnapshot("cloud-jwt-1", userId, "studio@ziwa.app", "pro"));

        byte[] sampleJpeg = CreateSampleJpeg(10, 8);
        var deletedPaths = new List<string>();
        string capturedPrefix = string.Empty;
        var wsConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteDoneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeSocket = new FakeRealtimeWebSocket();

        using var handler = new DelegateHttpHandler((req, bodyText, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/storage/v1/object/list/phone-bridge" && req.Method == HttpMethod.Post)
            {
                Assert.Equal("Bearer cloud-jwt-1", req.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            }

            if (path.StartsWith("/storage/v1/object/phone-bridge/", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                Assert.Equal("Bearer cloud-jwt-1", req.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(sampleJpeg),
                });
            }

            if (path == "/storage/v1/object/phone-bridge" && req.Method == HttpMethod.Delete)
            {
                using JsonDocument doc = JsonDocument.Parse(bodyText);
                foreach (JsonElement el in doc.RootElement.GetProperty("prefixes").EnumerateArray())
                {
                    deletedPaths.Add(el.GetString() ?? string.Empty);
                }

                deleteDoneTcs.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var authOptions = new SupabaseAuthOptions
        {
            SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
            SupabasePublishableKey = "sb_publishable_test",
            CustomHttpHandler = handler,
        };
        var bridgeOptions = new PhoneBridgeOptions
        {
            PreferredPort = 0,
            BindLoopbackOnly = true,
            EnableCloudSync = true,
            CloudPollInterval = TimeSpan.FromSeconds(30),
        };

        await using var bridge = new KestrelPhoneBridgeService(
            assetStore,
            sessionStore,
            authOptions,
            bridgeOptions,
            httpMessageHandler: null,
            webSocketFactory: (_, _) =>
            {
                wsConnectedTcs.TrySetResult();
                return Task.FromResult<WebSocket>(fakeSocket);
            });

        var pairedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var photoTcs = new TaskCompletionSource<PhoneBridgeReceivedPhoto>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.DevicePaired += d => pairedTcs.TrySetResult(d);
        bridge.PhotoReceived += p => photoTcs.TrySetResult(p);

        PhoneBridgeStartInfo startInfo = await bridge.StartAsync();
        capturedPrefix = $"{userId}/{startInfo.Token}";
        Assert.Equal($"https://grido.cloud-ip.cc/cam#session={capturedPrefix}", startInfo.CloudUrl);

        await wsConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fakeSocket.EnqueueIncomingText(JsonSerializer.Serialize(new
        {
            @event = "broadcast",
            topic = $"realtime:phone-bridge:{startInfo.Token}",
            payload = new
            {
                type = "broadcast",
                @event = "paired",
                payload = new { device = "iPhone 15 Pro" },
            },
        }));

        string paired = await pairedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("iPhone 15 Pro", paired);

        string objectPath = $"{capturedPrefix}/shot_1.jpg";
        fakeSocket.EnqueueIncomingText(JsonSerializer.Serialize(new
        {
            @event = "broadcast",
            topic = $"realtime:phone-bridge:{startInfo.Token}",
            payload = new
            {
                type = "broadcast",
                @event = "photo-ready",
                payload = new { path = objectPath },
            },
        }));

        PhoneBridgeReceivedPhoto received = await photoTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10, received.WidthPx);
        Assert.Equal(8, received.HeightPx);
        Assert.Equal(PhoneBridgePhotoSource.CloudRelay, received.SourceKind);
        Assert.True(assetStore.TryGetMetadata(received.AssetId, out _));

        await deleteDoneTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(objectPath, deletedPaths);

        await bridge.StopAsync();
    }

    private static byte[] CreateSamplePng(int width, int height)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bmp.Erase(new SKColor(40, 110, 220, 255));
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateSampleJpeg(int width, int height)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bmp.Erase(new SKColor(220, 90, 40, 255));
        using SKData data = bmp.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    private sealed class FakeRealtimeWebSocket : WebSocket
    {
        private readonly System.Threading.Channels.Channel<byte[]> _incoming =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueIncomingText(string text) =>
            _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            ValueWebSocketReceiveResult r = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            return new WebSocketReceiveResult(r.Count, r.MessageType, r.EndOfMessage);
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                byte[] next = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                next.AsSpan().CopyTo(buffer.Span);
                return new ValueWebSocketReceiveResult(next.Length, WebSocketMessageType.Text, endOfMessage: true);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
            }
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : string.Empty;
            return await handler(request, body, cancellationToken).ConfigureAwait(false);
        }
    }
}
