using Ziwa.Infrastructure.IO;

namespace Ziwa.Infrastructure.Ai;

public sealed class CloudAiOptions
{
    public const string DefaultModalEndpointUrl =
        "https://wisamsamir78--grido-ai-upscaler-imageenhancer-enhance.modal.run";

    public const string DefaultSupabaseUrl =
        "https://mvovehnyvoiawvwaurav.supabase.co";

    public const int MaxInputSidePx = 2048;
    public const int MaxOutputSidePx = 4096;
    public const int MaxInputDecodedBytes = 15 * 1024 * 1024;
    public const int MaxBase64Chars = 20 * 1024 * 1024;
    public const int MaxRequestJsonBytes = 30 * 1024 * 1024;
    public const int MaxResponseBytes = 50 * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    public CloudAiOptions()
    {
    }

    public string? ModalEndpointUrl { get; init; }
    public string? SupabaseUrl { get; init; }
    public string? SupabasePublishableKey { get; init; }
    public TimeSpan RequestTimeout { get; init; } = DefaultTimeout;
    public bool AllowInsecureHttpForTesting { get; init; }
    public HttpMessageHandler? CustomHttpHandler { get; init; }

    public Uri ResolveValidatedModalEndpointUri() =>
        ResolveSecureUri(ModalEndpointUrl, DefaultModalEndpointUrl, AllowInsecureHttpForTesting);

    public Uri? ResolveValidatedSupabaseBaseUri()
    {
        if (string.IsNullOrWhiteSpace(SupabasePublishableKey))
        {
            return null;
        }

        string candidate = string.IsNullOrWhiteSpace(SupabaseUrl) ? DefaultSupabaseUrl : SupabaseUrl!;
        return AppEnvConfigReader.ResolveSecureUriOrNull(candidate, AllowInsecureHttpForTesting);
    }

    public static CloudAiOptions LoadFromAppDataAndEnvironment(string appDataRoot)
    {
        Func<string, string?> readSetting = AppEnvConfigReader.CreateReader(appDataRoot);

        return new CloudAiOptions
        {
            ModalEndpointUrl = readSetting("MODAL_AI_URL"),
            SupabaseUrl = readSetting("SUPABASE_URL"),
            SupabasePublishableKey = readSetting("SUPABASE_PUBLISHABLE_KEY") ?? readSetting("SUPABASE_ANON_KEY"),
        };
    }

    private static Uri ResolveSecureUri(string? configured, string fallbackUrl, bool allowHttpForTesting) =>
        AppEnvConfigReader.ResolveSecureUriOrNull(configured, allowHttpForTesting)
        ?? new Uri(fallbackUrl, UriKind.Absolute);
}
