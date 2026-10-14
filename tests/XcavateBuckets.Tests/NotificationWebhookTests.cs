using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;
using XcavateProfileApi.Services.Notifications;

namespace XcavateBuckets.Tests;

/// <summary>
/// The two notification webhooks (income-distributed fanout, direct-share-send receiver push)
/// driven against the real controller over SQLite, like <see cref="VotingWebhookTests"/> does
/// for the voting group. These pin the push copy and addressing, the holder fanout's
/// profile-mapping (direct + wallet-migration fallback), the dedupe under at-least-once
/// redelivery, the 502 retry contract when the indexer cannot answer, the best-effort property
/// name, and the shared-secret gate (fail-closed when unconfigured).
/// </summary>
[TestFixture]
public class NotificationWebhookTests
{
    private const string Secret = "test-webhook-secret";

    /// <summary>A genuine 32-byte base58 Solana public key (also used in PushNotificationTests).</summary>
    private const string HolderOne = "DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b";
    private const string HolderTwo = "Ho1derTwo22222222222222222222222222222222222";
    private const string HolderThree = "Ho1derThree333333333333333333333333333333333";
    private const string Receiver = "ReceiverW411et1111111111111111111111111111111111";
    private const string Sender = "SenderW411et111111111111111111111111111111111111";
    private const long AssetId = 12;
    private const string IncomeTx = "1ncomeTxSignature1111111111111111111111111111111111111111111111";
    private const string SendTx = "SendTxSignature222222222222222222222222222222222222222222222222";

    /// <summary>POSTs the raw JSON and returns the status plus the parsed body (default when empty).</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client, string path, string json)
    {
        using var response = await client.PostAsync(
            path, new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();

        JsonElement body;
        try
        {
            body = string.IsNullOrWhiteSpace(text)
                ? default
                : JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            body = default;
        }

        return ((HttpStatusCode)response.StatusCode, body);
    }

    /// <summary>The shared-secret channel the indexer's header-less delivery loop can carry it on.</summary>
    private static string WithSecret(string path) => $"{path}?secret={Secret}";

    private static string IncomeJson(string txSignature = IncomeTx)
    {
        return new JsonObject
        {
            ["event"] = "income_distributed",
            ["asset_id"] = AssetId,
            ["mint"] = "PaymentM1nt1111111111111111111111111111111111111",
            ["amount"] = 1_500_000,
            ["agent"] = "AgentW411et11111111111111111111111111111111111111",
            ["letting"] = "LettingPDA111",
            ["property"] = "AssetPDA111",
            ["income"] = "IncomePDA111",
            ["slot"] = 487_900_000,
            ["tx_signature"] = txSignature,
            ["block_time"] = "2026-10-14T10:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();
    }

    private static string SharesJson(string txSignature = SendTx, string receiver = Receiver)
    {
        return new JsonObject
        {
            ["event"] = "shares_transferred",
            ["asset_id"] = AssetId,
            ["amount"] = 5,
            ["sender"] = Sender,
            ["receiver"] = receiver,
            ["slot"] = 487_950_000,
            ["tx_signature"] = txSignature,
            ["block_time"] = "2026-10-14T11:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
    }

    private static List<PushNotification> Drain(WebhooksHost host)
    {
        var items = new List<PushNotification>();
        while (host.Queue!.Reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    private static async Task<int> RecordedEventsAsync(WebhooksHost host) =>
        await host.WithProfileDbAsync(db => db.ProcessedWebhookEvents.CountAsync());

    // ---- income_distributed -------------------------------------------------------------------

    [Test]
    public async Task Income_distributed_pushes_every_registered_holder()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.ShareHolderWallets = [HolderOne, HolderTwo, HolderThree];
        await host.WithProfileDbAsync(async db =>
        {
            // HolderOne: profile keyed directly by the wallet. HolderTwo: a migrated account —
            // the profile still sits under the legacy SS58 address and the WalletMigrations row
            // maps the Solana wallet to it. HolderThree: never registered.
            db.Profiles.Add(new Profile { Ss58Address = HolderOne, X25519Key = TestDb.Key32 });
            db.Profiles.Add(new Profile { Ss58Address = TestDb.Alice, X25519Key = TestDb.Key32 });
            db.WalletMigrations.Add(new WalletMigration
            {
                Ss58Address = TestDb.Alice,
                SolanaAddress = HolderTwo
            });
            await db.SaveChangesAsync();
            return true;
        });

        var (status, body) = await PostAsync(
            host.Client, WithSecret("/webhooks/income_distributed_devnet"), IncomeJson());

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("processed"));
            Assert.That(body.GetProperty("holders").GetInt32(), Is.EqualTo(3));
            Assert.That(body.GetProperty("pushed").GetInt32(), Is.EqualTo(2));
            Assert.That(body.GetProperty("skippedNoProfile").GetInt32(), Is.EqualTo(1));
        });

        var pushes = Drain(host);
        Assert.Multiple(() =>
        {
            Assert.That(
                pushes.Select(p => (p.Chain, p.Address)),
                Is.EquivalentTo(new[]
                {
                    (PushNotification.SolanaChain, HolderOne),
                    (PushNotification.SolanaChain, HolderTwo)
                }));
            Assert.That(pushes.Select(p => p.Title), Is.All.EqualTo("Rent received"));
            Assert.That(
                pushes.Select(p => p.Body),
                Is.All.EqualTo("Your property paid out rent — open the app to claim it"));
            Assert.That(
                pushes.Select(p => (p.Type, p.BucketId)),
                Is.All.EqualTo((PushNotification.IncomeDistributedType, string.Empty)));
        });
    }

    [Test]
    public async Task Income_distributed_redelivery_is_a_duplicate_and_never_re_sends()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.ShareHolderWallets = [HolderOne];
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = HolderOne, X25519Key = TestDb.Key32 });
            await db.SaveChangesAsync();
            return true;
        });

        var first = await PostAsync(host.Client, WithSecret("/webhooks/income_distributed_devnet"), IncomeJson());
        var second = await PostAsync(host.Client, WithSecret("/webhooks/income_distributed_devnet"), IncomeJson());

        Assert.That(first.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.Body.GetProperty("status").GetString(), Is.EqualTo("duplicate"));
        });
        Assert.That(Drain(host), Has.Count.EqualTo(1), "the redelivery must not re-send");
        Assert.That(await RecordedEventsAsync(host), Is.EqualTo(1));
    }

    [Test]
    public async Task Income_distributed_retries_when_the_holder_resolution_fails()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.ShareHolderWallets = [HolderOne];
        host.Indexer.ThrowOnQuery = true;

        var failed = await PostAsync(host.Client, WithSecret("/webhooks/income_distributed_devnet"), IncomeJson());

        Assert.That(failed.Status, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(
            await RecordedEventsAsync(host), Is.EqualTo(0),
            "an unresolved event records no dedupe row — the outbox's retry must fan out for real");

        host.Indexer.ThrowOnQuery = false;
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = HolderOne, X25519Key = TestDb.Key32 });
            await db.SaveChangesAsync();
            return true;
        });
        var retried = await PostAsync(host.Client, WithSecret("/webhooks/income_distributed_devnet"), IncomeJson());

        Assert.That(retried.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(retried.Body.GetProperty("status").GetString(), Is.EqualTo("processed"));
        Assert.That(Drain(host), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Income_distributed_rejects_an_invalid_payload()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);

        var wrongEvent = IncomeJson().Replace("\"income_distributed\"", "\"something_else\"");
        var (wrongEventStatus, _) = await PostAsync(
            host.Client, WithSecret("/webhooks/income_distributed_devnet"), wrongEvent);

        var missingTx = JsonSerializer.Deserialize<JsonObject>(IncomeJson())!;
        missingTx.Remove("tx_signature");
        var (missingTxStatus, _) = await PostAsync(
            host.Client, WithSecret("/webhooks/income_distributed_devnet"), missingTx.ToJsonString());

        Assert.Multiple(() =>
        {
            Assert.That(wrongEventStatus, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(missingTxStatus, Is.EqualTo(HttpStatusCode.BadRequest));
        });
        Assert.That(await RecordedEventsAsync(host), Is.EqualTo(0));
    }

    // ---- shares_transferred -------------------------------------------------------------------

    [Test]
    public async Task Shares_transferred_pushes_the_receiver_with_the_property_name()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.PropertyAssetName = "42 Main Street";
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = Receiver, X25519Key = TestDb.Key32 });
            await db.SaveChangesAsync();
            return true;
        });

        var (status, body) = await PostAsync(
            host.Client, WithSecret("/webhooks/shares_received_devnet"), SharesJson());

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("processed"));

        var push = Drain(host).Single();
        Assert.Multiple(() =>
        {
            Assert.That((push.Chain, push.Address), Is.EqualTo((PushNotification.SolanaChain, Receiver)));
            Assert.That(push.Title, Is.EqualTo("You received property shares"));
            Assert.That(push.Body, Is.EqualTo("5 shares of 42 Main Street were transferred to your wallet"));
            Assert.That((push.Type, push.BucketId), Is.EqualTo((PushNotification.SharesReceivedType, string.Empty)));
        });
    }

    [Test]
    public async Task Shares_transferred_sends_without_the_name_when_the_indexer_cannot_answer()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.ThrowOnQuery = true; // the name enrichment is best-effort, never a 502
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = Receiver, X25519Key = TestDb.Key32 });
            await db.SaveChangesAsync();
            return true;
        });

        var (status, body) = await PostAsync(
            host.Client, WithSecret("/webhooks/shares_received_devnet"), SharesJson());

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("processed"));
        Assert.That(
            Drain(host).Single().Body,
            Is.EqualTo("5 shares of a property were transferred to your wallet"));
    }

    [Test]
    public async Task Shares_transferred_skips_a_receiver_without_a_profile()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);

        var (status, body) = await PostAsync(
            host.Client, WithSecret("/webhooks/shares_received_devnet"), SharesJson());

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("skipped"));
        Assert.That(Drain(host), Is.Empty, "no profile, no push");
    }

    [Test]
    public async Task Shares_transferred_ignores_a_self_transfer()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);

        var (status, body) = await PostAsync(
            host.Client, WithSecret("/webhooks/shares_received_devnet"), SharesJson(receiver: Sender));

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ignored"));
        Assert.That(Drain(host), Is.Empty);
    }

    [Test]
    public async Task Shares_transferred_redelivery_never_re_sends()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.PropertyAssetName = "42 Main Street";
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = Receiver, X25519Key = TestDb.Key32 });
            await db.SaveChangesAsync();
            return true;
        });

        await PostAsync(host.Client, WithSecret("/webhooks/shares_received_devnet"), SharesJson());
        var second = await PostAsync(host.Client, WithSecret("/webhooks/shares_received_devnet"), SharesJson());

        Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.Body.GetProperty("status").GetString(), Is.EqualTo("duplicate"));
        Assert.That(Drain(host), Has.Count.EqualTo(1), "the redelivery must not re-send");
        Assert.That(await RecordedEventsAsync(host), Is.EqualTo(1));
    }

    [Test]
    public async Task Two_sends_in_one_transaction_are_two_events()
    {
        // The dedupe key carries the event's subject fields, not just the tx signature: a batch
        // transaction sending shares of two assets to the same receiver must notify per send.
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.PropertyAssetName = "42 Main Street";
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = Receiver, X25519Key = TestDb.Key32 });
            await db.SaveChangesAsync();
            return true;
        });

        var otherAsset = JsonSerializer.Deserialize<JsonObject>(SharesJson())!;
        otherAsset["asset_id"] = AssetId + 1;
        await PostAsync(host.Client, WithSecret("/webhooks/shares_received_devnet"), SharesJson());
        var (status, body) = await PostAsync(
            host.Client, WithSecret("/webhooks/shares_received_devnet"), otherAsset.ToJsonString());

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("processed"));
        Assert.That(Drain(host), Has.Count.EqualTo(2));
        Assert.That(await RecordedEventsAsync(host), Is.EqualTo(2));
    }

    // ---- the shared-secret gate ----------------------------------------------------------------

    [Test]
    public async Task The_notification_webhooks_fail_closed_without_a_configured_secret()
    {
        await using var host = await WebhooksHost.StartAsync();

        var (incomeStatus, _) = await PostAsync(host.Client, "/webhooks/income_distributed_devnet", IncomeJson());
        var (sharesStatus, _) = await PostAsync(host.Client, "/webhooks/shares_received_devnet", SharesJson());

        Assert.Multiple(() =>
        {
            Assert.That(incomeStatus, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(sharesStatus, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        });
        Assert.That(await RecordedEventsAsync(host), Is.EqualTo(0));
    }

    [Test]
    public async Task A_missing_or_wrong_secret_is_rejected()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);

        var (missingStatus, _) = await PostAsync(host.Client, "/webhooks/income_distributed_devnet", IncomeJson());
        var (wrongStatus, _) = await PostAsync(
            host.Client, "/webhooks/income_distributed_devnet?secret=not-the-secret", IncomeJson());

        Assert.Multiple(() =>
        {
            Assert.That(missingStatus, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(wrongStatus, Is.EqualTo(HttpStatusCode.Unauthorized));
        });
        Assert.That(await RecordedEventsAsync(host), Is.EqualTo(0));
    }

    [Test]
    public async Task The_secret_is_also_accepted_from_the_header()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret);
        host.Indexer.ShareHolderWallets = [];

        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/income_distributed_devnet");
        request.Headers.Add("X-Webhook-Secret", Secret);
        request.Content = new StringContent(IncomeJson(), Encoding.UTF8, "application/json");
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task The_fanout_accepts_the_event_without_pushing_when_push_is_not_configured()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: Secret, pushConfigured: false);
        host.Indexer.ShareHolderWallets = [HolderOne];

        var (status, body) = await PostAsync(
            host.Client, WithSecret("/webhooks/income_distributed_devnet"), IncomeJson());

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("accepted"));
            Assert.That(body.GetProperty("pushed").GetInt32(), Is.EqualTo(0));
        });
        Assert.That(host.Queue, Is.Null);
        Assert.That(
            await RecordedEventsAsync(host), Is.EqualTo(1),
            "the event is durably accepted — the indexer must not retry what this service cannot act on");
    }
}
