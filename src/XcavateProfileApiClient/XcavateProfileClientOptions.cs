namespace XcavateProfile.Client;

/// <summary>Configuration for <see cref="XcavateProfileClient"/>.</summary>
/// <remarks>
/// There is deliberately no timestamp-skew setting here. The tolerance is the server's
/// (<c>SignatureValidationOptions.TimestampSkew</c>, five minutes) and a client cannot widen it;
/// the property that used to sit here was never read by anything.
/// </remarks>
public class XcavateProfileClientOptions
{
    /// <summary>
    /// Base URL of the API, for example <c>https://profile-api.xcavate.io</c>. A trailing slash is
    /// added if missing.
    /// </summary>
    public required string ApiUrl { get; set; }
}
