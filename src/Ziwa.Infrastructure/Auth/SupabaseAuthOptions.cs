using Ziwa.Infrastructure.Ai;
using Ziwa.Infrastructure.IO;

namespace Ziwa.Infrastructure.Auth;

public sealed class SupabaseAuthOptions
{
    public const string DefaultSupabaseUrl = CloudAiOptions.DefaultSupabaseUrl;

    public string? SupabaseUrl { get; init; } = DefaultSupabaseUrl;
    public string? SupabasePublishableKey { get; init; }
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan OAuthCallbackTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public bool AllowInsecureHttpForTesting { get; init; }
    public HttpMessageHandler? CustomHttpHandler { get; init; }
    public Func<DateTimeOffset>? Clock { get; init; }
    public string AppVersion { get; init; } = "ZiwaStudio-1.0.0";

    public Uri? ResolveValidatedBaseUri()
    {
        if (string.IsNullOrWhiteSpace(SupabasePublishableKey))
        {
            return null;
        }

        string candidate = string.IsNullOrWhiteSpace(SupabaseUrl) ? DefaultSupabaseUrl : SupabaseUrl!;
        return AppEnvConfigReader.ResolveSecureUriOrNull(candidate, AllowInsecureHttpForTesting);
    }

    public static SupabaseAuthOptions LoadFromAppDataAndEnvironment(
        string appDataRoot,
        CloudAiOptions? cloudAiFallback = null)
    {
        Func<string, string?> readSetting = AppEnvConfigReader.CreateReader(appDataRoot);

        string url = readSetting("SUPABASE_URL")
            ?? cloudAiFallback?.SupabaseUrl
            ?? DefaultSupabaseUrl;

        string? key = readSetting("SUPABASE_PUBLISHABLE_KEY")
            ?? readSetting("SUPABASE_ANON_KEY")
            ?? cloudAiFallback?.SupabasePublishableKey;

        return new SupabaseAuthOptions
        {
            SupabaseUrl = url,
            SupabasePublishableKey = key,
        };
    }
}
