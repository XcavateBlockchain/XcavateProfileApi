using Microsoft.AspNetCore.Mvc;
using XcavateProfileApi.Data;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Models;
using XcavateProfileApi.Services;
using XcavateProfileApi.Services.Ipfs;
using XcavateProfileApi.Swagger;
using XcavateProfileApiClient;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// The lawyer "Terms document": one PDF per wallet address, pinned to IPFS. Uploading again
/// replaces the document — only the current CID is kept. Reading is public, so the terms a
/// lawyer agreed to can be fetched by anyone from the wallet address alone.
/// </summary>
[ApiController]
[Route("api/profiles/{ss58address}/terms-document")]
public class TermsDocumentController : ControllerBase
{
    private const string BasePath = "/api/profiles";

    /// <summary>25 MB of PDF, plus a megabyte for multipart encoding overhead.</summary>
    private const int RequestSizeLimit = 26 * 1024 * 1024;

    private const string PdfExtension = ".pdf";
    private const string PdfContentType = "application/pdf";

    private const string UnsupportedTypeMessage = "Unsupported file type. Only PDF (.pdf) is allowed.";

    private readonly ProfileDbContext _context;
    private readonly ISignatureValidator _signatureValidator;
    private readonly IIpfsService _ipfsService;

    public TermsDocumentController(
        ProfileDbContext context,
        ISignatureValidator signatureValidator,
        IIpfsService ipfsService)
    {
        _context = context;
        _signatureValidator = signatureValidator;
        _ipfsService = ipfsService;
    }

    /// <summary>
    /// Gets the wallet's current terms document: CID, gateway URL, file name and when it was
    /// last replaced. Public read — no signature. 404 when the wallet has never uploaded one.
    /// </summary>
    // GET: api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W/terms-document
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TermsDocumentResponse>> GetTermsDocumentAsync(string ss58address)
    {
        var document = await _context.LawyerTermsDocuments.FindAsync(ss58address);
        if (document == null)
        {
            return NotFound();
        }

        return Ok(ToResponse(document));
    }

    /// <summary>
    /// Uploads (or replaces) the wallet's terms document (multipart PDF, up to 25 MB). Requires a
    /// signed request from the owner or an admin; like the image upload, the signature covers an
    /// empty payload — the file bytes are deliberately outside it. The file is pinned to IPFS and
    /// its CID stored keyed by the wallet address, replacing any previous document.
    /// </summary>
    /// <remarks>
    /// NOTE: any reverse proxy in front of the API (e.g. nginx client_max_body_size) must allow
    /// at least the same request size, or uploads fail with 413 before ever reaching this
    /// endpoint.
    /// </remarks>
    [SignedRequest]
    // POST: api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W/terms-document
    // PDFs up to 25MB are supported; the extra 1MB covers multipart encoding overhead.
    [HttpPost]
    [RequestSizeLimit(RequestSizeLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = RequestSizeLimit)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TermsDocumentResponse>> UploadTermsDocumentAsync(
        string ss58address, IFormFile file, CancellationToken cancellationToken)
    {
        // Verify authentication headers
        var address = Request.Headers["X-SS58-Address"].FirstOrDefault();
        var signature = Request.Headers["X-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-Timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return Unauthorized("Missing authentication headers");
        }

        // The signed body for a multipart upload is EmptyPayloadBody, whose hash is the empty
        // string rather than the hash of one — the client signs the same, and the file bytes are
        // deliberately outside the signature.
        var result = await _signatureValidator.ValidateAsync(
            address,
            signature,
            timestamp,
            "POST",
            $"{BasePath}/{ss58address}/terms-document",
            new EmptyPayloadBody());

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        // Check authorization: can only upload the terms document for the own wallet or is admin
        if (address != ss58address && !_signatureValidator.IsAdmin(address))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                "You can only upload the terms document for your own wallet");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        // The gateway serves pinned files publicly, so the stored content type must never come
        // from the client unchecked — PDF only, by extension AND declared content type.
        var fileName = Path.GetFileName(file.FileName);
        if (!string.Equals(Path.GetExtension(fileName), PdfExtension, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(file.ContentType, PdfContentType, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(UnsupportedTypeMessage);
        }

        string cid;
        try
        {
            await using var stream = file.OpenReadStream();
            cid = await _ipfsService.PinFileAsync(stream, fileName, PdfContentType, cancellationToken);
        }
        catch (IpfsNotConfiguredException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                $"The IPFS pinning service rejected the upload: {exception.Message}");
        }

        var now = new DateTimeOffset(Timestamps.UtcNow(), TimeSpan.Zero);

        // Upsert: one terms document per wallet — a re-upload replaces the row.
        var document = await _context.LawyerTermsDocuments.FindAsync(ss58address);
        if (document == null)
        {
            document = new LawyerTermsDocument
            {
                Ss58Address = ss58address,
                Cid = cid,
                FileName = fileName,
                ContentType = PdfContentType,
                UpdatedAt = now
            };
            _context.LawyerTermsDocuments.Add(document);
        }
        else
        {
            document.Cid = cid;
            document.FileName = fileName;
            document.ContentType = PdfContentType;
            document.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ToResponse(document));
    }

    private TermsDocumentResponse ToResponse(LawyerTermsDocument document) =>
        new()
        {
            Cid = document.Cid,
            Url = _ipfsService.BuildGatewayUrl(document.Cid),
            FileName = document.FileName,
            UpdatedAt = document.UpdatedAt
        };
}
