using System.Buffers.Binary;
using System.IO.Compression;

namespace Ziwa.Application.Services;

public static class ProceduralEmblemPngBuilder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] BuildEmblemPngBytes(int size = 256)
    {
        int stride = (size * 4) + 1;
        byte[] raw = new byte[stride * size];

        double half = (size - 1) * 0.5;
        for (int y = 0; y < size; y++)
        {
            int rowStart = y * stride;
            raw[rowStart] = 0; // PNG filter type None

            double ny = (y - half) / half;
            for (int x = 0; x < size; x++)
            {
                double nx = (x - half) / half;
                double dist = System.Math.Sqrt((nx * nx) + (ny * ny));
                double diamond = System.Math.Abs(nx) + System.Math.Abs(ny);

                double t = System.Math.Clamp(((nx + 1.0) * 0.35) + ((ny + 1.0) * 0.35), 0.0, 1.0);
                byte r = (byte)System.Math.Round(11 + (t * 28));
                byte g = (byte)System.Math.Round(92 + (t * 78));
                byte b = (byte)System.Math.Round(171 + (t * 65));
                byte a = 255;

                if (dist <= 0.72 && dist >= 0.62)
                {
                    r = 242;
                    g = 204;
                    b = 96;
                }
                else if (diamond <= 0.48)
                {
                    r = 255;
                    g = 255;
                    b = 255;
                }

                int px = rowStart + 1 + (x * 4);
                raw[px] = r;
                raw[px + 1] = g;
                raw[px + 2] = b;
                raw[px + 3] = a;
            }
        }

        using var idatPayload = new System.IO.MemoryStream();
        using (var zlib = new ZLibStream(idatPayload, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), size);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;

        using var png = new System.IO.MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, idatPayload.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(System.IO.Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        stream.Write(lenBytes);
        stream.Write(type);
        stream.Write(data);

        uint crc = 0xFFFF_FFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        crc ^= 0xFFFF_FFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
