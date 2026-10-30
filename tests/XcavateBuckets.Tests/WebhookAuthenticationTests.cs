using System.Net;
using System.Text;

namespace XcavateBuckets.Tests;

[TestFixture]
public class WebhookAuthenticationTests
{
    private const string Secret = "route-wide-webhook-test-secret";

    private static IEnumerable<string> WebhookRoutes()
    {
        yield return "/webhooks/init_property_asset_devnet";
        yield return "/webhooks/offers_namespace_created_devnet";
        yield return "/webhooks/property_claim_started_devnet";
        yield return "/webhooks/spv_lawyer_elected_devnet";
        yield return "/webhooks/deal_executed_devnet";
        yield return "/webhooks/letting_agent_appointed_devnet";
        yield return "/webhooks/shares_transferred_devnet";
        yield return "/webhooks/property_holder_changed_devnet";
        yield return "/webhooks/spv_case_claimed_devnet";
        yield return "/webhooks/agent_election_opened_devnet";
        yield return "/webhooks/proposal_created_devnet";
        yield return "/webhooks/proposal_finalized_devnet";
        yield return "/webhooks/challenge_created_devnet";
        yield return "/webhooks/challenge_finalized_devnet";
        yield return "/webhooks/income_distributed_devnet";
        yield return "/webhooks/shares_received_devnet";
    }

    [TestCaseSource(nameof(WebhookRoutes))]
    public async Task Every_webhook_rejects_missing_and_wrong_secrets(string route)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);

        using var missing = await PostEmptyObjectAsync(host.Client, route);
        using var wrong = await PostEmptyObjectAsync(host.Client, $"{route}?secret=wrong-secret");

        Assert.Multiple(() =>
        {
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    [TestCaseSource(nameof(WebhookRoutes))]
    public async Task Every_webhook_fails_closed_without_a_configured_secret(string route)
    {
        await using var host = await WebhooksHost.StartAsync();

        using var response = await PostEmptyObjectAsync(host.Client, $"{route}?secret={Secret}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [TestCaseSource(nameof(WebhookRoutes))]
    public async Task Every_webhook_authenticates_before_json_model_binding(string route)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);

        using var malformed = await PostAsync(host.Client, route, "{", "application/json");
        using var unsupported = await PostAsync(host.Client, route, "not JSON", "text/plain");

        Assert.Multiple(() =>
        {
            Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(unsupported.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    [TestCaseSource(nameof(WebhookRoutes))]
    public async Task Every_webhook_fails_closed_before_json_model_binding(string route)
    {
        await using var host = await WebhooksHost.StartAsync();

        using var response = await PostAsync(
            host.Client, $"{route}?secret={Secret}", "{", "application/json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [TestCaseSource(nameof(WebhookRoutes))]
    public async Task Every_webhook_accepts_the_secret_header_before_validating_the_payload(string route)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Webhook-Secret", Secret);

        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static Task<HttpResponseMessage> PostEmptyObjectAsync(HttpClient client, string route) =>
        client.PostAsync(route, new StringContent("{}", Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string route, string body, string mediaType) =>
        client.PostAsync(route, new StringContent(body, Encoding.UTF8, mediaType));
}
