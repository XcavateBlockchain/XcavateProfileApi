namespace XcavateProfileApi.Models;

/// <summary>
/// The one "Terms document" a lawyer wallet has stored in S3, keyed by the wallet address.
/// A new upload replaces the row — only the current URL is kept.
/// </summary>
/// <remarks>
/// Keyed by the wallet address alone rather than tied to <c>Profile</c>: a lawyer can publish
/// their terms before (or without ever) filling in a profile.
/// </remarks>
public class LawyerTermsDocument
{
    /// <summary>The lawyer's wallet address — the primary key, one document per wallet.</summary>
    public required string Ss58Address { get; set; }

    /// <summary>The public URL the current document is served from.</summary>
    public required string Url { get; set; }

    /// <summary>The file name the document was uploaded with.</summary>
    public required string FileName { get; set; }

    /// <summary>Always <c>application/pdf</c> — the upload endpoint accepts nothing else.</summary>
    public required string ContentType { get; set; }

    /// <summary>When the current document was pinned.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
