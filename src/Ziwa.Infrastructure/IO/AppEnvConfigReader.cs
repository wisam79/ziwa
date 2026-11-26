namespace Ziwa.Infrastructure.IO;

internal static class AppEnvConfigReader
{
    public static Func<string, string?> CreateReader(string appDataRoot)
    {
        var appDataVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        return name =>
            appDataVars.TryGetValue(name, out string? v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : Environment.GetEnvironmentVariable(name);
    }

    public static Uri? ResolveSecureUriOrNull(string? configured, bool allowHttpForTesting)
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
