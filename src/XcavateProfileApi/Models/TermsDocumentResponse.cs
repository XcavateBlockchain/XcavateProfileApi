using System.Text.Json.Serialization;

namespace XcavateProfileApi.Models;

/// <summary>
/// The current "Terms document" of a lawyer wallet: the public URL it is served from, the
/// uploaded file name and when it was last replaced.
/// </summary>
public class TermsDocumentResponse
{
    /// <summary>The public URL the document is served from.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>The file name the document was uploaded with.</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    /// <summary>When the current document was uploaded.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}
