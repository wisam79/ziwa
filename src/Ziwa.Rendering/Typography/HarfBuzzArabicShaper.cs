using System.Collections.Immutable;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using Ziwa.Domain.Primitives;

namespace Ziwa.Rendering.Typography;

public readonly record struct ShapedGlyph(
    ushort GlyphId,
    float XMm,
    float YOffsetMm,
    float AdvanceMm,
    uint Cluster);

public sealed record ShapedLineResult(
    ImmutableArray<ShapedGlyph> Glyphs,
    float TotalAdvanceMm,
    float AscentMm,
    float DescentMm);

public sealed class HarfBuzzArabicShaper
{
    private readonly FontRegistry _fontRegistry;

    public HarfBuzzArabicShaper(FontRegistry fontRegistry)
    {
        _fontRegistry = fontRegistry;
    }

    public ShapedLineResult ShapeLine(
        string line,
        string fontFamily,
        int fontWeight,
        double fontSizeMm,
        TextDirectionMode directionMode,
        double letterSpacingEm = 0.0)
    {
        FontRegistry.FontEntry entry = _fontRegistry.ResolveFont(fontFamily, fontWeight);
        using SKFont skFont = CreateSkFont(entry.Typeface, fontSizeMm);
        SKFontMetrics metrics = skFont.Metrics;
        float ascent = -metrics.Ascent;
        float descent = metrics.Descent;

        if (string.IsNullOrEmpty(line))
        {
            return new ShapedLineResult([], 0f, ascent, descent);
        }

        bool baseIsRtl = ResolveBaseDirectionIsRtl(line, directionMode);
        List<TextRun> visualRuns = SplitAndOrderVisualRuns(line, baseIsRtl);

        var glyphs = ImmutableArray.CreateBuilder<ShapedGlyph>();
        float scale = (float)fontSizeMm / entry.UnitsPerEm;
        float extraSpacingMm = (float)(letterSpacingEm * fontSizeMm);
        float cursorX = 0f;

        foreach (TextRun run in visualRuns)
        {
            using var hbBuffer = new HarfBuzzSharp.Buffer();
            hbBuffer.Direction = run.IsRtl ? Direction.RightToLeft : Direction.LeftToRight;
            hbBuffer.Script = run.IsArabic ? Script.Arabic : Script.Latin;
            hbBuffer.AddUtf16(run.Text);

            entry.HbFont.Shape(hbBuffer);

            ReadOnlySpan<GlyphInfo> infos = hbBuffer.GetGlyphInfoSpan();
            ReadOnlySpan<GlyphPosition> positions = hbBuffer.GetGlyphPositionSpan();

            for (int i = 0; i < infos.Length; i++)
            {
                float xOffsetMm = positions[i].XOffset * scale;
                float yOffsetMm = positions[i].YOffset * scale;
                float advanceMm = positions[i].XAdvance * scale;
                float spacedAdvance = advanceMm > 0f ? Math.Max(0.1f, advanceMm + extraSpacingMm) : 0f;

                glyphs.Add(new ShapedGlyph(
                    GlyphId: (ushort)infos[i].Codepoint,
                    XMm: cursorX + xOffsetMm,
                    YOffsetMm: yOffsetMm,
                    AdvanceMm: spacedAdvance,
                    Cluster: infos[i].Cluster));

                cursorX += spacedAdvance;
            }
        }

        return new ShapedLineResult(glyphs.ToImmutable(), cursorX, ascent, descent);
    }

    public SKTextBlob? BuildTextBlob(
        ShapedLineResult shaped,
        string fontFamily,
        int fontWeight,
        double fontSizeMm,
        float originXMm,
        float baselineYMm)
    {
        if (shaped.Glyphs.IsDefaultOrEmpty)
        {
            return null;
        }

        FontRegistry.FontEntry entry = _fontRegistry.ResolveFont(fontFamily, fontWeight);
        using SKFont skFont = CreateSkFont(entry.Typeface, fontSizeMm);
        using var builder = new SKTextBlobBuilder();

        SKPositionedRunBuffer runBuffer = builder.AllocatePositionedRun(skFont, shaped.Glyphs.Length);
        Span<ushort> glyphIds = runBuffer.GetGlyphSpan();
        Span<SKPoint> positions = runBuffer.GetPositionSpan();

        for (int i = 0; i < shaped.Glyphs.Length; i++)
        {
            ShapedGlyph g = shaped.Glyphs[i];
            glyphIds[i] = g.GlyphId;
            // HarfBuzz +Y points upward; Skia +Y points downward.
            positions[i] = new SKPoint(originXMm + g.XMm, baselineYMm - g.YOffsetMm);
        }

        return builder.Build();
    }

    internal static bool ResolveBaseDirectionIsRtl(string text, TextDirectionMode mode)
    {
        return mode switch
        {
            TextDirectionMode.Rtl => true,
            TextDirectionMode.Ltr => false,
            _ => DetectFirstStrongIsRtl(text),
        };
    }

    public static List<TextRun> SplitAndOrderVisualRuns(string text, bool baseIsRtl)
    {
        var logicalRuns = new List<TextRun>();
        if (string.IsNullOrEmpty(text))
        {
            return logicalRuns;
        }

        var buffer = new StringBuilder();
        bool currentIsRtl = baseIsRtl;
        bool currentIsArabic = baseIsRtl;
        bool hasStarted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            CharBidiClass cls = ClassifyChar(c, i, text, baseIsRtl);

            bool charIsRtl;
            bool charIsArabic;
            switch (cls)
            {
                case CharBidiClass.RtlArabic:
                    charIsRtl = true;
                    charIsArabic = true;
                    break;
                case CharBidiClass.Ltr:
                    charIsRtl = false;
                    charIsArabic = false;
                    break;
                default:
                    charIsRtl = hasStarted ? currentIsRtl : baseIsRtl;
                    charIsArabic = hasStarted ? currentIsArabic : baseIsRtl;
                    break;
            }

            if (hasStarted && charIsRtl != currentIsRtl)
            {
                logicalRuns.Add(new TextRun(buffer.ToString(), currentIsRtl, currentIsArabic));
                buffer.Clear();
            }

            currentIsRtl = charIsRtl;
            currentIsArabic = charIsArabic;
            hasStarted = true;
            buffer.Append(c);
        }

        if (buffer.Length > 0)
        {
            logicalRuns.Add(new TextRun(buffer.ToString(), currentIsRtl, currentIsArabic));
        }

        if (baseIsRtl && logicalRuns.Count > 1)
        {
            logicalRuns.Reverse();
        }

        return logicalRuns;
    }

    private static bool DetectFirstStrongIsRtl(string text)
    {
        foreach (char c in text)
        {
            if (IsArabicChar(c) || IsHebrewChar(c))
            {
                return true;
            }

            if (IsLatinLetter(c))
            {
                return false;
            }
        }

        return true;
    }

    private static CharBidiClass ClassifyChar(char c, int index, string text, bool baseIsRtl)
    {
        if (IsArabicChar(c) || IsHebrewChar(c))
        {
            return CharBidiClass.RtlArabic;
        }

        if (IsLatinLetter(c) || char.IsAsciiDigit(c))
        {
            return CharBidiClass.Ltr;
        }

        // Look ahead for neutral chars (spaces/symbols) between two LTR tokens inside an RTL line.
        bool prevLtr = LookBackIsLtr(index, text);
        bool nextLtr = LookAheadIsLtr(index, text);
        if (prevLtr && nextLtr)
        {
            return CharBidiClass.Ltr;
        }

        bool prevRtl = LookBackIsRtl(index, text);
        bool nextRtl = LookAheadIsRtl(index, text);
        if (prevRtl && nextRtl)
        {
            return CharBidiClass.RtlArabic;
        }

        return baseIsRtl ? CharBidiClass.Neutral : CharBidiClass.Ltr;
    }

    private static bool LookBackIsLtr(int index, string text)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            char c = text[i];
            if (IsLatinLetter(c) || char.IsAsciiDigit(c))
            {
                return true;
            }

            if (IsArabicChar(c) || IsHebrewChar(c))
            {
                return false;
            }
        }

        return false;
    }

    private static bool LookAheadIsLtr(int index, string text)
    {
        for (int i = index + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (IsLatinLetter(c) || char.IsAsciiDigit(c))
            {
                return true;
            }

            if (IsArabicChar(c) || IsHebrewChar(c))
            {
                return false;
            }
        }

        return false;
    }

    private static bool LookBackIsRtl(int index, string text)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            char c = text[i];
            if (IsArabicChar(c) || IsHebrewChar(c))
            {
                return true;
            }

            if (IsLatinLetter(c) || char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return false;
    }

    private static bool LookAheadIsRtl(int index, string text)
    {
        for (int i = index + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (IsArabicChar(c) || IsHebrewChar(c))
            {
                return true;
            }

            if (IsLatinLetter(c) || char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsArabicChar(char c) =>
        (c >= '\u0600' && c <= '\u06FF') ||
        (c >= '\u0750' && c <= '\u077F') ||
        (c >= '\u08A0' && c <= '\u08FF') ||
        (c >= '\uFB50' && c <= '\uFDFF') ||
        (c >= '\uFE70' && c <= '\uFEFF');

    private static bool IsHebrewChar(char c) => c >= '\u0590' && c <= '\u05FF';

    private static bool IsLatinLetter(char c) =>
        (c >= 'A' && c <= 'Z') ||
        (c >= 'a' && c <= 'z') ||
        (c >= '\u00C0' && c <= '\u024F');

    private static SKFont CreateSkFont(SKTypeface typeface, double fontSizeMm)
    {
        return new SKFont(typeface, (float)fontSizeMm)
        {
            LinearMetrics = true,
            Subpixel = true,
            Hinting = SKFontHinting.None,
            Edging = SKFontEdging.Antialias,
        };
    }

    public readonly record struct TextRun(string Text, bool IsRtl, bool IsArabic);

    private enum CharBidiClass
    {
        RtlArabic,
        Ltr,
        Neutral,
    }
}
