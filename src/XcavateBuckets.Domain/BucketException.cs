namespace XcavateBuckets.Domain;

/// <summary>
/// A domain rule was violated. Each instance carries the pallet error it corresponds to, which the
/// API layer surfaces as a GraphQL error <c>code</c> extension.
/// </summary>
public class BucketException(BucketErrorCode code, string message) : Exception(message)
{
    public BucketErrorCode Code { get; } = code;

    public static BucketException NamespaceAlreadyExists() =>
        new(BucketErrorCode.NamespaceAlreadyExists, "The requested namespace already exists.");

    public static BucketException UnknownNamespace() =>
        new(BucketErrorCode.UnknownNamespace, "The requested namespace does not exist.");

    public static BucketException UnknownBucket() =>
        new(BucketErrorCode.UnknownBucket, "The bucket does not exist.");

    public static BucketException BucketIsLocked() =>
        new(BucketErrorCode.BucketIsLocked, "The bucket is locked.");

    public static BucketException UnknownMessage() =>
        new(BucketErrorCode.UnknownMessage, "The requested message does not exist.");

    public static BucketException UnknownTag() =>
        new(BucketErrorCode.UnknownTag, "The given tag does not exist.");

    public static BucketException NotManager() =>
        new(BucketErrorCode.NotManager,
            "The origin is not authorized to perform the manager action for the namespace.");

    public static BucketException NotAdmin() =>
        new(BucketErrorCode.NotAdmin,
            "The origin is not authorized to perform the admin action for the bucket.");

    public static BucketException NotContributor() =>
        new(BucketErrorCode.NotContributor,
            "The contributor does not exist for the requested bucket.");

    public static BucketException DanglingBuckets() =>
        new(BucketErrorCode.DanglingBuckets, "There are dangling buckets for the namespace.");

    public static BucketException DanglingMessages() =>
        new(BucketErrorCode.DanglingMessages, "There are dangling messages for the bucket.");

    public static BucketException DanglingAdmins() =>
        new(BucketErrorCode.DanglingAdmins, "There are dangling admins for the bucket.");

    public static BucketException DanglingContributors() =>
        new(BucketErrorCode.DanglingContributors, "There are dangling contributors for the bucket.");

    public static BucketException DanglingViewers() =>
        new(BucketErrorCode.DanglingViewers, "There are dangling viewers for the bucket.");

    public static BucketException DanglingManagers() =>
        new(BucketErrorCode.DanglingManagers, "There are dangling managers for the namespace.");

    public static BucketException DanglingTags() =>
        new(BucketErrorCode.DanglingTags, "There are dangling tags for the bucket.");

    public static BucketException ArithmeticOverflow() =>
        new(BucketErrorCode.ArithmeticOverflow, "Overflow in arithmetic operation.");

    public static BucketException ArithmeticUnderflow() =>
        new(BucketErrorCode.ArithmeticUnderflow, "Underflow in arithmetic operation.");

    public static BucketException LastManagerRemoval() =>
        new(BucketErrorCode.LastManagerRemoval,
            "Cannot remove the last manager of a namespace.");

    public static BucketException InvalidInput(string detail) =>
        new(BucketErrorCode.InvalidInput, detail);
}
