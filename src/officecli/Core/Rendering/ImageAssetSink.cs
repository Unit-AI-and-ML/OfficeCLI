// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace OfficeCli.Core.Rendering;

/// <summary>
/// Writes image parts out as standalone files and returns the URL to reference them by —
/// the mechanism behind <see cref="RenderOptions.AssetDirectory"/>. A picture-heavy
/// document inlines to tens of MiB of base64 (99% of the HTML is image payload) which a
/// downstream viewer with a byte budget refuses outright; externalizing the images leaves
/// only the markup, a few hundred KiB.
/// <para>
/// One sink per render. Null means "inline as before", so a request that does not ask for
/// external assets keeps the previous output byte for byte.
/// </para>
/// </summary>
internal sealed class ImageAssetSink
{
    // 64 bits of SHA-256. The bytes are hashed, so equal content means an equal name; the
    // only way two different images collide is a hash collision, and a collision would
    // just mean one of them is served for both references.
    private const int HashChars = 16;

    private readonly string _directory;
    private readonly string _urlPrefix;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private bool _failed;

    private ImageAssetSink(string directory, string urlPrefix)
    {
        _directory = directory;
        _urlPrefix = urlPrefix;
    }

    /// <summary>
    /// A sink for the request, or null when it did not ask for external assets (or asked
    /// for a blank directory, which means the same thing).
    /// </summary>
    public static ImageAssetSink? For(string? directory, string? urlPrefix)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var dir = Path.TrimEndingDirectorySeparator(directory!);
        return new ImageAssetSink(dir, NormalizePrefix(urlPrefix, dir));
    }

    /// <summary>
    /// Write <paramref name="bytes"/> as a content-addressed file and return the URL to
    /// reference it by, or null when they could not be written — the caller then inlines
    /// the image exactly as it would have without this feature. A read-only or bogus
    /// output directory costs the user the external files, never the whole preview.
    /// </summary>
    public string? TryWrite(byte[] bytes, string contentType)
    {
        if (_failed) return null;

        // Content-addressed: the same image reached through a second relationship (or from
        // a header, footnote, or table cell) resolves to the same name, so it is written
        // once and both references emit the same URL.
        var name = $"img-{Convert.ToHexString(SHA256.HashData(bytes))[..HashChars].ToLowerInvariant()}"
                   + ExtensionFor(contentType);
        try
        {
            if (_written.Add(name))
            {
                Directory.CreateDirectory(_directory);
                File.WriteAllBytes(Path.Combine(_directory, name), bytes);
            }
            return _urlPrefix + name;
        }
        catch
        {
            // Unwritable directory, disk full, or a path that is a file. Stop trying for
            // the rest of this render rather than throwing once per image.
            _failed = true;
            return null;
        }
    }

    /// <summary>
    /// The URL prefix for the files. Defaults to the directory's own name, which is what a
    /// server serving the assets beside the HTML sees; never the filesystem path, since the
    /// HTML is published by a server that mounts the directory wherever it likes. The
    /// result is emitted verbatim into src="...", so the CLI rejects prefixes carrying
    /// characters that would break out of the attribute or the URL.
    /// </summary>
    private static string NormalizePrefix(string? urlPrefix, string directory)
    {
        var prefix = string.IsNullOrWhiteSpace(urlPrefix) ? Path.GetFileName(directory) : urlPrefix!.Trim();
        prefix = prefix.TrimEnd('/');
        return prefix.Length == 0 ? "" : prefix + "/";
    }

    /// <summary>
    /// File extension for the part's content type, so a static server serves the file with
    /// the right media type (it keys off the suffix). Unknown image subtypes keep their own
    /// token — a modern <c>image/avif</c> part should not land on disk as an extensionless
    /// file the server hands out as octet-stream.
    /// </summary>
    private static string ExtensionFor(string contentType)
    {
        switch (contentType.ToLowerInvariant())
        {
            case "image/png": return ".png";
            case "image/jpeg":
            case "image/jpg": return ".jpg";
            case "image/gif": return ".gif";
            case "image/webp": return ".webp";
            case "image/bmp":
            case "image/x-ms-bmp": return ".bmp";
            case "image/svg+xml": return ".svg";
            case "image/tiff":
            case "image/tif":
            case "image/x-tiff": return ".tif";
            case "image/wmf":
            case "image/x-wmf": return ".wmf";
            case "image/emf":
            case "image/x-emf": return ".emf";
            case "image/x-icon":
            case "image/vnd.microsoft.icon": return ".ico";
        }

        // Fallback: the subtype's leading token, minus any "+suffix" or parameters
        // ("image/avif", "image/heic", "image/svg+compressed"). Sanitized to filename-safe
        // characters in case the part declares something exotic.
        var slash = contentType.IndexOf('/');
        if (slash < 0) return "";
        var subtype = contentType[(slash + 1)..];
        var cut = subtype.IndexOfAny(['+', ';']);
        if (cut >= 0) subtype = subtype[..cut];
        var token = new string(subtype.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return token.Length == 0 ? "" : "." + token;
    }
}
