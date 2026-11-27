using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace CharacterConceptAI.Server.Storage;

public class LocalMediaStore : IMediaStore
{
    private static readonly Regex StorageKeyRegex = new(
        @"\A[a-f0-9]{32}/[0-9]{6}/[a-f0-9]{32}\.(png|jpg|jpeg)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _rootPath;
    private readonly long _maxBytes;
    private readonly long _maxPixels;

    public LocalMediaStore(IConfiguration configuration)
    {
        var configured = configuration["Media:RootPath"];
        var resolved = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "MediaStorage")
            : (Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured));
        _rootPath = Path.GetFullPath(resolved);
        _maxBytes = configuration.GetValue<long>("Media:MaxImageBytes", 25_000_000);
        _maxPixels = configuration.GetValue<long>("Media:MaxImagePixels", 16_777_216);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<StoredMediaResult> SaveAsync(Guid projectId, Stream content, string extension, CancellationToken ct = default)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        if (ext is not ("png" or "jpg" or "jpeg")) throw new InvalidDataException("Only PNG and JPEG images are allowed.");
        var relativeDir = Path.Combine(projectId.ToString("N"), DateTime.UtcNow.ToString("yyyyMM"));
        var targetDir = Path.Combine(_rootPath, relativeDir);
        Directory.CreateDirectory(targetDir);

        var fileId = Guid.NewGuid().ToString("N");
        var safeKey = $"{relativeDir.Replace('\\', '/')}/{fileId}.{ext}";
        var fullPath = Path.Combine(_rootPath, safeKey.Replace('/', Path.DirectorySeparatorChar));
        var tempPath = Path.Combine(_rootPath, $"temp_{Guid.NewGuid():N}.tmp");

        long size = 0;
        string hash;
        var pngHeader = new byte[24];
        int pngHeaderCaptured = 0;
        try
        {
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    if (size + read > _maxBytes) throw new InvalidDataException("Image exceeds the configured byte limit.");
                    if (ext == "png" && pngHeaderCaptured < 24)
                    {
                        var toCopy = Math.Min(24 - pngHeaderCaptured, read);
                        Buffer.BlockCopy(buffer, 0, pngHeader, pngHeaderCaptured, toCopy);
                        pngHeaderCaptured += toCopy;
                    }
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    size += read;
                }
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            int width, height;
            if (ext == "png")
            {
                // Parsed directly from captured stream header — zero redundant disk re-open!
                if (pngHeaderCaptured < 24 ||
                    !pngHeader.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                    !pngHeader.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                    throw new InvalidDataException("Invalid PNG signature.");
                width = BinaryPrimitives.ReadInt32BigEndian(pngHeader.AsSpan(16, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(pngHeader.AsSpan(20, 4));
            }
            else
            {
                await using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                (width, height) = ReadJpegDimensions(fs);
            }

            if (width <= 0 || height <= 0 || (long)width * height > _maxPixels)
                throw new InvalidDataException("Image dimensions exceed the configured pixel limit.");
            File.Move(tempPath, fullPath);

            return new StoredMediaResult(
                StorageKey: safeKey,
                SizeBytes: size,
                Sha256: hash,
                Width: width,
                Height: height,
                ContentType: ext == "png" ? "image/png" : "image/jpeg"
            );
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("Truncated or malformed image header.");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        if (!IsStorageKey(storageKey)) return Task.FromResult<Stream?>(null);
        var fullPath = GetFullPath(storageKey);
        try
        {
            Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string storageKey)
    {
        if (!IsStorageKey(storageKey)) return Task.CompletedTask;
        try
        {
            var fullPath = GetFullPath(storageKey);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch
        {
            // Best-effort cleanup
        }
        return Task.CompletedTask;
    }

    public string GetFullPath(string storageKey)
    {
        if (!IsStorageKey(storageKey)) throw new ArgumentException("Invalid server-issued storage key.", nameof(storageKey));
        var path = Path.GetFullPath(Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(_rootPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Storage key escapes the media root.", nameof(storageKey));
        return path;
    }

    private static bool IsStorageKey(string key) => !string.IsNullOrWhiteSpace(key) && StorageKeyRegex.IsMatch(key);

    private static (int Width, int Height) ReadJpegDimensions(Stream stream)
    {
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8) throw new InvalidDataException("Invalid JPEG signature.");
        while (stream.Position < stream.Length)
        {
            int b = stream.ReadByte();
            if (b != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker < 0 || marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            int s1 = stream.ReadByte(), s2 = stream.ReadByte();
            if (s1 < 0 || s2 < 0) break;
            int size = (s1 << 8) | s2;
            if (size < 2 || stream.Position + size - 2 > stream.Length) break;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (size < 8) break;
                if (stream.ReadByte() < 0) break;
                int h1 = stream.ReadByte(), h2 = stream.ReadByte();
                int w1 = stream.ReadByte(), w2 = stream.ReadByte();
                if (h1 < 0 || h2 < 0 || w1 < 0 || w2 < 0) break;
                return ((w1 << 8) | w2, (h1 << 8) | h2);
            }
            stream.Seek(size - 2, SeekOrigin.Current);
        }
        throw new InvalidDataException("JPEG dimensions were not found.");
    }
}
