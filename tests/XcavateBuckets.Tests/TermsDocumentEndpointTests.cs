using System.Net;
using System.Net.Http.Headers;

namespace XcavateBuckets.Tests;

/// <summary>
/// The lawyer terms-document endpoints — a signed multipart PDF upload stored in S3 under a
/// fixed per-wallet key, and a public read — driven through the shipped SDK against the real
/// controller, validator and database.
/// </summary>
[TestFixture]
public class TermsDocumentEndpointTests
{
    private static MemoryStream Pdf() => new("%PDF-1.4 not really a pdf"u8.ToArray());

    [Test]
    public async Task Upload_pdf_persists_row_and_returns_url()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var lawyer = TestWallets.Substrate(0xA0);

        using var pdf = Pdf();
        var uploaded = await client.UploadTermsDocumentAsync(lawyer.Value, "terms.pdf", pdf, lawyer);

        var stored = await host.StoredTermsDocumentAsync(lawyer.Value);
        var downloaded = await host.S3.DownloadObjectAsync(
            "xcavate-profile", $"terms-documents/{lawyer.Value}.pdf", CancellationToken.None);

        var expectedUrl = $"https://objects.test/xcavate-profile/terms-documents/{lawyer.Value}.pdf";
        Assert.Multiple(() =>
        {
            Assert.That(uploaded.Url, Is.EqualTo(expectedUrl));
            Assert.That(uploaded.FileName, Is.EqualTo("terms.pdf"));
            Assert.That(uploaded.UpdatedAt, Is.GreaterThan(DateTimeOffset.MinValue));

            Assert.That(stored?.Ss58Address, Is.EqualTo(lawyer.Value));
            Assert.That(stored?.Url, Is.EqualTo(uploaded.Url));
            Assert.That(stored?.FileName, Is.EqualTo("terms.pdf"));
            Assert.That(stored?.ContentType, Is.EqualTo("application/pdf"));

            Assert.That(host.S3.Uploads, Has.Count.EqualTo(1));
            Assert.That(host.S3.Uploads[0].Bucket, Is.EqualTo("xcavate-profile"));
            Assert.That(
                host.S3.Uploads[0].Key, Is.EqualTo($"terms-documents/{lawyer.Value}.pdf"));
            Assert.That(host.S3.Uploads[0].ContentType, Is.EqualTo("application/pdf"));
            Assert.That(downloaded, Is.EqualTo("%PDF-1.4 not really a pdf"u8.ToArray()));
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
            Assert.That(
                host.S3.Uploads, Has.Count.EqualTo(2),
                "both uploads reach the store — the second rewrites the same object");
            Assert.That(
                host.S3.Uploads[1].Key, Is.EqualTo(host.S3.Uploads[0].Key),
                "the key is fixed per wallet, so the re-upload overwrites the object");
            Assert.That(stored?.Url, Is.EqualTo(uploadedSecond.Url));
            Assert.That(stored?.FileName, Is.EqualTo("terms-v2.pdf"));
            Assert.That(
                stored?.UpdatedAt, Is.GreaterThanOrEqualTo(uploadedFirst.UpdatedAt),
                "the row reflects the latest upload");
            Assert.That(read?.Url, Is.EqualTo(uploadedSecond.Url));
            Assert.That(read?.UpdatedAt, Is.EqualTo(uploadedSecond.UpdatedAt));
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
            Assert.That(host.S3.Uploads, Is.Empty, "nothing may reach the store unsigned");
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
            Assert.That(host.S3.Uploads, Is.Empty, "a refused file must never reach the store");
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
            Assert.That(host.S3.Uploads, Is.Empty);
            Assert.That(host.StoredTermsDocumentAsync(lawyer.Value).Result, Is.Null);
        });
    }
}
