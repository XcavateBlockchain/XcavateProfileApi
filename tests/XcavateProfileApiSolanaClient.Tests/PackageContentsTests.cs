using System.Reflection;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Buckets;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApiSolanaClient.Tests;

/// <summary>
/// What the package is for. XcavateProfileApiClient already signs Solana requests, so the only
/// thing this package adds is the absence of Substrate.NET.API — and an assembly that quietly
/// regained that reference would still pass every behavioural test while being pointless.
/// </summary>
[TestFixture]
public class PackageContentsTests
{
    private static readonly Assembly Client = typeof(XcavateProfileClient).Assembly;

    [Test]
    public void The_shared_sources_land_in_the_solana_assembly()
    {
        Assert.That(Client.GetName().Name, Is.EqualTo("XcavateProfileApiSolanaClient"));
    }

    /// <summary>
    /// The reason the package exists. Substrate.NET.API is also what drags in StreamJsonRpc,
    /// Serilog, Newtonsoft.Json and the MessagePack advisories the other package has to pin around.
    /// </summary>
    [TestCase("Substrate")]
    [TestCase("Schnorrkel")]
    [TestCase("MessagePack")]
    [TestCase("StreamJsonRpc")]
    [TestCase("Serilog")]
    [TestCase("Newtonsoft")]
    public void No_substrate_dependency_is_referenced(string forbidden)
    {
        var referenced = Client.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.That(
            referenced,
            Has.None.Contains(forbidden).IgnoreCase,
            $"referenced: {string.Join(", ", referenced)}");
    }

    /// <summary>
    /// The Substrate/ folder is excluded by path, which is a build-file convention rather than
    /// anything the compiler enforces. If a new sr25519 type is added outside that folder it will
    /// silently reappear here and pull the dependency back in.
    /// </summary>
    [TestCase("XcavateProfileApiClient.Signing.SubstrateRequestSigner")]
    [TestCase("XcavateProfileApiClient.Signing.Sr25519SignatureScheme")]
    public void Substrate_only_types_are_excluded(string typeName)
    {
        Assert.That(Client.GetType(typeName), Is.Null);
    }

    [Test]
    public void Substrate_only_members_are_excluded()
    {
        var cryptoHelper = typeof(CryptoHelper);

        Assert.Multiple(() =>
        {
            Assert.That(cryptoHelper.GetMethod("SignAsync"), Is.Null, "sr25519 signing");
            Assert.That(cryptoHelper.GetMethod("VerifySignature"), Is.Null, "sr25519 verification");
        });
    }

    /// <summary>Everything a Solana consumer actually calls has to survive the exclusion.</summary>
    [Test]
    public void The_chain_agnostic_surface_is_intact()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(XcavateProfileClient), Is.Not.Null);
            Assert.That(typeof(Profile), Is.Not.Null);
            Assert.That(typeof(Company), Is.Not.Null);
            Assert.That(typeof(WalletMigration), Is.Not.Null);
            Assert.That(typeof(UserRole), Is.Not.Null);
            Assert.That(typeof(UserPermissions), Is.Not.Null);
            Assert.That(typeof(CompanyPermissions), Is.Not.Null);
            Assert.That(typeof(SolanaRequestSigner), Is.Not.Null);
            Assert.That(typeof(SolanaSignatureScheme), Is.Not.Null);
            Assert.That(typeof(SignatureEncoding), Is.Not.Null);
            Assert.That(typeof(SigningHttpMessageHandler), Is.Not.Null);
            Assert.That(typeof(CryptoHelper).GetMethod("Hash"), Is.Not.Null);
            Assert.That(typeof(CryptoHelper).GetMethod("ConstructPayload"), Is.Not.Null);
        });
    }

    /// <summary>
    /// The GraphQL client is generated per-project from operations copied in at build time, so
    /// unlike the hand-written sources it can go missing without breaking compilation of anything
    /// else in this package.
    /// </summary>
    [Test]
    public void The_generated_graphql_client_is_present()
    {
        var operations = typeof(IXcavateBucketsClient).GetProperties();

        Assert.That(operations, Is.Not.Empty);
        Assert.That(
            operations.Select(p => p.Name),
            Does.Contain("CreateNamespace").And.Contain("GetBuckets"));
    }

    /// <summary>
    /// The signing overloads that take a Substrate <c>Account</c> are gone; the
    /// <see cref="IRequestSigner"/> ones they delegated to are the whole API here.
    /// </summary>
    [Test]
    public void Every_write_still_accepts_a_signer()
    {
        var methods = typeof(XcavateProfileClient)
            .GetMethods()
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(IRequestSigner)))
            .Select(m => m.Name)
            .Distinct();

        Assert.That(
            methods,
            Is.EquivalentTo(new[]
            {
                nameof(XcavateProfileClient.CreateProfileAsync),
                nameof(XcavateProfileClient.UpdateProfileAsync),
                nameof(XcavateProfileClient.DeleteProfileAsync),
                nameof(XcavateProfileClient.UploadImageAsync),
                nameof(XcavateProfileClient.RegisterWalletMigrationAsync),
                nameof(XcavateProfileClient.CreateCompanyAsync),
                nameof(XcavateProfileClient.UpdateCompanyAsync),
                nameof(XcavateProfileClient.DeleteCompanyAsync),
                nameof(XcavateProfileClient.UploadCompanyLogoAsync)
            }));
    }
}
