using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CharacterConceptAI.Desktop.Services;

public sealed record FilmColorPreset(
    string Key,
    string Label,
    double Exposure,
    double Contrast,
    double Saturation,
    double Warmth,
    double Clarity,
    double FilmGrain,
    double Vignette,
    double ShadowTintR = 0,
    double ShadowTintG = 0,
    double ShadowTintB = 0,
    double HighlightTintR = 0,
    double HighlightTintG = 0,
    double HighlightTintB = 0,
    bool Monochrome = false)
{
    public string LocalizedLabel => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => Key switch
        {
            "original" => "Original",
            "jp_airy" => "Clear Japan",
            "golden_warm" => "Warm Sunset",
            _ => Label
        },
        Infrastructure.Settings.AppLanguage.Zh => Key switch
        {
            "original" => "原图",
            "jp_airy" => "日系通透",
            "golden_warm" => "暖调落日",
            _ => Label
        },
        _ => Label
    };
}

public sealed record ColorGradeSettings(
    string PresetKey = "original",
    double Exposure = 0.0,
    double Contrast = 0.0,
    double Saturation = 0.0,
    double Warmth = 0.0,
    double Clarity = 0.0,
    double FilmGrain = 0.0,
    double Vignette = 0.0);

public sealed class PreparedColorGradeBuffer
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    public PreparedColorGradeBuffer(int width, int height, int stride, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }
}

/// <summary>
/// Ultra-fast 0-GPU local cinema color grading &amp; 35mm film grain processor (&lt;10ms for 1024px images).
/// </summary>
public static class ImageColorGradingService
{
    public static IReadOnlyList<FilmColorPreset> Presets { get; } =
    [
        new("original", "Ảnh gốc", 0, 0, 0, 0, 0, 0, 0),
        new("kodak_portra", "Kodak Portra", 0.04, 0.06, 0.05, 0.14, 0.10, 0.22, 0.16,
            ShadowTintR: 4, ShadowTintG: 2, ShadowTintB: -4, HighlightTintR: 8, HighlightTintG: 4, HighlightTintB: -5),
        new("fuji_chrome", "Fuji Chrome", 0.0, 0.14, -0.08, -0.04, 0.16, 0.20, 0.18,
            ShadowTintR: -5, ShadowTintG: 3, ShadowTintB: 7, HighlightTintR: 4, HighlightTintG: 3, HighlightTintB: -2),
        new("teal_orange", "Teal & Orange", 0.0, 0.16, 0.10, 0.08, 0.18, 0.15, 0.24,
            ShadowTintR: -14, ShadowTintG: 5, ShadowTintB: 16, HighlightTintR: 14, HighlightTintG: 5, HighlightTintB: -10),
        new("jp_airy", "Nhật Trong Trẻo", 0.10, -0.06, -0.10, -0.08, 0.05, 0.10, 0.05,
            ShadowTintR: -4, ShadowTintG: 4, ShadowTintB: 12, HighlightTintR: 4, HighlightTintG: 6, HighlightTintB: 8),
        new("golden_warm", "Hoàng Hôn Ấm", 0.05, 0.08, 0.14, 0.26, 0.10, 0.16, 0.20,
            ShadowTintR: 8, ShadowTintG: 2, ShadowTintB: -8, HighlightTintR: 16, HighlightTintG: 8, HighlightTintB: -12),
        new("leica_bw", "Leica B&W", 0.02, 0.24, -1.0, 0.0, 0.28, 0.30, 0.28, Monochrome: true)
    ];

    public static FilmColorPreset GetPreset(string? key) =>
        Presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    public static PreparedColorGradeBuffer Prepare(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return new PreparedColorGradeBuffer(width, height, stride, pixels);
    }

    public static bool IsIdentity(ColorGradeSettings settings)
    {
        var preset = GetPreset(settings.PresetKey);
        return string.Equals(preset.Key, "original", StringComparison.OrdinalIgnoreCase) &&
               Math.Abs(settings.Exposure) < 0.005 &&
               Math.Abs(settings.Contrast) < 0.005 &&
               Math.Abs(settings.Saturation) < 0.005 &&
               Math.Abs(settings.Warmth) < 0.005 &&
               settings.Clarity < 0.005 &&
               settings.FilmGrain < 0.005 &&
               settings.Vignette < 0.005;
    }

    public static BitmapSource Apply(BitmapSource source, ColorGradeSettings settings)
    {
        if (IsIdentity(settings)) return source;
        return Apply(Prepare(source), settings);
    }

    public static BitmapSource Apply(PreparedColorGradeBuffer buffer, ColorGradeSettings settings)
    {
        var preset = GetPreset(settings.PresetKey);
        var width = buffer.Width;
        var height = buffer.Height;
        var stride = buffer.Stride;
        var src = buffer.Pixels;
        var dst = new byte[src.Length];

        var expMul = (float)Math.Pow(2.0, Math.Clamp(settings.Exposure, -0.6, 0.6));
        var contrastMul = (float)(1.0 + Math.Clamp(settings.Contrast, -0.5, 0.6));
        var satMul = (float)Math.Max(0.0, 1.0 + Math.Clamp(settings.Saturation, -1.0, 0.9));
        var warmth = (float)Math.Clamp(settings.Warmth, -0.6, 0.6);
        var clarity = (float)Math.Clamp(settings.Clarity, 0.0, 1.0);
        var grainAmp = (float)(Math.Clamp(settings.FilmGrain, 0.0, 1.0) * 24.0);
        var vignetteStr = (float)Math.Clamp(settings.Vignette, 0.0, 0.85);
        var mono = preset.Monochrome || satMul <= 0.01f;
        var hasWarmth = MathF.Abs(warmth) > 0.005f;
        var hasClarity = clarity > 0.01f;
        var hasGrain = grainAmp > 0.2f;
        var hasVignette = vignetteStr > 0.01f;
        var hasSat = !mono && MathF.Abs(satMul - 1.0f) > 0.005f;

        var shR = (float)preset.ShadowTintR;
        var shG = (float)preset.ShadowTintG;
        var shB = (float)preset.ShadowTintB;
        var hiR = (float)preset.HighlightTintR;
        var hiG = (float)preset.HighlightTintG;
        var hiB = (float)preset.HighlightTintB;
        var hasSplitTone = (MathF.Abs(shR) + MathF.Abs(shG) + MathF.Abs(shB) + MathF.Abs(hiR) + MathF.Abs(hiG) + MathF.Abs(hiB)) > 0.01f;

        var invHalfW = 1.0f / MathF.Max(1.0f, width * 0.5f);
        var invHalfH = 1.0f / MathF.Max(1.0f, height * 0.5f);
        var halfW = width * 0.5f;
        var halfH = height * 0.5f;
        var clarityAmt = clarity * 0.85f;
        var warmR = warmth * 34.0f;
        var warmG = warmth * 8.0f;
        var warmB = -warmth * 34.0f;
        var vigScale = vignetteStr * 0.42f;

        Parallel.For(0, height, y =>
        {
            var rowOffset = y * stride;
            var ny = (y - halfH) * invHalfH;
            var ny2 = ny * ny;
            var yInner = y > 0 && y < height - 1;
            var yHash = unchecked((uint)(y * 668265263));

            for (int x = 0; x < width; x++)
            {
                var idx = rowOffset + (x << 2);
                float b = src[idx];
                float g = src[idx + 1];
                float r = src[idx + 2];
                var a = src[idx + 3];

                if (hasClarity && yInner && x > 0 && x < width - 1)
                {
                    var up = idx - stride;
                    var dn = idx + stride;
                    var lf = idx - 4;
                    var rt = idx + 4;
                    var avgB = (src[up] + src[dn] + src[lf] + src[rt]) * 0.25f;
                    var avgG = (src[up + 1] + src[dn + 1] + src[lf + 1] + src[rt + 1]) * 0.25f;
                    var avgR = (src[up + 2] + src[dn + 2] + src[lf + 2] + src[rt + 2]) * 0.25f;
                    b += Math.Clamp(b - avgB, -28.0f, 28.0f) * clarityAmt;
                    g += Math.Clamp(g - avgG, -28.0f, 28.0f) * clarityAmt;
                    r += Math.Clamp(r - avgR, -28.0f, 28.0f) * clarityAmt;
                }

                r = ((r * expMul) - 127.5f) * contrastMul + 127.5f;
                g = ((g * expMul) - 127.5f) * contrastMul + 127.5f;
                b = ((b * expMul) - 127.5f) * contrastMul + 127.5f;

                if (hasWarmth)
                {
                    r += warmR;
                    g += warmG;
                    b += warmB;
                }

                float lum = 0f;
                if (hasSplitTone || hasGrain)
                    lum = Math.Clamp((0.299f * r + 0.587f * g + 0.114f * b) * (1.0f / 255.0f), 0.0f, 1.0f);

                if (hasSplitTone)
                {
                    var invL = 1.0f - lum;
                    var shW = invL * invL;
                    var hiW = lum * lum;
                    r += shR * shW + hiR * hiW;
                    g += shG * shW + hiG * hiW;
                    b += shB * shW + hiB * hiW;
                }

                if (mono)
                {
                    var gray = 0.299f * r + 0.587f * g + 0.114f * b;
                    r = g = b = gray;
                }
                else if (hasSat)
                {
                    var gray = 0.299f * r + 0.587f * g + 0.114f * b;
                    r = gray + (r - gray) * satMul;
                    g = gray + (g - gray) * satMul;
                    b = gray + (b - gray) * satMul;
                }

                if (hasGrain)
                {
                    uint h = unchecked((uint)(x * 374761393) + yHash);
                    h = unchecked((h ^ (h >> 13)) * 1274126177u);
                    h ^= h >> 16;
                    var n = ((h & 0xFFFF) * (1.0f / 32767.5f)) - 1.0f;
                    var midWeight = 0.45f + 0.55f * (1.0f - MathF.Abs(lum - 0.5f) * 2.0f);
                    var delta = n * grainAmp * midWeight;
                    r += delta;
                    g += delta;
                    b += delta;
                }

                if (hasVignette)
                {
                    var nx = (x - halfW) * invHalfW;
                    var vig = Math.Clamp(1.0f - (nx * nx + ny2) * vigScale, 0.25f, 1.0f);
                    r *= vig;
                    g *= vig;
                    b *= vig;
                }

                dst[idx] = b <= 0f ? (byte)0 : (b >= 255f ? (byte)255 : (byte)(b + 0.5f));
                dst[idx + 1] = g <= 0f ? (byte)0 : (g >= 255f ? (byte)255 : (byte)(g + 0.5f));
                dst[idx + 2] = r <= 0f ? (byte)0 : (r >= 255f ? (byte)255 : (byte)(r + 0.5f));
                dst[idx + 3] = a;
            }
        });

        var result = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, dst, stride);
        result.Freeze();
        return result;
    }

    public static byte[] EncodePngBytes(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
