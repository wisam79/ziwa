using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Ai;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Ai;

public sealed class ModalAiUpscaler : IModalAiUpscaler, IDisposable
{
    private readonly AssetStore _assetStore;
    private readonly CloudAiOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public ModalAiUpscaler(AssetStore assetStore, CloudAiOptions? options = null)
    {
        _assetStore = assetStore;
        _options = options ?? new CloudAiOptions();

        if (_options.CustomHttpHandler is not null)
        {
            _httpClient = new HttpClient(_options.CustomHttpHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });
            _ownsHttpClient = true;
        }

        // Total deadline (180s default) is enforced via CancellationTokenSource across all stages.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<CloudUpscaleResult> EnhanceImageAsync(
        AssetId sourceAssetId,
        CloudAuthSessionSnapshot session,
        double fidelity = 0.7,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (sourceAssetId.IsEmpty)
        {
            throw new CloudAiException(CloudAiErrorCode.InvalidInput, "الصورة المحددة غير صالحة للترميم السحابي");
        }

        if (session is null || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new CloudAiException(CloudAiErrorCode.Unauthenticated, "تسجيل الدخول مطلوب لاستخدام ميزة الترميم السحابي");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!_assetStore.TryGetEncodedBytes(sourceAssetId, out byte[]? encodedSourceBytes) || encodedSourceBytes is null)
        {
            throw new CloudAiException(CloudAiErrorCode.InvalidInput, "تعذر العثور على بيانات الصورة المصدر للترميم السحابي");
        }

        TimeSpan budget = _options.RequestTimeout > TimeSpan.Zero ? _options.RequestTimeout : CloudAiOptions.DefaultTimeout;
        using var timeoutCts = new CancellationTokenSource(budget);
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        CancellationToken budgetCt = budgetCts.Token;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            PreparedUploadPayload prepared = await Task.Run(
                () => PrepareUploadPayload(encodedSourceBytes, budgetCt),
                budgetCt);

            CloudAiQuotaSnapshot? precheckQuota = await TryCheckSupabaseQuotaPrecheckAsync(
                session,
                prepared.EncodedByteLength,
                budgetCt);

            string requestId = GenerateCryptographicUuidV4();
            double clampedFidelity = Math.Clamp(double.IsFinite(fidelity) ? fidelity : 0.7, 0.1, 1.0);
            int planDailyLimit = CloudAiQuotaPolicy.GetPlanDailyLimit(session.Plan);

            string requestJson = BuildEnhanceRequestJson(prepared.DataUrl, planDailyLimit, requestId, clampedFidelity);
            byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);
            if (requestBytes.Length > CloudAiOptions.MaxRequestJsonBytes)
            {
                throw new CloudAiException(
                    CloudAiErrorCode.PayloadTooLarge,
                    "حجم طلب الصورة يتجاوز الحد الآمن للإرسال (30 MiB)");
            }

            Uri modalUri = _options.ResolveValidatedModalEndpointUri();
            using var request = new HttpRequestMessage(HttpMethod.Post, modalUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken.Trim());
            request.Headers.TryAddWithoutValidation("Idempotency-Key", requestId);
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
            request.Headers.TryAddWithoutValidation("User-Agent", "ZiwaStudio-Desktop/1.0.0");
            request.Content = new ByteArrayContent(requestBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            // Zero automatic retries: each POST reserves user daily quota server-side.
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                budgetCt);

            byte[] responseBytes = await ReadBoundedResponseBytesAsync(response, budgetCt);

            if (!response.IsSuccessStatusCode)
            {
                throw MapModalHttpFailure(response.StatusCode, responseBytes);
            }

            ParsedModalSuccessResponse parsed = ParseModalSuccessResponse(responseBytes);

            FinalizedOutputAsset outputAsset = await Task.Run(
                () => FinalizeAndRegisterOutput(parsed.DecodedImageBytes, prepared, budgetCt),
                budgetCt);

            stopwatch.Stop();

            CloudAiQuotaSnapshot? resolvedQuota = ResolveServerQuotaSnapshot(
                parsed.UsedToday,
                parsed.DailyLimit,
                precheckQuota,
                session.UserIdentifier);

            double execSeconds = parsed.ExecutionSeconds is double s && double.IsFinite(s) && s >= 0
                ? s
                : Math.Round(stopwatch.Elapsed.TotalSeconds, 2);

            return new CloudUpscaleResult(
                UpscaledAssetId: outputAsset.AssetId,
                SourceWidthPx: prepared.WidthPx,
                SourceHeightPx: prepared.HeightPx,
                OutputWidthPx: outputAsset.WidthPx,
                OutputHeightPx: outputAsset.HeightPx,
                ExecutionSeconds: execSeconds,
                RequestId: string.IsNullOrWhiteSpace(parsed.RequestId) ? requestId : parsed.RequestId,
                ServerQuotaSnapshot: resolvedQuota);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new CloudAiException(
                CloudAiErrorCode.Timeout,
                "انتهت مهلة الترميم السحابي (180 ثانية)؛ قد تكون الحصة قد حُجزت على الخادم — تحقق من رصيدك قبل إعادة المحاولة",
                ex);
        }
        catch (CloudAiException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new CloudAiException(
                CloudAiErrorCode.NetworkOrServerError,
                "تعذر الاتصال بخادم الترميم السحابي (Modal CPU) — تحقق من اتصال الإنترنت",
                ex);
        }
    }

    public static string GenerateCryptographicUuidV4()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return string.Create(36, bytes.ToArray(), static (chars, b) =>
        {
            ReadOnlySpan<char> hex = "0123456789abcdef";
            int pos = 0;
            for (int i = 0; i < 16; i++)
            {
                if (i is 4 or 6 or 8 or 10)
                {
                    chars[pos++] = '-';
                }

                chars[pos++] = hex[b[i] >> 4];
                chars[pos++] = hex[b[i] & 0x0F];
            }
        });
    }

    private static PreparedUploadPayload PrepareUploadPayload(byte[] encodedSourceBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using SKBitmap normalized = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(encodedSourceBytes);
        int width = normalized.Width;
        int height = normalized.Height;
        long pixelCount = (long)width * height;
        long maxPixels = (long)CloudAiOptions.MaxInputSidePx * CloudAiOptions.MaxInputSidePx;

        if (width <= 0 || height <= 0 || width > CloudAiOptions.MaxInputSidePx || height > CloudAiOptions.MaxInputSidePx || pixelCount > maxPixels)
        {
            throw new CloudAiException(
                CloudAiErrorCode.DimensionsTooLarge,
                $"الحد الأقصى للصورة المرسلة للترميم السحابي هو {CloudAiOptions.MaxInputSidePx}×{CloudAiOptions.MaxInputSidePx} بكسل حفاظاً على تكبير 2x");
        }

        ct.ThrowIfCancellationRequested();

        bool hasTransparency = DetectTransparencyAndOptionallyCaptureAlpha(normalized, out byte[]? alphaChannel);

        using SKImage image = SKImage.FromBitmap(normalized);
        SKEncodedImageFormat format = hasTransparency ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        int quality = hasTransparency ? 100 : 92;
        using SKData encoded = image.Encode(format, quality)
            ?? throw new CloudAiException(CloudAiErrorCode.InvalidInput, "تعذر ترميز الصورة لإرسالها للخادم السحابي");

        byte[] payloadBytes = encoded.ToArray();
        if (payloadBytes.Length > CloudAiOptions.MaxInputDecodedBytes)
        {
            throw new CloudAiException(
                CloudAiErrorCode.PayloadTooLarge,
                "حجم الصورة يتجاوز الحد الأقصى المسموح به للترميم السحابي (15 MiB)");
        }

        string base64 = Convert.ToBase64String(payloadBytes);
        if (base64.Length > CloudAiOptions.MaxBase64Chars)
        {
            throw new CloudAiException(
                CloudAiErrorCode.PayloadTooLarge,
                "حجم الصورة المرمّزة يتجاوز الحد المسموح به (20 MiB)");
        }

        string mimeType = hasTransparency ? "image/png" : "image/jpeg";
        string dataUrl = $"data:{mimeType};base64,{base64}";

        return new PreparedUploadPayload(width, height, payloadBytes.Length, dataUrl, alphaChannel);
    }

    private async Task<CloudAiQuotaSnapshot?> TryCheckSupabaseQuotaPrecheckAsync(
        CloudAuthSessionSnapshot session,
        int imageByteLength,
        CancellationToken budgetCt)
    {
        Uri? supabaseBaseUri = _options.ResolveValidatedSupabaseBaseUri();
        if (supabaseBaseUri is null || !Guid.TryParse(session.UserId, out Guid userGuid))
        {
            return null;
        }

        Uri rpcUri = new(supabaseBaseUri, "/rest/v1/rpc/check_and_record_ai_usage");
        string rpcJson = JsonSerializer.Serialize(new
        {
            p_user_id = userGuid.ToString("D"),
            p_daily_limit = 0,
            p_image_bytes = (long)imageByteLength,
            p_exec_seconds = 0,
            p_cost_usd = 0,
            p_check_only = true,
        });

        using var rpcTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(budgetCt, rpcTimeoutCts.Token);

        using var req = new HttpRequestMessage(HttpMethod.Post, rpcUri);
        req.Headers.TryAddWithoutValidation("apikey", _options.SupabasePublishableKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken.Trim());
        req.Content = new StringContent(rpcJson, Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage resp = await _httpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token);

            byte[] rawBody = await ReadBoundedResponseBytesAsync(resp, linkedCts.Token, maxBytes: 64 * 1024);

            if (!resp.IsSuccessStatusCode)
            {
                string? rpcMessage = TryExtractJsonStringProperty(rawBody, "message");
                if (resp.StatusCode == HttpStatusCode.TooManyRequests ||
                    (rpcMessage is not null && rpcMessage.Contains("تجاوزت الحد اليومي", StringComparison.Ordinal)))
                {
                    throw new CloudAiException(
                        CloudAiErrorCode.QuotaExceeded,
                        string.IsNullOrWhiteSpace(rpcMessage)
                            ? "تم الوصول للحد اليومي لاستخدام الترميم السحابي"
                            : rpcMessage);
                }

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new CloudAiException(
                        CloudAiErrorCode.Unauthorized,
                        "انتهت صلاحية الجلسة أو التوكن غير صالح — يرجى إعادة تسجيل الدخول");
                }

                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(rawBody);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("used_today", out JsonElement usedEl) &&
                root.TryGetProperty("daily_limit", out JsonElement limitEl) &&
                usedEl.TryGetInt32(out int usedToday) &&
                limitEl.TryGetInt32(out int dailyLimit) &&
                dailyLimit > 0 &&
                usedToday >= 0)
            {
                return new CloudAiQuotaSnapshot(usedToday, dailyLimit, DateTimeOffset.UtcNow, session.UserIdentifier);
            }
        }
        catch (CloudAiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (budgetCt.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Transient RPC pre-check errors must not invent fake client quota; Modal still enforces server RPC atomically.
        }

        return null;
    }

    private static string BuildEnhanceRequestJson(
        string imageDataUrl,
        int dailyLimit,
        string requestId,
        double fidelity) =>
        JsonSerializer.Serialize(new
        {
            image = imageDataUrl,
            dailyLimit,
            requestId,
            fidelity,
        });

    private static async Task<byte[]> ReadBoundedResponseBytesAsync(
        HttpResponseMessage response,
        CancellationToken ct,
        int maxBytes = CloudAiOptions.MaxResponseBytes)
    {
        if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            throw new CloudAiException(
                CloudAiErrorCode.PayloadTooLarge,
                "حجم استجابة الخادم السحابي يتجاوز الحد الآمن (50 MiB)");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int totalRead = 0;

        while (true)
        {
            int read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new CloudAiException(
                    CloudAiErrorCode.PayloadTooLarge,
                    "حجم استجابة الخادم السحابي يتجاوز الحد الآمن (50 MiB)");
            }

            buffer.Write(chunk, 0, read);
        }

        if (response.Content.Headers.ContentLength is long expected && totalRead != expected)
        {
            throw new CloudAiException(
                CloudAiErrorCode.NetworkOrServerError,
                "انقطعت استجابة خادم الترميم السحابي قبل اكتمالها");
        }

        return buffer.ToArray();
    }

    private static CloudAiException MapModalHttpFailure(HttpStatusCode statusCode, byte[] responseBytes)
    {
        string? serverError = TryExtractJsonStringProperty(responseBytes, "error");
        string suffix = !string.IsNullOrWhiteSpace(serverError) ? $": {serverError}" : string.Empty;

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new CloudAiException(
                CloudAiErrorCode.Unauthorized,
                $"طلب غير مصرح به: الجلسة منتهية أو غير صالحة{(suffix.Length > 0 ? $" ({serverError})" : string.Empty)}"),
            HttpStatusCode.Conflict => new CloudAiException(
                CloudAiErrorCode.DuplicateRequestConflict,
                $"طلب مكرر (409 Conflict): تمت معالجة هذا المعرّف مسبقاً لحماية رصيدك{suffix}"),
            HttpStatusCode.RequestEntityTooLarge => new CloudAiException(
                CloudAiErrorCode.PayloadTooLarge,
                $"حجم الصورة أو الطلب يتجاوز الحد المسموح به على الخادم (413){suffix}"),
            HttpStatusCode.TooManyRequests => new CloudAiException(
                CloudAiErrorCode.QuotaExceeded,
                !string.IsNullOrWhiteSpace(serverError)
                    ? serverError!
                    : "تم الوصول للحد اليومي المسموح به لاستخدام الذكاء الاصطناعي السحابي"),
            HttpStatusCode.NotFound => new CloudAiException(
                CloudAiErrorCode.EndpointNotFound,
                "تعذر العثور على نقطة النهاية للترميم السحابي (404 Not Found)"),
            HttpStatusCode.ServiceUnavailable => new CloudAiException(
                CloudAiErrorCode.ServiceUnavailable,
                $"خدمة التحقق من الحصة أو الخادم السحابي غير متاحة مؤقتاً (503){suffix}"),
            _ => new CloudAiException(
                CloudAiErrorCode.NetworkOrServerError,
                $"فشل خادم الترميم السحابي ({(int)statusCode}){suffix}"),
        };
    }

    private static ParsedModalSuccessResponse ParseModalSuccessResponse(byte[] responseBytes)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CloudAiException(CloudAiErrorCode.NetworkOrServerError, "استجابة خادم الترميم ليست كائن JSON صالحاً");
            }

            if (root.TryGetProperty("usage_recorded", out JsonElement usageRecordedEl) &&
                usageRecordedEl.ValueKind == JsonValueKind.False)
            {
                throw new CloudAiException(
                    CloudAiErrorCode.UnrecordedUsage,
                    "تعذر توثيق استهلاك حصة الترميم السحابي في قاعدة البيانات — لم يتم تطبيق النتيجة");
            }

            if (root.TryGetProperty("success", out JsonElement successEl) &&
                successEl.ValueKind == JsonValueKind.False)
            {
                string? err = TryExtractJsonStringProperty(root, "error");
                throw new CloudAiException(
                    CloudAiErrorCode.NetworkOrServerError,
                    string.IsNullOrWhiteSpace(err) ? "أفاد الخادم السحابي بفشل عملية الترميم" : err);
            }

            string? errField = TryExtractJsonStringProperty(root, "error");
            if (!string.IsNullOrWhiteSpace(errField))
            {
                throw new CloudAiException(CloudAiErrorCode.NetworkOrServerError, errField);
            }

            string? imagePayload = TryExtractJsonStringProperty(root, "image");
            byte[] decodedImageBytes = DecodeResponseImageDataUrl(imagePayload);

            double? execSec = root.TryGetProperty("execution_seconds", out JsonElement execEl) && execEl.TryGetDouble(out double d)
                ? d
                : null;
            string? requestId = TryExtractJsonStringProperty(root, "request_id");

            int? usedToday = root.TryGetProperty("used_today", out JsonElement usedEl) && usedEl.TryGetInt32(out int u)
                ? u
                : null;
            int? dailyLimit = root.TryGetProperty("daily_limit", out JsonElement limEl) && limEl.TryGetInt32(out int lim)
                ? lim
                : null;

            return new ParsedModalSuccessResponse(decodedImageBytes, execSec, requestId, usedToday, dailyLimit);
        }
        catch (JsonException ex)
        {
            throw new CloudAiException(CloudAiErrorCode.NetworkOrServerError, "استجابة JSON من خادم الترميم غير صالحة", ex);
        }
    }

    private static byte[] DecodeResponseImageDataUrl(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new CloudAiException(CloudAiErrorCode.InvalidInput, "رد الخادم السحابي يفتقر لبيانات الصورة المرمّمة");
        }

        string base64Part = payload;
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = payload.IndexOf(',');
            if (commaIdx <= 0)
            {
                throw new CloudAiException(CloudAiErrorCode.InvalidInput, "تنسيق data URL للصورة المستلمة غير صالح");
            }

            string header = payload[..commaIdx];
            if (!header.Equals("data:image/png;base64", StringComparison.OrdinalIgnoreCase) &&
                !header.Equals("data:image/jpeg;base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new CloudAiException(CloudAiErrorCode.InvalidInput, "نوع الصورة المستلمة من الخادم السحابي غير مدعوم");
            }

            base64Part = payload[(commaIdx + 1)..];
        }

        if (base64Part.Length == 0 || base64Part.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new CloudAiException(CloudAiErrorCode.InvalidInput, "ترميز base64 للصورة المستلمة غير صالح");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64Part);
        }
        catch (FormatException ex)
        {
            throw new CloudAiException(CloudAiErrorCode.InvalidInput, "فشل فك ترميز base64 للصورة المرمّمة", ex);
        }

        if (decoded.Length == 0 || decoded.Length > CloudAiOptions.MaxResponseBytes)
        {
            throw new CloudAiException(CloudAiErrorCode.PayloadTooLarge, "حجم الصورة المستلمة يتجاوز الحد المسموح به");
        }

        return decoded;
    }

    private FinalizedOutputAsset FinalizeAndRegisterOutput(
        byte[] decodedImageBytes,
        PreparedUploadPayload prepared,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using (SKData codecData = SKData.CreateCopy(decodedImageBytes))
        using (SKCodec? codec = SKCodec.Create(codecData))
        {
            if (codec is null)
            {
                throw new CloudAiException(CloudAiErrorCode.InvalidInput, "الصورة المعادة من الخادم ليست ملفاً نقطياً صالحاً");
            }

            int outW = codec.Info.Width;
            int outH = codec.Info.Height;
            if (outW <= 0 || outH <= 0 || outW > CloudAiOptions.MaxOutputSidePx || outH > CloudAiOptions.MaxOutputSidePx)
            {
                throw new CloudAiException(
                    CloudAiErrorCode.InvalidInput,
                    $"أبعاد الصورة المعادة تتجاوز الحد الأقصى ({CloudAiOptions.MaxOutputSidePx}×{CloudAiOptions.MaxOutputSidePx})");
            }
        }

        ct.ThrowIfCancellationRequested();

        using SKBitmap outputBitmap = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(decodedImageBytes);
        byte[] finalBytes = decodedImageBytes;

        if (prepared.OriginalAlphaChannel is not null && !BitmapHasTransparency(outputBitmap))
        {
            finalBytes = ApplyPreservedTransparencyToPngBytes(
                outputBitmap,
                prepared.OriginalAlphaChannel,
                prepared.WidthPx,
                prepared.HeightPx,
                ct);
        }

        ct.ThrowIfCancellationRequested();
        AssetId assetId = _assetStore.RegisterAsset(finalBytes);
        return new FinalizedOutputAsset(assetId, outputBitmap.Width, outputBitmap.Height);
    }

    private static bool DetectTransparencyAndOptionallyCaptureAlpha(SKBitmap rgbaBitmap, out byte[]? alphaChannel)
    {
        ReadOnlySpan<byte> pixels = rgbaBitmap.GetPixelSpan();
        int width = rgbaBitmap.Width;
        int height = rgbaBitmap.Height;
        int rowBytes = rgbaBitmap.RowBytes;
        bool hasAlpha = false;

        for (int y = 0; y < height && !hasAlpha; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                if (pixels[rowStart + (x * 4) + 3] < 255)
                {
                    hasAlpha = true;
                    break;
                }
            }
        }

        if (!hasAlpha)
        {
            alphaChannel = null;
            return false;
        }

        alphaChannel = AlphaMaskCompositor.ExtractAlphaChannel(rgbaBitmap);
        return true;
    }

    private static bool BitmapHasTransparency(SKBitmap rgbaBitmap)
    {
        ReadOnlySpan<byte> pixels = rgbaBitmap.GetPixelSpan();
        int width = rgbaBitmap.Width;
        int height = rgbaBitmap.Height;
        int rowBytes = rgbaBitmap.RowBytes;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                if (pixels[rowStart + (x * 4) + 3] < 255)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[] ApplyPreservedTransparencyToPngBytes(
        SKBitmap opaqueOutputBitmap,
        byte[] sourceAlpha,
        int sourceWidth,
        int sourceHeight,
        CancellationToken ct)
    {
        int outW = opaqueOutputBitmap.Width;
        int outH = opaqueOutputBitmap.Height;

        using var maskSrc = new SKBitmap(new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        IntPtr maskPtr = maskSrc.GetPixels();
        int maskRowBytes = maskSrc.RowBytes;
        byte[] whiteRow = new byte[sourceWidth * 4];
        for (int y = 0; y < sourceHeight; y++)
        {
            ct.ThrowIfCancellationRequested();
            int alphaOffset = y * sourceWidth;
            for (int x = 0; x < sourceWidth; x++)
            {
                int idx = x * 4;
                whiteRow[idx] = 255;
                whiteRow[idx + 1] = 255;
                whiteRow[idx + 2] = 255;
                whiteRow[idx + 3] = sourceAlpha[alphaOffset + x];
            }

            System.Runtime.InteropServices.Marshal.Copy(whiteRow, 0, IntPtr.Add(maskPtr, y * maskRowBytes), whiteRow.Length);
        }

        maskSrc.NotifyPixelsChanged();

        using var composed = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(composed))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(opaqueOutputBitmap, 0f, 0f);
            using var dstInPaint = new SKPaint
            {
                BlendMode = SKBlendMode.DstIn,
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true,
            };
            canvas.DrawBitmap(maskSrc, new SKRect(0f, 0f, outW, outH), dstInPaint);
        }

        ct.ThrowIfCancellationRequested();
        using SKImage image = SKImage.FromBitmap(composed);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static CloudAiQuotaSnapshot? ResolveServerQuotaSnapshot(
        int? responseUsedToday,
        int? responseDailyLimit,
        CloudAiQuotaSnapshot? precheckQuota,
        string userIdentifier)
    {
        if (responseUsedToday is int used && used >= 0 && responseDailyLimit is int limit && limit > 0)
        {
            return new CloudAiQuotaSnapshot(used, limit, DateTimeOffset.UtcNow, userIdentifier);
        }

        if (precheckQuota is not null && precheckQuota.DailyLimit > 0 && precheckQuota.UsedToday >= 0)
        {
            return new CloudAiQuotaSnapshot(precheckQuota.UsedToday + 1, precheckQuota.DailyLimit, DateTimeOffset.UtcNow, userIdentifier);
        }

        return null;
    }

    private static string? TryExtractJsonStringProperty(byte[] jsonBytes, string propertyName)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            return TryExtractJsonStringProperty(doc.RootElement, propertyName);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractJsonStringProperty(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out JsonElement prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record PreparedUploadPayload(
        int WidthPx,
        int HeightPx,
        int EncodedByteLength,
        string DataUrl,
        byte[]? OriginalAlphaChannel);

    private sealed record ParsedModalSuccessResponse(
        byte[] DecodedImageBytes,
        double? ExecutionSeconds,
        string? RequestId,
        int? UsedToday,
        int? DailyLimit);

    private sealed record FinalizedOutputAsset(
        AssetId AssetId,
        int WidthPx,
        int HeightPx);
}
