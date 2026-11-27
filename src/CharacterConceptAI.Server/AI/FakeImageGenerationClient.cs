using System.IO.Compression;

namespace CharacterConceptAI.Server.AI;

public class FakeImageGenerationClient : IImageGenerationClient
{
    public bool IsSimulated => true;

    public async Task<GenerationExecutionResult> GenerateAsync(GenerationExecutionRequest request, CancellationToken ct = default)
    {
        // Simulate short generation time (300ms)
        await Task.Delay(300, ct);

        // Generate a deterministic valid PNG in memory
        var width = request.FullQuality ? request.Width * 2 : request.Width;
        var height = request.FullQuality ? request.Height * 2 : request.Height;
        var pngBytes = GenerateSyntheticPng(width, height, request.Seed, request.ReferenceImageBytes, request.Denoise);

        var output = new GeneratedImageOutput(
            ImageBytes: pngBytes,
            Extension: "png",
            Width: width,
            Height: height
        );

        return new GenerationExecutionResult(
            Success: true,
            Outputs: [output],
            ProviderPromptId: $"simulated_{Guid.NewGuid():N}",
            ErrorMessage: null,
            Elapsed: TimeSpan.FromMilliseconds(300)
        );
    }

    private static byte[] GenerateSyntheticPng(int width, int height, long seed, byte[]? refBytes = null, double? denoise = null)
    {
        // Minimal uncompressed RGB PNG generator with deterministic color pattern based on seed
        using var ms = new MemoryStream();
        // PNG Signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR
        using (var ihdr = new MemoryStream())
        {
            ihdr.Write(BigEndian(width));
            ihdr.Write(BigEndian(height));
            ihdr.WriteByte(8); // bit depth
            ihdr.WriteByte(2); // color type (RGB)
            ihdr.WriteByte(0); // compression
            ihdr.WriteByte(0); // filter
            ihdr.WriteByte(0); // interlace
            WriteChunk(ms, "IHDR", ihdr.ToArray());
        }

        // IDAT
        var rawData = new byte[height * (1 + width * 3)];
        byte r = (byte)((seed * 37) % 200 + 40);
        byte g = (byte)((seed * 67) % 200 + 40);
        byte b = (byte)((seed * 97) % 200 + 40);

        if (refBytes != null && refBytes.Length > 0)
        {
            var factor = denoise ?? 0.6;
            r = (byte)((r * factor + (refBytes[0] % 120 + 20) * (1 - factor)) % 255);
            g = (byte)((g * factor + (refBytes[^1] % 120 + 20) * (1 - factor)) % 255);
        }

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (1 + width * 3);
            rawData[rowStart] = 0; // Filter type None
            for (int x = 0; x < width; x++)
            {
                int pixelIndex = rowStart + 1 + x * 3;
                // create a subtle grid pattern
                bool isBorder = (x < 4 || x >= width - 4 || y < 4 || y >= height - 4);
                rawData[pixelIndex] = isBorder ? (byte)255 : r;
                rawData[pixelIndex + 1] = isBorder ? (byte)255 : g;
                rawData[pixelIndex + 2] = isBorder ? (byte)255 : b;
            }
        }

        using (var compressedStream = new MemoryStream())
        {
            using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Fastest, true))
            {
                zlibStream.Write(rawData);
            }
            WriteChunk(ms, "IDAT", compressedStream.ToArray());
        }

        // IEND
        WriteChunk(ms, "IEND", []);

        return ms.ToArray();
    }

    private static void WriteChunk(Stream dest, string type, byte[] data)
    {
        dest.Write(BigEndian(data.Length));
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        dest.Write(typeBytes);
        dest.Write(data);

        // CRC32
        uint crc = Crc32(typeBytes, data);
        dest.Write(BigEndian((int)crc));
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        void Update(byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
            }
        }
        foreach (var b in type) Update(b);
        foreach (var b in data) Update(b);
        return ~crc;
    }
}
