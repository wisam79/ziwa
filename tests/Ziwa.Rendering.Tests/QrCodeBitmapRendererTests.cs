using SkiaSharp;
using Ziwa.Rendering.Qr;

namespace Ziwa.Rendering.Tests;

public sealed class QrCodeBitmapRendererTests
{
    [Theory]
    [InlineData("http://192.168.1.45:8741/cam?token=0123456789abcdef0123456789abcdef")]
    [InlineData("https://grido.cloud-ip.cc/cam#session=11111111-2222-4333-8444-555555555555/0123456789abcdef0123456789abcdef")]
    public void Encode_ProducesValidFinderPatternsAndTimingLine(string url)
    {
        QrCodeMatrix matrix = QrCodeBitmapRenderer.Encode(url);

        Assert.True(matrix.Size >= 21);
        Assert.Equal(0, (matrix.Size - 17) % 4);
        Assert.Equal(matrix.Size * matrix.Size, matrix.Modules.Length);

        // Top-left, top-right, bottom-left finder centers must be dark, with white separator ring
        AssertFinderPattern(matrix, 0, 0);
        AssertFinderPattern(matrix, matrix.Size - 7, 0);
        AssertFinderPattern(matrix, 0, matrix.Size - 7);

        // Timing pattern alternating bits along row/col 6
        for (int i = 8; i < matrix.Size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, matrix[i, 6]);
            Assert.Equal(i % 2 == 0, matrix[6, i]);
        }
    }

    [Fact]
    public void RenderBitmap_DrawsWhiteQuietZoneAndDarkModules()
    {
        using SKBitmap bitmap = QrCodeBitmapRenderer.RenderBitmap(
            "https://grido.cloud-ip.cc/cam#session=11111111-2222-4333-8444-555555555555/abcdef1234567890abcdef1234567890",
            pixelSize: 320);

        Assert.True(bitmap.Width >= 200);
        Assert.Equal(bitmap.Width, bitmap.Height);
        Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));

        bool hasDarkPixel = false;
        for (int y = 0; y < bitmap.Height && !hasDarkPixel; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red < 50)
                {
                    hasDarkPixel = true;
                    break;
                }
            }
        }

        Assert.True(hasDarkPixel);
    }

    [Fact]
    public void Encode_ThrowsForEmptyOrOversizedPayload()
    {
        Assert.ThrowsAny<ArgumentException>(() => QrCodeBitmapRenderer.Encode(string.Empty));
        Assert.Throws<ArgumentException>(() => QrCodeBitmapRenderer.Encode(new string('a', 300)));
    }

    private static void AssertFinderPattern(QrCodeMatrix matrix, int startX, int startY)
    {
        for (int y = 0; y < 7; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                bool expectedDark = x == 0 || x == 6 || y == 0 || y == 6 || (x is >= 2 and <= 4 && y is >= 2 and <= 4);
                Assert.Equal(expectedDark, matrix[startX + x, startY + y]);
            }
        }
    }
}
