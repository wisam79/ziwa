using System.Text;
using SkiaSharp;

namespace Ziwa.Rendering.Qr;

public readonly record struct QrCodeMatrix(int Size, bool[] Modules)
{
    public bool this[int x, int y] => Modules[(y * Size) + x];
}

/// <summary>
/// Pure ISO/IEC 18004 Byte-mode (ECC Level M, Versions 1..12) QR encoder and SkiaSharp renderer.
/// </summary>
public static class QrCodeBitmapRenderer
{
    private readonly record struct VersionSpec(
        int Version,
        int DataCodewords,
        int EccPerBlock,
        int Group1Blocks,
        int Group1Data,
        int Group2Blocks,
        int Group2Data,
        int[] AlignmentCenters);

    private static readonly VersionSpec[] VersionsM =
    [
        new(1, 16, 10, 1, 16, 0, 0, []),
        new(2, 28, 16, 1, 28, 0, 0, [6, 18]),
        new(3, 44, 26, 1, 44, 0, 0, [6, 22]),
        new(4, 64, 18, 2, 32, 0, 0, [6, 26]),
        new(5, 86, 24, 2, 43, 0, 0, [6, 30]),
        new(6, 108, 16, 4, 27, 0, 0, [6, 34]),
        new(7, 124, 18, 4, 31, 0, 0, [6, 22, 38]),
        new(8, 154, 22, 2, 38, 2, 39, [6, 24, 42]),
        new(9, 182, 22, 3, 36, 2, 37, [6, 26, 46]),
        new(10, 216, 26, 4, 43, 1, 44, [6, 28, 50]),
        new(11, 254, 30, 1, 50, 4, 51, [6, 30, 54]),
        new(12, 290, 22, 6, 36, 2, 37, [6, 32, 58]),
    ];

    public static QrCodeMatrix Encode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        byte[] payload = Encoding.UTF8.GetBytes(text);
        VersionSpec spec = SelectVersion(payload.Length);
        byte[] dataCodewords = BuildDataCodewords(payload, spec);
        byte[] interleaved = BuildInterleavedCodewords(dataCodewords, spec);

        int size = 17 + (4 * spec.Version);
        var functionMask = new bool[size * size];
        var baseModules = new bool[size * size];

        DrawFunctionPatterns(spec, size, baseModules, functionMask);
        PlaceDataBits(size, baseModules, functionMask, interleaved);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        bool[] bestModules = baseModules;

        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[])baseModules.Clone();
            ApplyMask(size, candidate, functionMask, mask);
            DrawFormatInfo(size, candidate, mask);
            if (spec.Version >= 7)
            {
                DrawVersionInfo(size, candidate, spec.Version);
            }

            int penalty = ComputePenalty(size, candidate);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                bestModules = candidate;
            }
        }

        _ = bestMask;
        return new QrCodeMatrix(size, bestModules);
    }

    public static SKBitmap RenderBitmap(string text, int pixelSize = 360, int quietZoneModules = 4)
    {
        QrCodeMatrix matrix = Encode(text);
        int totalModules = matrix.Size + (quietZoneModules * 2);
        int scale = Math.Max(2, pixelSize / totalModules);
        int bitmapSize = totalModules * scale;

        var bitmap = new SKBitmap(bitmapSize, bitmapSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var darkPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(17, 24, 39, 255),
            IsAntialias = false,
        };

        for (int y = 0; y < matrix.Size; y++)
        {
            for (int x = 0; x < matrix.Size; x++)
            {
                if (!matrix[x, y])
                {
                    continue;
                }

                float left = (x + quietZoneModules) * scale;
                float top = (y + quietZoneModules) * scale;
                canvas.DrawRect(left, top, scale, scale, darkPaint);
            }
        }

        return bitmap;
    }

    private static VersionSpec SelectVersion(int byteCount)
    {
        foreach (VersionSpec spec in VersionsM)
        {
            int countBits = spec.Version <= 9 ? 8 : 16;
            int totalDataBits = spec.DataCodewords * 8;
            int neededBits = 4 + countBits + (byteCount * 8);
            if (neededBits <= totalDataBits)
            {
                return spec;
            }
        }

        throw new ArgumentException($"QR payload length ({byteCount} bytes) exceeds supported capacity (287 bytes).", nameof(byteCount));
    }

    private static byte[] BuildDataCodewords(byte[] payload, VersionSpec spec)
    {
        var bits = new List<bool>(spec.DataCodewords * 8);
        AppendBits(bits, 0b0100, 4); // Byte mode
        int countBits = spec.Version <= 9 ? 8 : 16;
        AppendBits(bits, payload.Length, countBits);

        foreach (byte b in payload)
        {
            AppendBits(bits, b, 8);
        }

        int capacityBits = spec.DataCodewords * 8;
        int terminatorLength = Math.Min(4, capacityBits - bits.Count);
        AppendBits(bits, 0, terminatorLength);

        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        var codewords = new byte[spec.DataCodewords];
        int cwIndex = 0;
        for (int i = 0; i < bits.Count; i += 8)
        {
            byte value = 0;
            for (int b = 0; b < 8; b++)
            {
                if (bits[i + b])
                {
                    value |= (byte)(1 << (7 - b));
                }
            }

            codewords[cwIndex++] = value;
        }

        byte[] padBytes = [0xEC, 0x11];
        int padIdx = 0;
        while (cwIndex < codewords.Length)
        {
            codewords[cwIndex++] = padBytes[padIdx];
            padIdx ^= 1;
        }

        return codewords;
    }

    private static byte[] BuildInterleavedCodewords(byte[] dataCodewords, VersionSpec spec)
    {
        int totalBlocks = spec.Group1Blocks + spec.Group2Blocks;
        var dataBlocks = new byte[totalBlocks][];
        var eccBlocks = new byte[totalBlocks][];
        byte[] generator = BuildReedSolomonGenerator(spec.EccPerBlock);

        int offset = 0;
        int blockIdx = 0;
        for (int i = 0; i < spec.Group1Blocks; i++)
        {
            dataBlocks[blockIdx] = dataCodewords.AsSpan(offset, spec.Group1Data).ToArray();
            eccBlocks[blockIdx] = ComputeReedSolomonRemainder(dataBlocks[blockIdx], generator, spec.EccPerBlock);
            offset += spec.Group1Data;
            blockIdx++;
        }

        for (int i = 0; i < spec.Group2Blocks; i++)
        {
            dataBlocks[blockIdx] = dataCodewords.AsSpan(offset, spec.Group2Data).ToArray();
            eccBlocks[blockIdx] = ComputeReedSolomonRemainder(dataBlocks[blockIdx], generator, spec.EccPerBlock);
            offset += spec.Group2Data;
            blockIdx++;
        }

        var result = new List<byte>(dataCodewords.Length + (totalBlocks * spec.EccPerBlock));
        int maxDataLen = Math.Max(spec.Group1Data, spec.Group2Data);
        for (int i = 0; i < maxDataLen; i++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                if (i < dataBlocks[b].Length)
                {
                    result.Add(dataBlocks[b][i]);
                }
            }
        }

        for (int i = 0; i < spec.EccPerBlock; i++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                result.Add(eccBlocks[b][i]);
            }
        }

        return [.. result];
    }

    private static byte[] BuildReedSolomonGenerator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;

        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = GfMultiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ComputeReedSolomonRemainder(byte[] data, byte[] generator, int degree)
    {
        var remainder = new byte[degree];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, degree - 1);
            remainder[degree - 1] = 0;
            for (int i = 0; i < degree; i++)
            {
                remainder[i] ^= GfMultiply(generator[i], factor);
            }
        }

        return remainder;
    }

    private static byte GfMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private static void DrawFunctionPatterns(VersionSpec spec, int size, bool[] modules, bool[] isFunction)
    {
        for (int i = 0; i < size; i++)
        {
            SetFunctionModule(size, modules, isFunction, 6, i, i % 2 == 0);
            SetFunctionModule(size, modules, isFunction, i, 6, i % 2 == 0);
        }

        DrawFinderPattern(size, modules, isFunction, 3, 3);
        DrawFinderPattern(size, modules, isFunction, size - 4, 3);
        DrawFinderPattern(size, modules, isFunction, 3, size - 4);

        int[] centers = spec.AlignmentCenters;
        for (int i = 0; i < centers.Length; i++)
        {
            for (int j = 0; j < centers.Length; j++)
            {
                bool overlapsFinder =
                    (i == 0 && j == 0) ||
                    (i == 0 && j == centers.Length - 1) ||
                    (i == centers.Length - 1 && j == 0);
                if (!overlapsFinder)
                {
                    DrawAlignmentPattern(size, modules, isFunction, centers[i], centers[j]);
                }
            }
        }

        DrawFormatInfo(size, modules, 0, isFunction);
        if (spec.Version >= 7)
        {
            DrawVersionInfo(size, modules, spec.Version, isFunction);
        }
    }

    private static void DrawFinderPattern(int size, bool[] modules, bool[] isFunction, int centerX, int centerY)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = centerX + dx;
                int y = centerY + dy;
                if (x < 0 || x >= size || y < 0 || y >= size)
                {
                    continue;
                }

                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                bool dark = dist is 0 or 1 or 3;
                SetFunctionModule(size, modules, isFunction, x, y, dark);
            }
        }
    }

    private static void DrawAlignmentPattern(int size, bool[] modules, bool[] isFunction, int centerX, int centerY)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunctionModule(size, modules, isFunction, centerX + dx, centerY + dy, dist != 1);
            }
        }
    }

    private static void DrawFormatInfo(int size, bool[] modules, int mask, bool[]? isFunction = null)
    {
        // ECC Level M = 00
        int data = mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        int bits = ((data << 10) | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++)
        {
            SetFunctionModule(size, modules, isFunction, 8, i, ((bits >> i) & 1) != 0);
        }

        SetFunctionModule(size, modules, isFunction, 8, 7, ((bits >> 6) & 1) != 0);
        SetFunctionModule(size, modules, isFunction, 8, 8, ((bits >> 7) & 1) != 0);
        SetFunctionModule(size, modules, isFunction, 7, 8, ((bits >> 8) & 1) != 0);
        for (int i = 9; i < 15; i++)
        {
            SetFunctionModule(size, modules, isFunction, 14 - i, 8, ((bits >> i) & 1) != 0);
        }

        for (int i = 0; i < 8; i++)
        {
            SetFunctionModule(size, modules, isFunction, size - 1 - i, 8, ((bits >> i) & 1) != 0);
        }

        for (int i = 8; i < 15; i++)
        {
            SetFunctionModule(size, modules, isFunction, 8, size - 15 + i, ((bits >> i) & 1) != 0);
        }

        SetFunctionModule(size, modules, isFunction, 8, size - 8, true);
    }

    private static void DrawVersionInfo(int size, bool[] modules, int version, bool[]? isFunction = null)
    {
        int rem = version;
        for (int i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }

        int bits = (version << 12) | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;
            int a = size - 11 + (i % 3);
            int b = i / 3;
            SetFunctionModule(size, modules, isFunction, a, b, bit);
            SetFunctionModule(size, modules, isFunction, b, a, bit);
        }
    }

    private static void PlaceDataBits(int size, bool[] modules, bool[] isFunction, byte[] data)
    {
        int bitIndex = 0;
        int totalBits = data.Length * 8;

        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    int idx = (y * size) + x;
                    if (isFunction[idx] || bitIndex >= totalBits)
                    {
                        continue;
                    }

                    bool bit = ((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                    modules[idx] = bit;
                    bitIndex++;
                }
            }
        }
    }

    private static void ApplyMask(int size, bool[] modules, bool[] isFunction, int mask)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int idx = (y * size) + x;
                if (isFunction[idx])
                {
                    continue;
                }

                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => ((x / 3) + (y / 2)) % 2 == 0,
                    5 => ((x * y) % 2) + ((x * y) % 3) == 0,
                    6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
                    7 => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
                    _ => false,
                };

                if (invert)
                {
                    modules[idx] = !modules[idx];
                }
            }
        }
    }

    private static int ComputePenalty(int size, bool[] modules)
    {
        int penalty = 0;

        for (int y = 0; y < size; y++)
        {
            int runLength = 0;
            bool runColor = false;
            int finderHistory = 0;
            for (int x = 0; x < size; x++)
            {
                bool color = modules[(y * size) + x];
                if (x == 0 || color != runColor)
                {
                    if (runLength >= 5)
                    {
                        penalty += 3 + (runLength - 5);
                    }

                    runColor = color;
                    runLength = 1;
                }
                else
                {
                    runLength++;
                }

                finderHistory = ((finderHistory << 1) & 0x7FF) | (color ? 1 : 0);
                if (x >= 10 && (finderHistory == 0x5D0 || finderHistory == 0x05D))
                {
                    penalty += 40;
                }
            }

            if (runLength >= 5)
            {
                penalty += 3 + (runLength - 5);
            }
        }

        for (int x = 0; x < size; x++)
        {
            int runLength = 0;
            bool runColor = false;
            int finderHistory = 0;
            for (int y = 0; y < size; y++)
            {
                bool color = modules[(y * size) + x];
                if (y == 0 || color != runColor)
                {
                    if (runLength >= 5)
                    {
                        penalty += 3 + (runLength - 5);
                    }

                    runColor = color;
                    runLength = 1;
                }
                else
                {
                    runLength++;
                }

                finderHistory = ((finderHistory << 1) & 0x7FF) | (color ? 1 : 0);
                if (y >= 10 && (finderHistory == 0x5D0 || finderHistory == 0x05D))
                {
                    penalty += 40;
                }
            }

            if (runLength >= 5)
            {
                penalty += 3 + (runLength - 5);
            }
        }

        int darkCount = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool c = modules[(y * size) + x];
                if (c)
                {
                    darkCount++;
                }

                if (x < size - 1 && y < size - 1 &&
                    c == modules[(y * size) + x + 1] &&
                    c == modules[((y + 1) * size) + x] &&
                    c == modules[((y + 1) * size) + x + 1])
                {
                    penalty += 3;
                }
            }
        }

        int total = size * size;
        int k = Math.Abs((darkCount * 20) - (total * 10)) / total;
        penalty += k * 10;

        return penalty;
    }

    private static void SetFunctionModule(int size, bool[] modules, bool[]? isFunction, int x, int y, bool value)
    {
        int idx = (y * size) + x;
        modules[idx] = value;
        if (isFunction is not null)
        {
            isFunction[idx] = true;
        }
    }

    private static void AppendBits(List<bool> bits, int value, int bitCount)
    {
        for (int i = bitCount - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }
}
