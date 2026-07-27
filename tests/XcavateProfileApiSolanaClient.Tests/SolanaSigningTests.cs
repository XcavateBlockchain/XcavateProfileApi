using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApiSolanaClient.Tests;

/// <summary>
/// Drives the REST client over a stub transport and verifies the signatures it produces with the
/// same scheme the server uses. No network, no keys on disk — but it covers the join the E2E suite
/// otherwise owns alone: that the payload the client signs is reconstructible from the request it
/// actually sent.
/// </summary>
[TestFixture]
public class SolanaSigningTests
{
    private const string ApiUrl = "http://localhost:5000";

    private const string X25519Key =
        "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    private static Account NewAccount(string phrase) =>
        new Wallet(new Mnemonic(phrase, WordList.English)).Account;

    /// <summary>A valid BIP39 phrase; the derived address is irrelevant as long as it is stable.</summary>
    private static Account TestAccount => NewAccount(
        "legal winner thank year wave sausage worth useful legal winner thank yellow");

    private static Profile NewProfile(string address) =>
        new() { Ss58Address = address, Nickname = "solana-user", X25519Key = X25519Key };

    /// <summary>Records requests and answers each with <paramref name="respond"/>.</summary>
    /// <remarks>
    /// The body is read here rather than from the recorded message: the request owns its content and
    /// disposes it once the call returns, so reading afterwards throws.
    /// </remarks>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public Dictionary<HttpRequestMessage, string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            // Concurrent_writes_do_not_share_signature_headers drives this from several tasks.
            lock (_gate)
            {
                Requests.Add(request);
                Bodies[request] = body;
            }

            return respond(request);
        }
    }

    private static StubHandler EchoingProfile() => new(request =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new { ss58address = "unused", nickname = "solana-user", x25519Key = X25519Key }),
            RequestMessage = request
        });

    private static (XcavateProfileClient Client, StubHandler Handler) NewClient(StubHandler? handler = null)
    {
        var stub = handler ?? EchoingProfile();
        return (
            new XcavateProfileClient(
                new XcavateProfileClientOptions { ApiUrl = ApiUrl }, new HttpClient(stub)),
            stub);
    }

    /// <summary>
    /// Rebuilds the payload from the request as the server would, and checks the signature over it.
    /// This is the assertion that would fail if the client signed a different path, body or
    /// timestamp than the one it put on the wire.
    /// </summary>
    private static bool SignatureVerifies(HttpRequestMessage request, string signedPath, IPayloadBody body)
    {
        var address = request.Headers.GetValues(SignedRequestHeaders.Address).Single();
        var signature = request.Headers.GetValues(SignedRequestHeaders.Signature).Single();
        var timestamp = request.Headers.GetValues(SignedRequestHeaders.Timestamp).Single();

        var parsed = DateTime.Parse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        Assert.That(SignatureEncoding.TryDecode(signature, out var signatureBytes), Is.True,
            "the header must decode as base58");

        var payload = CryptoHelper.ConstructPayload(request.Method.Method, signedPath, body, parsed);

        return new SolanaSignatureScheme().Verify(payload, signatureBytes, address);
    }

    [Test]
    public void Signer_round_trips_through_the_servers_scheme()
    {
        var signer = new SolanaRequestSigner(TestAccount);
        const string payload = "POST:/api/profiles:0xdeadbeef:2026-07-27T10:00:00.0000000Z";

        var signature = signer.SignAsync(payload).Result;

        Assert.Multiple(() =>
        {
            Assert.That(new SolanaSignatureScheme().CanVerify(signer.Address), Is.True);
            Assert.That(
                new SolanaSignatureScheme().Verify(payload, signature, signer.Address), Is.True);
            // Base58, not hex — what a browser wallet's bs58.encode produces.
            Assert.That(signer.EncodeSignature(signature), Does.Not.StartWith("0x"));
        });
    }

    [Test]
    public async Task Create_signs_the_profile_body_and_the_posted_bytes_match()
    {
        var signer = new SolanaRequestSigner(TestAccount);
        var (client, handler) = NewClient();
        using var _ = client;

        var profile = NewProfile(signer.Address);
        await client.CreateProfileAsync(profile, signer);

        var request = handler.Requests.Single();
        var sentJson = handler.Bodies[request];

        Assert.Multiple(() =>
        {
            Assert.That(SignatureVerifies(request, "/api/profiles", profile), Is.True);
            // The signature covers Profile.Hash(); if the serialized body differed from what was
            // hashed, the server would compute a different payload and reject it.
            Assert.That(
                CryptoHelper.HashHex(sentJson), Is.EqualTo(profile.Hash()),
                "the bytes POSTed must be the bytes that were hashed");
        });
    }

    [Test]
    public async Task Delete_signs_an_empty_body_over_the_address_path()
    {
        var signer = new SolanaRequestSigner(TestAccount);
        var (client, handler) = NewClient();
        using var _ = client;

        await client.DeleteProfileAsync(signer.Address, signer);

        var request = handler.Requests.Single();

        Assert.That(
            SignatureVerifies(request, $"/api/profiles/{signer.Address}", EmptyPayloadBody.Instance),
            Is.True);
    }

    /// <summary>
    /// Multipart uploads are the one case where the signed body is deliberately not the request
    /// body: the server hashes an empty body, so the client must too.
    /// </summary>
    [Test]
    public async Task Image_upload_signs_an_empty_body_not_the_file()
    {
        var signer = new SolanaRequestSigner(TestAccount);
        var (client, handler) = NewClient(new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("https://cdn.example/pic.png"),
                RequestMessage = request
            }));
        using var _ = client;

        using var image = new MemoryStream([1, 2, 3, 4]);
        var url = await client.UploadImageAsync(signer.Address, image, "pic.png", signer);

        var request = handler.Requests.Single();

        Assert.Multiple(() =>
        {
            Assert.That(url, Is.EqualTo("https://cdn.example/pic.png"));
            Assert.That(
                SignatureVerifies(
                    request, $"/api/profiles/{signer.Address}/image", EmptyPayloadBody.Instance),
                Is.True);
            Assert.That(
                request.Content!.Headers.ContentType!.MediaType, Is.EqualTo("multipart/form-data"));
        });
    }

    /// <summary>
    /// A nickname is free text, so it has to be percent-encoded in the URI. The server signs over
    /// the decoded route value, so the payload must keep the raw form — escaping both, or neither,
    /// breaks the lookup.
    /// </summary>
    [Test]
    public async Task Nickname_lookup_escapes_the_uri()
    {
        var (client, handler) = NewClient(new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }));
        using var _ = client;

        var result = await client.GetProfileByNicknameAsync("a name/with?specials");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null, "404 surfaces as null rather than throwing");
            Assert.That(
                handler.Requests.Single().RequestUri!.AbsoluteUri,
                Is.EqualTo($"{ApiUrl}/api/profiles/nickname/a%20name%2Fwith%3Fspecials"));
        });
    }

    /// <summary>
    /// Signature headers used to be written to HttpClient.DefaultRequestHeaders, so two writes in
    /// flight at once would sign with one identity and send with the other's headers. They are now
    /// per-request; every one of these must verify against its own signer.
    /// </summary>
    [Test]
    public async Task Concurrent_writes_do_not_share_signature_headers()
    {
        var signers = Enumerable
            .Range(0, 8)
            .Select(_ => new SolanaRequestSigner(new Account()))
            .ToList();

        var (client, handler) = NewClient();
        using var _ = client;

        var profiles = signers.ToDictionary(s => s, s => NewProfile(s.Address));

        await Task.WhenAll(signers.Select(s => client.CreateProfileAsync(profiles[s], s)));

        Assert.That(handler.Requests, Has.Count.EqualTo(signers.Count));

        var addresses = handler.Requests
            .Select(r => r.Headers.GetValues(SignedRequestHeaders.Address).Single())
            .ToList();

        Assert.That(addresses, Is.Unique, "each request must carry its own signer's address");

        foreach (var request in handler.Requests)
        {
            var address = request.Headers.GetValues(SignedRequestHeaders.Address).Single();
            var signer = signers.Single(s => s.Address == address);

            Assert.That(
                SignatureVerifies(request, "/api/profiles", profiles[signer]), Is.True,
                $"the signature sent for {address} must verify against that address");
        }
    }

    /// <summary>The API explains its refusals in the body; the old client discarded that.</summary>
    [Test]
    public void Error_responses_carry_the_status_and_the_server_message()
    {
        var signer = new SolanaRequestSigner(TestAccount);
        var (client, _) = NewClient(new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("You can only update your own profile"),
                RequestMessage = request
            }));
        using var __ = client;

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateProfileAsync(signer.Address, NewProfile(signer.Address), signer));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(ex.Message, Does.Contain("You can only update your own profile"));
        });
    }

    /// <summary>
    /// A base address with a path is only preserved if it ends in a slash; without the fix-up the
    /// "profile-api" segment is discarded and every request goes to the wrong URL.
    /// </summary>
    [Test]
    public async Task Base_address_with_a_path_prefix_is_preserved()
    {
        var handler = new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<Profile>()),
                RequestMessage = request
            });

        using var client = new XcavateProfileClient(
            new XcavateProfileClientOptions { ApiUrl = "https://host/profile-api" },
            new HttpClient(handler));

        await client.GetProfilesAsync();

        Assert.That(
            handler.Requests.Single().RequestUri!.AbsoluteUri,
            Is.EqualTo("https://host/profile-api/api/profiles"));
    }

    /// <summary>
    /// HttpClient.BaseAddress cannot be assigned once the instance has sent a request, so a client
    /// that set it would throw for exactly the shared-HttpClient case the overload exists to serve.
    /// The base address is held by XcavateProfileClient instead and never written to the caller's
    /// object.
    /// </summary>
    [Test]
    public async Task An_already_used_http_client_can_still_be_supplied()
    {
        var handler = new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<Profile>()),
                RequestMessage = request
            });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://elsewhere/") };

        // Sending once is what locks BaseAddress against further assignment.
        await httpClient.GetAsync("warm-up");

        using var client = new XcavateProfileClient(
            new XcavateProfileClientOptions { ApiUrl = ApiUrl }, httpClient);

        await client.GetProfilesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                handler.Requests.Last().RequestUri!.AbsoluteUri,
                Is.EqualTo($"{ApiUrl}/api/profiles"),
                "requests must go to ApiUrl, not the client's own BaseAddress");
            Assert.That(
                httpClient.BaseAddress, Is.EqualTo(new Uri("https://elsewhere/")),
                "the caller's HttpClient must not be mutated");
        });
    }

    /// <summary>An injected HttpClient belongs to the caller — typically IHttpClientFactory.</summary>
    [Test]
    public async Task Disposing_the_client_leaves_an_injected_http_client_usable()
    {
        var handler = EchoingProfile();
        var httpClient = new HttpClient(handler);

        using (var client = new XcavateProfileClient(
                   new XcavateProfileClientOptions { ApiUrl = ApiUrl }, httpClient))
        {
            await client.GetProfileAsync("someone");
        }

        // Absolute, because the client deliberately never set BaseAddress on the caller's instance.
        Assert.DoesNotThrowAsync(() => httpClient.GetAsync($"{ApiUrl}/api/profiles"));
    }
}
