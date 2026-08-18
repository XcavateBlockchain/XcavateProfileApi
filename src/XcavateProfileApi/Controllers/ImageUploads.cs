namespace XcavateProfileApi.Controllers;

/// <summary>
/// The image allow-list shared by the profile-picture and company-logo endpoints.
/// </summary>
/// <remarks>
/// The bucket serves objects publicly, so the stored content type must never come from the client —
/// an attacker who could store <c>text/html</c> would have stored XSS on the platform's own origin.
/// It is derived from the file extension against this list instead.
/// <para>
/// <c>.svg</c> is deliberately absent: an SVG can embed scripts, which is the same attack by another
/// route. Adding it here re-opens it for every endpoint at once.
/// </para>
/// </remarks>
internal static class ImageUploads
{
    /// <summary>25 MB of image, plus a megabyte for multipart encoding overhead.</summary>
    public const int RequestSizeLimit = 26 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };

    public static bool TryGetContentType(string extension, out string contentType) =>
        AllowedTypes.TryGetValue(extension, out contentType!);

    /// <summary>The refusal message for an extension outside the list.</summary>
    public static string UnsupportedTypeMessage =>
        "Unsupported image type. Allowed: " + string.Join(", ", AllowedTypes.Keys);
}
