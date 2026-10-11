using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi.Model.Types;
using XcavateBuckets.Domain.Data;
using XcavateProfile.Client;
using static Substrate.NetApi.Mnemonic;

namespace XcavateBuckets.Tests;

/// <summary>
/// The composed realXmarket secondary-market mutations — <c>createOffersNamespace</c> and
/// <c>setupOfferBucket</c> — driven through the shipped pipeline: signature verification, the
/// open-create authorization relaxation, the single-transaction guarantee and error-code mapping.
/// </summary>
[TestFixture]
public class OfferBucketSetupTests
{
    private const string Key32 = TestDb.Key32;
    private const string OtherKey32 = TestDb.OtherKey32;
    private const string ViewerKey32 = TestDb.ViewerKey32;
    private const string Hash32 = TestDb.Hash32;
    private const string OtherHash32 = "0x5555555555555555555555555555555555555555555555555555555555555555";

    private static Account NewAccount(byte entropyFill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(entropyFill, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "OfferTests" }, KeyType.Sr25519)
            .Account;
    }

    private static Account Buyer() => NewAccount(0x31);

    private static Account OtherBuyer() => NewAccount(0x32);

    private static string SetupMutation(
        string seller,
        string propertyName = "Villa Sol",
        string assetId = "42",
        string shareListingId = "7",
        string cluster = "devnet",
        string? buyerViewerKey = null,
        string? sellerViewerKey = null,
        string offerContentHash = OtherHash32)
    {
        var buyerKeyArg = buyerViewerKey is null ? "" : $"""buyerViewerKey: "{buyerViewerKey}" """;
        var sellerKeyArg = sellerViewerKey is null ? "" : $"""sellerViewerKey: "{sellerViewerKey}" """;

        return $$"""
            mutation {
              setupOfferBucket(input: {
                propertyName: "{{propertyName}}"
                assetId: "{{assetId}}"
                shareListingId: "{{shareListingId}}"
                cluster: "{{cluster}}"
                seller: "{{seller}}"
                bucketEncryptionKey: "{{Key32}}"
                {{buyerKeyArg}}
                {{sellerKeyArg}}
                keySharingMessage: {
                  reference: "key-sharing:seed-{{shareListingId}}"
                  ipfsContent: "{\"type\":\"key-sharing\"}"
                  metadata: {
                    description: "key sharing"
                    contentType: "application/json"
                    contentHash: "{{Hash32}}"
                  }
                }
                offerMessage: {
                  reference: "offer:{{shareListingId}}:buyer"
                  ipfsContent: "{\"price\":1000}"
                  metadata: {
                    description: "the offer"
                    contentType: "application/json"
                    contentHash: "{{offerContentHash}}"
                  }
                }
              }) { namespaceId bucketId offerMessageId }
            }
            """;
    }

    private static async Task SeedProfileWithKeyAsync(GraphQLHost host, string wallet, string x25519Key) =>
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = wallet, X25519Key = x25519Key });
            await db.SaveChangesAsync();
            return true;
        });

    [Test]
    public async Task SetupOfferBucket_creates_the_whole_offer_flow_in_one_signature()
    {
        var buyer = Buyer();
        const string seller = TestDb.Bob;
        await using var host = await GraphQLHost.StartAsync();
        await SeedProfileWithKeyAsync(host, buyer.Value, ViewerKey32);
        await SeedProfileWithKeyAsync(host, seller, OtherKey32);

        var result = await host.SignedAsync(SetupMutation(seller), buyer);

        Assert.That(result.FirstErrorCode(), Is.Null, result.RootElement.ToString());
        var payload = result.Data("setupOfferBucket");
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("namespaceId").GetString(), Is.EqualTo("1"),
                "BigInt is a string on the wire");
            Assert.That(payload.GetProperty("bucketId").GetString(), Is.EqualTo("1"));
            Assert.That(payload.GetProperty("offerMessageId").GetString(), Is.EqualTo("1"),
                "the key-sharing message takes id 0, the offer id 1");
        });

        var state = await host.WithDbAsync(async db => new
        {
            Namespace = await db.Namespaces.SingleAsync(),
            Managers = await db.NamespaceManagers.Select(m => m.Manager).ToListAsync(),
            Bucket = await db.Buckets.SingleAsync(),
            Admins = await db.BucketAdmins.Select(a => a.SubjectId).ToListAsync(),
            Contributors = await db.BucketContributors.Select(c => c.SubjectId).ToListAsync(),
            Viewers = await db.BucketViewers.Select(v => v.ViewerId).ToListAsync(),
            Messages = await db.Messages.OrderBy(m => m.MessageId)
                .Select(m => new { m.MessageId, m.Tag, m.ContentType, m.Contributor, m.IpfsContent })
                .ToListAsync()
        });

        Assert.Multiple(() =>
        {
            Assert.That(state.Namespace.Name, Is.EqualTo("Villa Sol offers"));
            Assert.That(state.Namespace.Category, Is.EqualTo("realXmarket-offers"));
            Assert.That(state.Namespace.Cluster, Is.EqualTo("devnet"));
            Assert.That(state.Namespace.Properties, Is.EqualTo("""{"assetId":"42"}"""));
            Assert.That(state.Namespace.PropertyId, Is.Null, "admin-only field, never set");
            Assert.That(state.Managers, Is.EqualTo(new[] { buyer.Value }),
                "the first signer becomes the namespace's manager");

            Assert.That(state.Bucket.Name, Is.EqualTo("Offer: Villa Sol #7"));
            Assert.That(state.Bucket.Category, Is.EqualTo("realXmarket"));
            Assert.That(state.Bucket.IsWritable, Is.True, "ResumeWriting unlocked it");
            Assert.That(state.Bucket.EncryptionKey, Is.EqualTo(Key32));
            using (var properties = JsonDocument.Parse(state.Bucket.Properties!))
            {
                var root = properties.RootElement;
                Assert.That(root.GetProperty("assetId").GetString(), Is.EqualTo("42"));
                Assert.That(root.GetProperty("shareListingId").GetString(), Is.EqualTo("7"));
                Assert.That(root.GetProperty("buyer").GetString(), Is.EqualTo(buyer.Value));
                Assert.That(root.GetProperty("seller").GetString(), Is.EqualTo(seller));
            }

            Assert.That(state.Admins, Is.EquivalentTo(new[] { buyer.Value, seller }));
            Assert.That(state.Contributors, Is.EquivalentTo(new[] { buyer.Value, seller }));
            Assert.That(state.Viewers, Is.EquivalentTo(new[] { ViewerKey32, OtherKey32 }),
                "both parties' profile X25519 keys are viewers");

            Assert.That(state.Messages, Has.Count.EqualTo(2));
            Assert.That(state.Messages[0].Tag, Is.EqualTo("didcomm/key-sharing-v1"));
            Assert.That(state.Messages[0].ContentType, Is.EqualTo("application/json"));
            Assert.That(state.Messages[0].Contributor, Is.EqualTo(buyer.Value));
            Assert.That(state.Messages[1].Tag, Is.EqualTo("realXmarket/offer"));
            Assert.That(state.Messages[1].IpfsContent, Is.EqualTo("""{"price":1000}"""));
            Assert.That(state.Messages[1].Contributor, Is.EqualTo(buyer.Value));
        });
    }

    [Test]
    public async Task SetupOfferBucket_reuses_the_offers_namespace_for_a_second_buyer()
    {
        var buyer = Buyer();
        var otherBuyer = OtherBuyer();
        const string seller = TestDb.Bob;
        await using var host = await GraphQLHost.StartAsync();

        var first = await host.SignedAsync(
            SetupMutation(seller, buyerViewerKey: ViewerKey32, sellerViewerKey: OtherKey32), buyer);
        Assert.That(first.FirstErrorCode(), Is.Null, first.RootElement.ToString());

        var second = await host.SignedAsync(
            SetupMutation(seller, shareListingId: "9",
                buyerViewerKey: ViewerKey32, sellerViewerKey: OtherKey32),
            otherBuyer);
        Assert.That(second.FirstErrorCode(), Is.Null, second.RootElement.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(second.Data("setupOfferBucket").GetProperty("namespaceId").GetString(),
                Is.EqualTo(first.Data("setupOfferBucket").GetProperty("namespaceId").GetString()),
                "same assetId + cluster resolves to the same offers namespace");
            Assert.That(second.Data("setupOfferBucket").GetProperty("bucketId").GetString(),
                Is.Not.EqualTo(first.Data("setupOfferBucket").GetProperty("bucketId").GetString()),
                "each offer gets its own bucket");
        });

        var counts = await host.WithDbAsync(async db => new
        {
            Namespaces = await db.Namespaces.CountAsync(),
            Buckets = await db.Buckets.CountAsync(),
            Managers = await db.NamespaceManagers.CountAsync()
        });
        Assert.Multiple(() =>
        {
            Assert.That(counts.Namespaces, Is.EqualTo(1));
            Assert.That(counts.Buckets, Is.EqualTo(2));
            Assert.That(counts.Managers, Is.EqualTo(1),
                "the second buyer never becomes a namespace manager");
        });
    }

    [Test]
    public async Task SetupOfferBucket_rolls_everything_back_when_a_late_step_fails()
    {
        var buyer = Buyer();
        const string seller = TestDb.Bob;
        await using var host = await GraphQLHost.StartAsync();

        // The offer message's content hash is not 32-byte hex: validation fails on the SECOND
        // write, after the namespace, bucket, memberships, unlock and first message all applied.
        var result = await host.SignedAsync(
            SetupMutation(seller, buyerViewerKey: ViewerKey32, sellerViewerKey: OtherKey32,
                offerContentHash: "not-hex"),
            buyer);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("INVALID_INPUT"),
            result.RootElement.ToString());

        var counts = await host.WithDbAsync(async db => new
        {
            Namespaces = await db.Namespaces.CountAsync(),
            Managers = await db.NamespaceManagers.CountAsync(),
            Buckets = await db.Buckets.CountAsync(),
            Admins = await db.BucketAdmins.CountAsync(),
            Contributors = await db.BucketContributors.CountAsync(),
            Viewers = await db.BucketViewers.CountAsync(),
            Messages = await db.Messages.CountAsync(),
            Tags = await db.Tags.CountAsync()
        });
        Assert.Multiple(() =>
        {
            Assert.That(counts.Namespaces, Is.Zero);
            Assert.That(counts.Managers, Is.Zero);
            Assert.That(counts.Buckets, Is.Zero);
            Assert.That(counts.Admins, Is.Zero);
            Assert.That(counts.Contributors, Is.Zero);
            Assert.That(counts.Viewers, Is.Zero);
            Assert.That(counts.Messages, Is.Zero);
            Assert.That(counts.Tags, Is.Zero);
        });
    }

    [Test]
    public async Task SetupOfferBucket_requires_a_seller()
    {
        var buyer = Buyer();
        await using var host = await GraphQLHost.StartAsync();

        var result = await host.SignedAsync(SetupMutation(seller: ""), buyer);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("SELLER_REQUIRED"),
            result.RootElement.ToString());
    }

    [Test]
    public async Task SetupOfferBucket_fails_when_a_viewer_key_cannot_be_resolved()
    {
        var buyer = Buyer();
        const string seller = TestDb.Bob;
        await using var host = await GraphQLHost.StartAsync();
        // Only the buyer's key is resolvable; the seller has no profile and no override.
        await SeedProfileWithKeyAsync(host, buyer.Value, ViewerKey32);

        var result = await host.SignedAsync(SetupMutation(seller), buyer);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("VIEWER_KEY_UNRESOLVED"),
            result.RootElement.ToString());
        var buckets = await host.WithDbAsync(db => db.Buckets.CountAsync());
        Assert.That(buckets, Is.Zero, "the failure rolls the setup back");
    }

    [Test]
    public async Task CreateOffersNamespace_is_idempotent_per_asset_and_cluster()
    {
        var buyer = Buyer();
        await using var host = await GraphQLHost.StartAsync();

        const string mutation = """
            mutation {
              createOffersNamespace(input: {
                name: "Villa Sol" assetId: "42" cluster: "devnet"
              }) { id name category cluster properties }
            }
            """;

        var first = await host.SignedAsync(mutation, buyer);
        Assert.That(first.FirstErrorCode(), Is.Null, first.RootElement.ToString());
        var created = first.Data("createOffersNamespace");
        Assert.Multiple(() =>
        {
            Assert.That(created.GetProperty("name").GetString(), Is.EqualTo("Villa Sol offers"),
                "the suffix is appended");
            Assert.That(created.GetProperty("category").GetString(), Is.EqualTo("realXmarket-offers"));
            Assert.That(created.GetProperty("cluster").GetString(), Is.EqualTo("devnet"));
            Assert.That(created.GetProperty("properties").GetString(),
                Is.EqualTo("""{"assetId":"42"}"""));
        });

        var second = await host.SignedAsync(mutation, buyer);
        Assert.That(second.FirstErrorCode(), Is.Null, second.RootElement.ToString());
        Assert.That(second.Data("createOffersNamespace").GetProperty("id").GetString(),
            Is.EqualTo(created.GetProperty("id").GetString()),
            "same assetId + cluster finds the existing namespace");

        var namespaces = await host.WithDbAsync(db => db.Namespaces.CountAsync());
        Assert.That(namespaces, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateOffersNamespace_keeps_an_already_suffixed_name()
    {
        var buyer = Buyer();
        await using var host = await GraphQLHost.StartAsync();

        var result = await host.SignedAsync(
            """
            mutation {
              createOffersNamespace(input: {
                name: "Villa Sol offers" assetId: "42" cluster: "devnet"
              }) { name }
            }
            """, buyer);

        Assert.That(result.FirstErrorCode(), Is.Null, result.RootElement.ToString());
        Assert.That(result.Data("createOffersNamespace").GetProperty("name").GetString(),
            Is.EqualTo("Villa Sol offers"), "the suffix is not doubled");
    }

    [Test]
    public async Task CreateOffersNamespace_distinguishes_assets_and_clusters()
    {
        var buyer = Buyer();
        await using var host = await GraphQLHost.StartAsync();

        async Task<string> IdFor(string assetId, string cluster)
        {
            var result = await host.SignedAsync($$"""
                mutation {
                  createOffersNamespace(input: {
                    name: "Villa Sol" assetId: "{{assetId}}" cluster: "{{cluster}}"
                  }) { id }
                }
                """, buyer);
            Assert.That(result.FirstErrorCode(), Is.Null, result.RootElement.ToString());
            return result.Data("createOffersNamespace").GetProperty("id").GetString()!;
        }

        var original = await IdFor("42", "devnet");

        Assert.That(await IdFor("43", "devnet"), Is.Not.EqualTo(original),
            "a different asset gets its own offers namespace");
        Assert.That(await IdFor("42", "mainnet"), Is.Not.EqualTo(original),
            "a different cluster gets its own offers namespace");
        Assert.That(await IdFor("42", "devnet"), Is.EqualTo(original));

        var namespaces = await host.WithDbAsync(db => db.Namespaces.CountAsync());
        Assert.That(namespaces, Is.EqualTo(3));
    }

    [Test]
    public async Task A_non_manager_can_create_a_bucket_in_an_offers_namespace()
    {
        var buyer = Buyer();
        var other = OtherBuyer();
        await using var host = await GraphQLHost.StartAsync();

        await host.SignedAsync(
            """
            mutation {
              createOffersNamespace(input: {
                name: "Villa Sol" assetId: "42" cluster: "devnet"
              }) { id }
            }
            """, buyer);

        var result = await host.SignedAsync(
            """
            mutation {
              createBucket(namespaceId: "1", metadata: { name: "Offer: Villa Sol #7", category: "realXmarket" }) {
                id creator
              }
            }
            """, other);

        Assert.That(result.FirstErrorCode(), Is.Null, result.RootElement.ToString());
        Assert.That(result.Data("createBucket").GetProperty("creator").GetString(),
            Is.EqualTo(other.Value));
    }

    [Test]
    public async Task A_non_manager_still_cannot_create_a_bucket_in_any_other_namespace()
    {
        var buyer = Buyer();
        var other = OtherBuyer();
        await using var host = await GraphQLHost.StartAsync();

        await host.SignedAsync(
            """
            mutation {
              createNamespace(metadata: { name: "Villa Sol", category: "property", cluster: "devnet" }) { id }
            }
            """, buyer);

        var result = await host.SignedAsync(
            """
            mutation {
              createBucket(namespaceId: "1", metadata: { name: "b", category: "c" }) { id }
            }
            """, other);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("NOT_MANAGER"));
    }

    [Test]
    public async Task SetupOfferBucket_validates_the_bucket_encryption_key()
    {
        var buyer = Buyer();
        await using var host = await GraphQLHost.StartAsync();

        var result = await host.SignedAsync(
            SetupMutation(TestDb.Bob).Replace(Key32, "0x1234"), buyer);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("INVALID_INPUT"),
            result.RootElement.ToString());
    }
}
