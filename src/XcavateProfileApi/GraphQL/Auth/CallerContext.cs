using XcavateBuckets.Domain;

namespace XcavateProfileApi.GraphQL.Auth;

/// <summary>Why signature verification did not produce an authenticated caller.</summary>
public enum CallerRejection
{
    /// <summary>No X-* headers were supplied at all — an ordinary anonymous read.</summary>
    NoCredentials,
    InvalidSignature,
    TimestampOutOfRange
}

/// <summary>
/// The authenticated caller for the current request, populated by
/// <see cref="GraphQLSignatureMiddleware"/> before Hot Chocolate executes.
/// </summary>
public interface ICallerContext
{
    string? Address { get; }

    bool IsAdmin { get; }

    bool IsAuthenticated { get; }

    CallerRejection Rejection { get; }

    string? RejectionDetail { get; }

    /// <summary>Returns the caller's address, or throws the reason it is missing.</summary>
    string RequireAddress();
}

public sealed class CallerContext : ICallerContext
{
    public string? Address { get; private set; }

    public bool IsAdmin { get; private set; }

    public bool IsAuthenticated => Address is not null;

    public CallerRejection Rejection { get; private set; } = CallerRejection.NoCredentials;

    public string? RejectionDetail { get; private set; }

    public void Authenticate(string address, bool isAdmin)
    {
        Address = address;
        IsAdmin = isAdmin;
        RejectionDetail = null;
    }

    public void Reject(CallerRejection rejection, string? detail)
    {
        Address = null;
        IsAdmin = false;
        Rejection = rejection;
        RejectionDetail = detail;
    }

    public string RequireAddress()
    {
        if (Address is not null)
        {
            return Address;
        }

        throw Rejection switch
        {
            CallerRejection.InvalidSignature => new BucketApiException(
                "INVALID_SIGNATURE", RejectionDetail ?? "Signature verification failed."),
            CallerRejection.TimestampOutOfRange => new BucketApiException(
                "TIMESTAMP_OUT_OF_RANGE", RejectionDetail ?? "Timestamp is outside the allowed window."),
            _ => new BucketApiException(
                "UNAUTHORIZED",
                "This operation requires the X-SS58-Address, X-Signature and X-Timestamp headers.")
        };
    }
}

/// <summary>
/// An API-layer failure that is not a pallet rule violation, so it carries its own code rather than
/// a <see cref="BucketErrorCode"/>.
/// </summary>
public sealed class BucketApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
