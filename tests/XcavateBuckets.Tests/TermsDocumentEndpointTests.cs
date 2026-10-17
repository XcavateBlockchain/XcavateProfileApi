using System.Net;
using System.Net.Http.Headers;
using XcavateProfileApi.Services.Ipfs;

namespace XcavateBuckets.Tests;

/// <summary>
/// The lawyer terms-document endpoints — a signed multipart PDF upload pinned to IPFS, the CID
/// stored one-per-wallet, and a public read — driven through the shipped SDK against the real
/// controller, validator and database.
/// </summary>
[TestFixture]
public class TermsDocumentEndpointTests
{
    private static MemoryStream Pdf() => new("%PDF-1.4 not really a pdf"u8.ToArray());

    [Test]
    public async Task Upload_pdf_returns_cid_and_persists_row()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA0);

        using var pdf = Pdf();
        var uploaded = await client.UploadTermsDocumentAsync(lawyer.Value, "terms.pdf", pdf, lawyer);

        var stored = await host.StoredTermsDocumentAsync(lawyer.Value);

        Assert.Multiple(() =>
        {
            Assert.That(uploaded.Cid, Is.EqualTo("bafytestcid1"));
            Assert.That(
                uploaded.Url, Is.EqualTo("https://gateway.test/ipfs/bafytestcid1"),
                "the URL is the gateway URL for the returned CID");
            Assert.That(uploaded.FileName, Is.EqualTo("terms.pdf"));
            Assert.That(uploaded.UpdatedAt, Is.GreaterThan(DateTimeOffset.MinValue));

            Assert.That(stored?.Ss58Address, Is.EqualTo(lawyer.Value));
            Assert.That(stored?.Cid, Is.EqualTo(uploaded.Cid));
            Assert.That(stored?.FileName, Is.EqualTo("terms.pdf"));
            Assert.That(stored?.ContentType, Is.EqualTo("application/pdf"));

            Assert.That(host.IpfsStub.Pins, Has.Count.EqualTo(1));
            Assert.That(host.IpfsStub.Pins[0].FileName, Is.EqualTo("terms.pdf"));
            Assert.That(host.IpfsStub.Pins[0].ContentType, Is.EqualTo("application/pdf"));
        });
    }

    [Test]
    public async Task Get_returns_404_when_none()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA1);

        Assert.That(await client.GetTermsDocumentAsync(lawyer.Value), Is.Null);
    }

    [Test]
    public async Task Get_returns_document_after_upload()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA2);

        using var pdf = Pdf();
        var uploaded = await client.UploadTermsDocumentAsync(lawyer.Value, "terms.pdf", pdf, lawyer);

        // The GET is public — a raw client with no signature headers sees the same document.
        using var response = await host.Client.GetAsync(
            $"/api/profiles/{lawyer.Value}/terms-document");
        var read = await client.GetTermsDocumentAsync(lawyer.Value);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(read?.Cid, Is.EqualTo(uploaded.Cid));
            Assert.That(read?.Url, Is.EqualTo(uploaded.Url));
            Assert.That(read?.FileName, Is.EqualTo(uploaded.FileName));
            Assert.That(read?.UpdatedAt, Is.EqualTo(uploaded.UpdatedAt));
        });
    }

    [Test]
    public async Task Second_upload_replaces_document()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA3);

        using var first = Pdf();
        var uploadedFirst =
            await client.UploadTermsDocumentAsync(lawyer.Value, "terms-v1.pdf", first, lawyer);

        using var second = Pdf();
        var uploadedSecond =
            await client.UploadTermsDocumentAsync(lawyer.Value, "terms-v2.pdf", second, lawyer);

        var stored = await host.StoredTermsDocumentAsync(lawyer.Value);
        var read = await client.GetTermsDocumentAsync(lawyer.Value);
        var rowCount = await host.TermsDocumentCountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                rowCount, Is.EqualTo(1),
                "a re-upload replaces the row rather than adding a second one");
            Assert.That(uploadedSecond.Cid, Is.Not.EqualTo(uploadedFirst.Cid));
            Assert.That(stored?.Cid, Is.EqualTo(uploadedSecond.Cid));
            Assert.That(stored?.FileName, Is.EqualTo("terms-v2.pdf"));
            Assert.That(read?.Cid, Is.EqualTo(uploadedSecond.Cid));
            Assert.That(read?.Url, Is.EqualTo(uploadedSecond.Url));
        });
    }

    [Test]
    public async Task Upload_without_signature_headers_rejected()
    {
        await using var host = await RestHost.StartAsync();

        var lawyer = TestWallets.Substrate(0xA4);

        using var pdf = Pdf();
        using var fileContent = new StreamContent(pdf);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var multipart = new MultipartFormDataContent { { fileContent, "file", "terms.pdf" } };

        using var response = await host.Client.PostAsync(
            $"/api/profiles/{lawyer.Value}/terms-document", multipart);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(host.IpfsStub.Pins, Is.Empty, "nothing may reach IPFS unsigned");
            Assert.That(host.StoredTermsDocumentAsync(lawyer.Value).Result, Is.Null);
        });
    }

    [Test]
    public async Task Upload_non_pdf_rejected()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA5);

        using var text = new MemoryStream("plain text"u8.ToArray());

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UploadTermsDocumentAsync(lawyer.Value, "terms.txt", text, lawyer));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(host.IpfsStub.Pins, Is.Empty, "a refused file must never reach IPFS");
        });
    }

    [Test]
    public async Task Upload_by_other_wallet_forbidden()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA6);
        var other = TestWallets.Substrate(0xA7);

        using var pdf = Pdf();

        // B's signature is genuine — it just does not own A's terms document.
        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UploadTermsDocumentAsync(lawyer.Value, "terms.pdf", pdf, other));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(host.IpfsStub.Pins, Is.Empty);
            Assert.That(host.StoredTermsDocumentAsync(lawyer.Value).Result, Is.Null);
        });
    }

    [Test]
    public async Task Upload_returns_503_when_ipfs_not_configured()
    {
        await using var host = await RestHost.StartAsync(new NullIpfsService());
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA8);

        using var pdf = Pdf();

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UploadTermsDocumentAsync(lawyer.Value, "terms.pdf", pdf, lawyer));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Upload_returns_502_when_pinata_fails()
    {
        var ipfs = new StubIpfsService
        {
            PinError = new HttpRequestException("Pinata pinFileToIPFS failed: 401 Unauthorized")
        };
        await using var host = await RestHost.StartAsync(ipfs);
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA9);

        using var pdf = Pdf();

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UploadTermsDocumentAsync(lawyer.Value, "terms.pdf", pdf, lawyer));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(
                host.StoredTermsDocumentAsync(lawyer.Value).Result, Is.Null,
                "a failed pin must not leave a stored CID behind");
        });
    }
}
