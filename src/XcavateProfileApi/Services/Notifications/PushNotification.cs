namespace XcavateProfileApi.Services.Notifications;

/// <summary>
/// One push to one wallet, in the shape the notifications API accepts. <paramref name="Type"/>
/// and <paramref name="BucketId"/> travel in the FCM data payload so the mobile app can deep-link
/// to the bucket when the notification is tapped; FCM requires data values to be strings.
/// </summary>
public sealed record PushNotification(
    string Chain, string Address, string Title, string Body, string Type, string BucketId)
{
    public const string PolkadotChain = "polkadot";
    public const string SolanaChain = "solana";

    public const string MessageType = "bucket_message";
    public const string MemberAddedType = "member_added";
}
