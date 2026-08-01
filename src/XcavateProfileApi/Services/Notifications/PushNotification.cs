namespace XcavateProfileApi.Services.Notifications;

/// <summary>One push to one wallet, in the shape the notifications API accepts.</summary>
public sealed record PushNotification(string Chain, string Address, string Title, string Body)
{
    public const string PolkadotChain = "polkadot";
    public const string SolanaChain = "solana";
}
