using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace XcavateProfileApi.Controllers;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class IndexerWebhookSecretAttribute : TypeFilterAttribute
{
    public const string ConfigKey = "INDEXER_WEBHOOK_SECRET";

    private const string SecretHeader = "X-Webhook-Secret";
    private const string SecretQueryKey = "secret";

    public IndexerWebhookSecretAttribute()
        : base(typeof(Filter))
    {
    }

    private sealed class Filter(IConfiguration config) : IAsyncAuthorizationFilter
    {
        public Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var secret = config[ConfigKey];
            if (string.IsNullOrWhiteSpace(secret))
            {
                context.Result = new ObjectResult(
                    $"The indexer webhook secret ({ConfigKey}) is not configured — webhooks are disabled.")
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                };
                return Task.CompletedTask;
            }

            var presented = context.HttpContext.Request.Headers[SecretHeader].FirstOrDefault()
                ?? context.HttpContext.Request.Query[SecretQueryKey].FirstOrDefault();
            if (string.IsNullOrEmpty(presented)
                || presented.Length != secret.Length
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(secret)))
            {
                context.Result = new UnauthorizedObjectResult("Missing or wrong webhook secret.");
            }

            return Task.CompletedTask;
        }
    }
}
