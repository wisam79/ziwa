using System.Buffers.Binary;
using Ziwa.Domain.Math;

namespace Ziwa.Rendering.Export;

public static class RasterDpiMetadataInjector
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] InjectPngPhysChunk(ReadOnlySpan<byte> pngBytes, double dpi)
    {
        if (pngBytes.Length < 33 || !pngBytes[..8].SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("Input bytes are not a valid PNG stream.");
        }

        uint ppm = DomainMath.DpiToPixelsPerMeter(dpi);

        using var output = new MemoryStream(pngBytes.Length + 24);
        output.Write(PngSignature);

        int offset = 8;
        bool physWritten = false;

        while (offset + 12 <= pngBytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(pngBytes.Slice(offset, 4));
            if (length < 0 || offset + 12 + length > pngBytes.Length)
            {
                throw new InvalidDataException("Corrupted PNG chunk length encountered.");
            }

            ReadOnlySpan<byte> type = pngBytes.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = pngBytes.Slice(offset + 8, length);
            int totalChunkLength = 12 + length;

            if (type.SequenceEqual("pHYs"u8))
            {
                offset += totalChunkLength;
                continue;
            }

            output.Write(pngBytes.Slice(offset, totalChunkLength));

            if (!physWritten && type.SequenceEqual("IHDR"u8))
            {
                WritePhysChunk(output, ppm);
                physWritten = true;
            }

            offset += totalChunkLength;
        }

        if (!physWritten)
        {
            throw new InvalidDataException("PNG stream did not contain an IHDR chunk.");
        }

        return output.ToArray();
    }

    public static byte[] InjectJpegJfifDensity(ReadOnlySpan<byte> jpegBytes, double dpi)
    {
        if (jpegBytes.Length < 4 || jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8)
        {
            throw new InvalidDataException("Input bytes are not a valid JPEG stream.");
        }

        double rounded = Math.Clamp(Math.Round(dpi, MidpointRounding.AwayFromZero), 1.0, ushort.MaxValue);
        ushort density = (ushort)rounded;

        if (jpegBytes.Length >= 20 &&
            jpegBytes[2] == 0xFF &&
            jpegBytes[3] == 0xE0 &&
            jpegBytes.Slice(6, 5).SequenceEqual("JFIF\0"u8))
        {
            byte[] updated = jpegBytes.ToArray();
            updated[13] = 1; // DPI (pixels per inch)
            BinaryPrimitives.WriteUInt16BigEndian(updated.AsSpan(14, 2), density);
            BinaryPrimitives.WriteUInt16BigEndian(updated.AsSpan(16, 2), density);
            return updated;
        }

        Span<byte> jfifSegment = stackalloc byte[18];
        jfifSegment[0] = 0xFF;
        jfifSegment[1] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(jfifSegment.Slice(2, 2), 16);
        "JFIF\0"u8.CopyTo(jfifSegment.Slice(4, 5));
        jfifSegment[9] = 1;  // Major version 1
        jfifSegment[10] = 1; // Minor version 1
        jfifSegment[11] = 1; // Units: dots per inch
        BinaryPrimitives.WriteUInt16BigEndian(jfifSegment.Slice(12, 2), density);
        BinaryPrimitives.WriteUInt16BigEndian(jfifSegment.Slice(14, 2), density);
        jfifSegment[16] = 0;
        jfifSegment[17] = 0;

        byte[] output = new byte[jpegBytes.Length + jfifSegment.Length];
        output[0] = 0xFF;
        output[1] = 0xD8;
        jfifSegment.CopyTo(output.AsSpan(2));
        jpegBytes[2..].CopyTo(output.AsSpan(2 + jfifSegment.Length));
        return output;
    }

    public static bool TryReadPngDpi(ReadOnlySpan<byte> pngBytes, out double dpiX, out double dpiY)
    {
        dpiX = 0.0;
        dpiY = 0.0;

        if (pngBytes.Length < 33 || !pngBytes[..8].SequenceEqual(PngSignature))
        {
            return false;
        }

        int offset = 8;
        while (offset + 12 <= pngBytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(pngBytes.Slice(offset, 4));
            if (length < 0 || offset + 12 + length > pngBytes.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> type = pngBytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("pHYs"u8) && length == 9)
            {
                ReadOnlySpan<byte> data = pngBytes.Slice(offset + 8, 9);
                if (data[8] != 1)
                {
                    return false;
                }

                uint ppmX = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4));
                uint ppmY = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                dpiX = (ppmX * DomainMath.MmPerInch) / 1000.0;
                dpiY = (ppmY * DomainMath.MmPerInch) / 1000.0;
                return true;
            }

            offset += 12 + length;
        }

        return false;
    }

    public static bool TryReadJpegDpi(ReadOnlySpan<byte> jpegBytes, out double dpiX, out double dpiY)
    {
        dpiX = 0.0;
        dpiY = 0.0;

        if (jpegBytes.Length < 20 || jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8)
        {
            return false;
        }

        if (jpegBytes[2] == 0xFF &&
            jpegBytes[3] == 0xE0 &&
            jpegBytes.Slice(6, 5).SequenceEqual("JFIF\0"u8) &&
            jpegBytes[13] == 1)
        {
            dpiX = BinaryPrimitives.ReadUInt16BigEndian(jpegBytes.Slice(14, 2));
            dpiY = BinaryPrimitives.ReadUInt16BigEndian(jpegBytes.Slice(16, 2));
            return dpiX > 0 && dpiY > 0;
        }

        return false;
    }

    private static void WritePhysChunk(Stream stream, uint pixelsPerMeter)
    {
        Span<byte> data = stackalloc byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(0, 4), pixelsPerMeter);
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(4, 4), pixelsPerMeter);
        data[8] = 1; // unit specifier: meter

        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, 9);
        stream.Write(lengthBytes);
        stream.Write("pHYs"u8);
        stream.Write(data);

        uint crc = 0xFFFF_FFFFu;
        crc = UpdateCrc(crc, "pHYs"u8);
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
