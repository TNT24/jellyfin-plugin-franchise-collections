using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Jellyfin.Plugin.FranchiseCollections.Notifications;

/// <summary>An image uploaded with a Discord message, referenced from an embed as <c>attachment://FileName</c>.</summary>
/// <param name="FileName">The file name, e.g. "movies.jpg".</param>
/// <param name="Data">The image.</param>
/// <param name="MimeType">Its type, e.g. "image/jpeg".</param>
public sealed record DiscordAttachment(string FileName, byte[] Data, string MimeType)
{
    /// <summary>Gets how an embed refers to this image.</summary>
    public string Reference => "attachment://" + FileName;
}

/// <summary>
/// Draws the pictures for Discord messages: a row of movie posters, and a small copy of a collection's poster.
/// Kept small so a message stays well under Discord's upload limit.
/// </summary>
public static class DiscordImages
{
    /// <summary>Posters in the row, at most.</summary>
    public const int StripPosters = 4;

    /// <summary>Each poster in the row is this wide (and 1.5 times as high).</summary>
    public const int CellWidth = 300;

    /// <summary>The collection poster shown beside the card.</summary>
    public const int ThumbnailHeight = 450;

    /// <summary>An image is never sent past this (Discord allows 10 MB a message on some servers).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private const int CellHeight = CellWidth * 3 / 2;
    private const int Gap = 6;

    /// <summary>
    /// Up to <see cref="StripPosters"/> posters side by side, each cropped to fill its cell, as a JPEG. Null if none
    /// of them can be read.
    /// </summary>
    /// <param name="posters">The poster files' contents, in order.</param>
    /// <returns>The JPEG, or null.</returns>
    /// <remarks>Kept separate so a missing or mismatched SkiaSharp fails here, inside the caller's try.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[]? PosterStrip(IReadOnlyList<byte[]> posters)
    {
        var bitmaps = new List<SKBitmap>();
        try
        {
            foreach (var poster in posters)
            {
                if (bitmaps.Count == StripPosters)
                {
                    break;
                }

                if (Decode(poster) is { } bitmap)
                {
                    bitmaps.Add(bitmap);
                }
            }

            if (bitmaps.Count == 0)
            {
                return null;
            }

            var width = (bitmaps.Count * CellWidth) + ((bitmaps.Count - 1) * Gap);
            using var surface = SKSurface.Create(new SKImageInfo(width, CellHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(new SKColor(0x10, 0x10, 0x14));
            for (var i = 0; i < bitmaps.Count; i++)
            {
                DrawCropped(surface.Canvas, bitmaps[i], new SKRect(i * (CellWidth + Gap), 0, (i * (CellWidth + Gap)) + CellWidth, CellHeight));
            }

            return Encode(surface);
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    /// <summary>A JPEG of the image no higher than <paramref name="maxHeight"/>, or null if it can't be read.</summary>
    /// <param name="encoded">The image file's contents.</param>
    /// <param name="maxHeight">The most it may be high.</param>
    /// <returns>The JPEG, or null.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[]? Shrink(byte[] encoded, int maxHeight)
    {
        using var source = Decode(encoded);
        if (source is null)
        {
            return null;
        }

        var scale = Math.Min(1.0, (double)maxHeight / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        DrawCropped(surface.Canvas, source, new SKRect(0, 0, width, height));
        return Encode(surface);
    }

    private static SKBitmap? Decode(byte[] encoded)
    {
        // SKBitmap.Decode throws rather than returning null when the bytes aren't an image it knows.
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            bitmap?.Dispose();
            return null;
        }

        return bitmap;
    }

    /// <summary>Draws the image filling <paramref name="cell"/>, cutting off whatever sticks out evenly on both sides.</summary>
    private static void DrawCropped(SKCanvas canvas, SKBitmap bitmap, SKRect cell)
    {
        var scale = Math.Max(cell.Width / bitmap.Width, cell.Height / bitmap.Height);
        var cropWidth = cell.Width / scale;
        var cropHeight = cell.Height / scale;
        var crop = SKRect.Create((bitmap.Width - cropWidth) / 2, (bitmap.Height - cropHeight) / 2, cropWidth, cropHeight);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, crop, cell, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static byte[]? Encode(SKSurface surface)
    {
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        var bytes = data?.ToArray();
        return bytes is { Length: > 0 and <= MaxBytes } ? bytes : null;
    }
}
