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
        return ResolveSecureUriOrNull(candidate, AllowInsecureHttpForTesting);
    }

    public static CloudAiOptions LoadFromAppDataAndEnvironment(string appDataRoot)
    {
        Dictionary<string, string> appDataVars = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(appDataRoot))
        {
            string envFile = Path.Combine(appDataRoot, ".env");
            if (File.Exists(envFile))
            {
                foreach (string rawLine in File.ReadLines(envFile))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line[..eq].Trim();
                    string val = line[(eq + 1)..].Trim().Trim('"', '\'');
                    if (key.Length > 0 && val.Length > 0)
                    {
                        appDataVars[key] = val;
                    }
                }
            }
        }

        string? ReadSetting(string name) =>
            appDataVars.TryGetValue(name, out string? v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : Environment.GetEnvironmentVariable(name);

        return new CloudAiOptions
        {
            ModalEndpointUrl = ReadSetting("MODAL_AI_URL"),
            SupabaseUrl = ReadSetting("SUPABASE_URL"),
            SupabasePublishableKey = ReadSetting("SUPABASE_ANON_KEY"),
        };
    }

    private static Uri ResolveSecureUri(string? configured, string fallbackUrl, bool allowHttpForTesting)
    {
        Uri? resolved = ResolveSecureUriOrNull(configured, allowHttpForTesting);
        return resolved ?? new Uri(fallbackUrl, UriKind.Absolute);
    }

    private static Uri? ResolveSecureUriOrNull(string? configured, bool allowHttpForTesting)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        string normalized = configured.Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        if (allowHttpForTesting && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return null;
    }
}
