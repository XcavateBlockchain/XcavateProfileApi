using HotChocolate.Types.Descriptors;
using System.Reflection;

namespace XcavateProfileApi.GraphQL.Auth;

/// <summary>
/// Refuses the field unless the request carried a valid sr25519 signature. Applied to the 15
/// role-based mutations; queries deliberately carry nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class RequireSignatureAttribute : ObjectFieldDescriptorAttribute
{
    protected override void OnConfigure(
        IDescriptorContext context, IObjectFieldDescriptor descriptor, MemberInfo? member)
    {
        descriptor.Use(next => async middlewareContext =>
        {
            // Throws with the specific reason: UNAUTHORIZED, INVALID_SIGNATURE or
            // TIMESTAMP_OUT_OF_RANGE.
            middlewareContext.Service<ICallerContext>().RequireAddress();
            await next(middlewareContext);
        });
    }
}

/// <summary>
/// Refuses the field unless the caller is a configured admin address. Stands in for the pallet's
/// <c>ForceOriginCheck</c> on the five <c>force*</c> mutations.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class RequireAdminAttribute : ObjectFieldDescriptorAttribute
{
    protected override void OnConfigure(
        IDescriptorContext context, IObjectFieldDescriptor descriptor, MemberInfo? member)
    {
        descriptor.Use(next => async middlewareContext =>
        {
            var caller = middlewareContext.Service<ICallerContext>();
            var address = caller.RequireAddress();

            if (!caller.IsAdmin)
            {
                throw new BucketApiException(
                    "FORBIDDEN",
                    $"'{address}' is not authorized to perform force operations.");
            }

            await next(middlewareContext);
        });
    }
}
