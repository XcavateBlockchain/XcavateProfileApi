using HotChocolate;
using HotChocolate.Execution;
using XcavateBuckets.Domain;
using XcavateProfileApi.GraphQL.Auth;

namespace XcavateProfileApi.GraphQL;

/// <summary>
/// Surfaces domain and auth failures as GraphQL errors carrying a stable <c>code</c> extension, so
/// clients can branch on the pallet's error identity rather than on message text.
/// </summary>
public sealed class BucketErrorFilter : IErrorFilter
{
    public IError OnError(IError error) => error.Exception switch
    {
        BucketException domain => error
            .WithMessage(domain.Message)
            .SetExtension("code", domain.Code.ToErrorCode()),

        BucketApiException api => error
            .WithMessage(api.Message)
            .SetExtension("code", api.Code),

        _ => error
    };
}
