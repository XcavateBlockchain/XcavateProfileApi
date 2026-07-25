using Substrate.NetApi;
using XcavateProfile.Client;
using XcavateProfileApi.Middleware;
using XcavateProfileApiClient;

namespace XcavateProfileApi.GraphQL.Auth;

/// <summary>
/// Verifies the sr25519 signature on a GraphQL request before Hot Chocolate executes it, reusing the
/// scheme the REST controllers already implement. Requests without the headers pass through as
/// anonymous, because queries are public — it is the field-level
/// <see cref="RequireSignatureAttribute"/> that refuses anonymous mutations.
/// </summary>
public sealed class GraphQLSignatureMiddleware(RequestDelegate next, ILogger<GraphQLSignatureMiddleware> logger)
{
    private const string GraphQLPath = "/graphql";

    public async Task InvokeAsync(HttpContext context, ICallerContext callerContext)
    {
        var caller = (CallerContext)callerContext;

        if (!IsGraphQLPost(context.Request))
        {
            await next(context);
            return;
        }

        var address = context.Request.Headers["X-SS58-Address"].FirstOrDefault();
        var signature = context.Request.Headers["X-Signature"].FirstOrDefault();
        var timestamp = context.Request.Headers["X-Timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(address)
            || string.IsNullOrEmpty(signature)
            || string.IsNullOrEmpty(timestamp))
        {
            caller.Reject(CallerRejection.NoCredentials, null);
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        var body = await ReadBodyAsync(context.Request);

        var validator = context.RequestServices.GetRequiredService<ISignatureValidator>();

        // The REST path signs the hash of the serialized body; here the body is the GraphQL
        // document, so the same payload format applies with /graphql as the path.
        var result = await validator.ValidateAsync(
            address,
            signature,
            timestamp,
            "POST",
            GraphQLPath,
            new RawBody(body));

        if (result.IsValid)
        {
            caller.Authenticate(address, validator.IsAdmin(address));
        }
        else
        {
            var rejection = result.Error?.Contains("Timestamp", StringComparison.OrdinalIgnoreCase) == true
                ? CallerRejection.TimestampOutOfRange
                : CallerRejection.InvalidSignature;

            caller.Reject(rejection, result.Error);
            logger.LogInformation(
                "Rejected GraphQL request from {Address}: {Reason}", address, result.Error);
        }

        await next(context);
    }

    private static bool IsGraphQLPost(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && request.Path.StartsWithSegments(GraphQLPath, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        // Hot Chocolate reads the same stream after us, so it has to start from the beginning.
        request.Body.Position = 0;

        return body;
    }

    /// <summary>
    /// Adapts the raw request body to the payload-hashing seam the REST client and server share, so
    /// the signed string is built by the same <c>CryptoHelper.ConstructPayload</c> call.
    /// </summary>
    private sealed class RawBody(string body) : IPayloadBody
    {
        public string Hash() => Utils.Bytes2HexString(CryptoHelper.Hash(body));
    }
}
