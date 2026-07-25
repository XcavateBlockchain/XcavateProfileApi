using System.Text;

namespace XcavateBuckets.Domain;

/// <summary>
/// Ports the pallet's <c>Error</c> enum. <c>UnableToPayFees</c> is deliberately absent: there is no
/// currency off-chain, so no call can ever raise it.
/// </summary>
public enum BucketErrorCode
{
    NamespaceAlreadyExists,
    UnknownNamespace,
    UnknownBucket,
    BucketIsLocked,
    UnknownMessage,
    UnknownTag,
    NotManager,
    NotAdmin,
    NotContributor,
    DanglingBuckets,
    DanglingMessages,
    DanglingAdmins,
    DanglingContributors,
    DanglingViewers,
    DanglingManagers,
    DanglingTags,
    ArithmeticOverflow,
    ArithmeticUnderflow,
    LastManagerRemoval,

    /// <summary>Not from the pallet: a bound or format check on API input failed.</summary>
    InvalidInput
}

public static class BucketErrorCodeExtensions
{
    /// <summary>
    /// Converts a code to the stable SCREAMING_SNAKE string clients see in
    /// <c>errors[].extensions.code</c>.
    /// </summary>
    public static string ToErrorCode(this BucketErrorCode code)
    {
        var name = code.ToString();
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
