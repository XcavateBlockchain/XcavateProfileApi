using System.Text.Json.Serialization;

namespace XcavateProfileApi.Models;

/// <summary>
/// The current "Terms document" of a lawyer wallet: its IPFS CID, the gateway URL it is served
/// from, the uploaded file name and when it was last replaced.
/// </summary>
public class TermsDocumentResponse
{
    /// <summary>The IPFS content identifier the document is pinned under.</summary>
    [JsonPropertyName("cid")]
    public string Cid { get; set; } = "";

    /// <summary>The public gateway URL the CID is served from.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>The file name the document was uploaded with.</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    /// <summary>When the current document was pinned.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}
