using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;

namespace Ziwa.Rendering.Typography;

public sealed class FontRegistry : IDisposable
{
    public const string DefaultFontFamily = "Cairo";

    private const string ResourcePrefix = "Ziwa.Rendering.Resources.Fonts.";

    private static readonly IReadOnlyDictionary<(string Family, bool Bold), string> FontResourceMap =
        new Dictionary<(string Family, bool Bold), string>
        {
            [("Cairo", false)] = "Cairo.ttf",
            [("Cairo", true)] = "Cairo.ttf",
            [("Tajawal", false)] = "Tajawal-Regular.ttf",
            [("Tajawal", true)] = "Tajawal-Bold.ttf",
            [("IBM Plex Sans Arabic", false)] = "IBMPlexSansArabic-Regular.ttf",
            [("IBM Plex Sans Arabic", true)] = "IBMPlexSansArabic-Bold.ttf",
        };

    private readonly ConcurrentDictionary<(string Family, bool Bold), FontEntry> _cache = new();
    private bool _disposed;

    public IReadOnlyList<string> RegisteredFamilies { get; } =
    [
        "Cairo",
        "Tajawal",
        "IBM Plex Sans Arabic",
    ];

    public FontEntry ResolveFont(string familyName, int fontWeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string normalizedFamily = NormalizeFamily(familyName);
        bool isBold = fontWeight >= 600;

        return _cache.GetOrAdd((normalizedFamily, isBold), key => LoadFontEntry(key.Family, key.Bold));
    }

    private static string NormalizeFamily(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return DefaultFontFamily;
        }

        if (familyName.Equals("Cairo", StringComparison.OrdinalIgnoreCase))
        {
            return "Cairo";
        }

        if (familyName.Equals("Tajawal", StringComparison.OrdinalIgnoreCase))
        {
            return "Tajawal";
        }

        if (familyName.Equals("IBM Plex Sans Arabic", StringComparison.OrdinalIgnoreCase))
        {
            return "IBM Plex Sans Arabic";
        }

        return DefaultFontFamily;
    }

    private static FontEntry LoadFontEntry(string family, bool isBold)
    {
        string fileName = FontResourceMap[(family, isBold)];
        string resourceName = ResourcePrefix + fileName;
        Assembly assembly = typeof(FontRegistry).Assembly;

        using Stream? resourceStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded font resource '{resourceName}' was not found.");

        using var bufferStream = new MemoryStream();
        resourceStream.CopyTo(bufferStream);
        byte[] fontBytes = bufferStream.ToArray();

        SKTypeface typeface = SKTypeface.FromData(SKData.CreateCopy(fontBytes))
            ?? throw new InvalidOperationException($"Failed to create SKTypeface from '{resourceName}'.");

        var pinHandle = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
        var blob = new Blob(pinHandle.AddrOfPinnedObject(), fontBytes.Length, MemoryMode.ReadOnly, () =>
        {
            if (pinHandle.IsAllocated)
            {
                pinHandle.Free();
            }
        });

        var face = new Face(blob, 0);
        int unitsPerEm = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1000;
        var hbFont = new Font(face);
        hbFont.SetScale(unitsPerEm, unitsPerEm);

        return new FontEntry(family, isBold, typeface, blob, face, hbFont, unitsPerEm);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (FontEntry entry in _cache.Values)
        {
            entry.Dispose();
        }

        _cache.Clear();
    }

    public sealed class FontEntry : IDisposable
    {
        internal FontEntry(
            string family,
            bool isBold,
            SKTypeface typeface,
            Blob blob,
            Face face,
            Font hbFont,
            int unitsPerEm)
        {
            Family = family;
            IsBold = isBold;
            Typeface = typeface;
            Blob = blob;
            Face = face;
            HbFont = hbFont;
            UnitsPerEm = unitsPerEm;
        }

        public string Family { get; }
        public bool IsBold { get; }
        public SKTypeface Typeface { get; }
        public Blob Blob { get; }
        public Face Face { get; }
        public Font HbFont { get; }
        public int UnitsPerEm { get; }

        public void Dispose()
        {
            HbFont.Dispose();
            Face.Dispose();
            Blob.Dispose();
            Typeface.Dispose();
        }
    }
}
