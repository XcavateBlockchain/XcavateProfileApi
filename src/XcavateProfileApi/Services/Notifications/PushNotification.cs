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

    /// <summary>
    /// The type for messages carrying a <c>voting/…</c> tag (the webhook-posted governance
    /// announcements), so the mobile app deep-links to the property's vote rather than the chat.
    /// </summary>
    public const string VotingType = "voting";

    /// <summary>
    /// The type for the "rent received" push the income-distributed webhook fans out to a
    /// property's holders. No bucket is attached — the app just opens.
    /// </summary>
    public const string IncomeDistributedType = "income_distributed";

    /// <summary>
    /// The type for the "you received shares" push the shares-transferred webhook sends to the
    /// receiver of a direct share send. No bucket is attached — the app just opens.
    /// </summary>
    public const string SharesReceivedType = "shares_received";
}
