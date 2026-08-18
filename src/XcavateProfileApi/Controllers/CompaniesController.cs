using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;
using XcavateProfileApi.Data;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Services;
using XcavateProfileApiClient;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// Companies registered by users. A company is owned by the wallet in <c>userId</c>, which is the
/// only address (besides an admin) that may change or delete it; one wallet may own several.
/// </summary>
/// <remarks>
/// Reads are public, like the rest of the API. Writes authorize per action, the same way
/// <see cref="ProfilesController"/> does rather than through a filter, because each one asks a
/// different question: create checks the signer owns the addresses in the body, update and delete
/// check ownership of the stored record, and the logo endpoint additionally requires it to exist.
/// <para>
/// Three groups of fields are not the caller's to set. <c>companyId</c>, <c>createdAt</c> and
/// <c>updatedAt</c> are assigned by the server, and <c>permission</c> is admin-only — a company
/// cannot attest to its own compliance. All four are ignored rather than refused when a caller sends
/// them, so reading a company, editing one field and PUTting it back is safe for any caller.
/// </para>
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class CompaniesController : ControllerBase
{
    private const string BasePath = "/api/companies";

    /// <summary>The bucket profile pictures already use; company logos are a separate key prefix.</summary>
    private const string ImageBucket = "xcavate-profile";

    private readonly ProfileDbContext _context;
    private readonly ISignatureValidator _signatureValidator;
    private readonly IS3Service _s3Service;

    public CompaniesController(
        ProfileDbContext context,
        ISignatureValidator signatureValidator,
        IS3Service s3Service)
    {
        _context = context;
        _signatureValidator = signatureValidator;
        _s3Service = s3Service;
    }

    // GET: api/companies
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<Company>>> GetCompaniesAsync()
    {
        var companies = await _context.Companies.ToListAsync();
        return Ok(companies);
    }

    // GET: api/companies/company_3kQ8ZrW7yVn1pLd2XmTgQa
    [HttpGet("{companyId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Company>> GetCompanyAsync(string companyId)
    {
        var company = await _context.Companies.FindAsync(companyId);
        if (company == null)
        {
            return NotFound();
        }
        return Ok(company);
    }

    // GET: api/companies/user/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
    // Every company one wallet owns. An owner with none is an empty list, not a 404.
    [HttpGet("user/{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<Company>>> GetCompaniesByUserAsync(string userId)
    {
        var companies = await _context.Companies
            .Where(c => c.UserId == userId)
            .ToListAsync();

        return Ok(companies);
    }

    // POST: api/companies
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Company>> CreateCompanyAsync([FromBody] Company company)
    {
        // Verify authentication headers from request
        var address = Request.Headers["X-SS58-Address"].FirstOrDefault();
        var signature = Request.Headers["X-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-Timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return Unauthorized("Missing authentication headers");
        }

        // Validate signature
        var result = await _signatureValidator.ValidateAsync(
            address,
            signature,
            timestamp,
            "POST",
            BasePath,
            company);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        if (Invalid(company) is { } error)
        {
            return BadRequest(error);
        }

        var isAdmin = _signatureValidator.IsAdmin(address);

        // The signer registers a company for their own wallet: they own it, and their address is
        // recorded as the creator. An admin may register one on another wallet's behalf.
        if (!isAdmin && (company.UserId != address || company.CompanyWalletAddress != address))
        {
            return Unauthorized(
                "userId and companyWalletAddress must both be the authenticated address");
        }

        var now = Timestamps.UtcNow();

        // Server-owned fields, overwritten whatever the body said. The id is generated here rather
        // than accepted from the caller so nobody can claim an id, and permission stays admin-only.
        company.CompanyId = IdGenerator.Generate("company");
        company.Permission = isAdmin ? company.Permission : null;
        company.CreatedAt = now;
        company.UpdatedAt = now;

        _context.Companies.Add(company);
        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetCompanyAsync), new { companyId = company.CompanyId }, company);
    }

    // PUT: api/companies/company_3kQ8ZrW7yVn1pLd2XmTgQa
    // Not an upsert, unlike the profile endpoint: ids are server-generated, so a caller cannot hold
    // one that does not exist yet.
    [HttpPut("{companyId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Company>> UpdateCompanyAsync(
        string companyId, [FromBody] Company company)
    {
        // Verify authentication headers
        var address = Request.Headers["X-SS58-Address"].FirstOrDefault();
        var signature = Request.Headers["X-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-Timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return Unauthorized("Missing authentication headers");
        }

        // Validate signature
        var result = await _signatureValidator.ValidateAsync(
            address,
            signature,
            timestamp,
            "PUT",
            $"{BasePath}/{companyId}",
            company);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        if (Invalid(company) is { } error)
        {
            return BadRequest(error);
        }

        var existing = await _context.Companies.FindAsync(companyId);
        if (existing == null)
        {
            return NotFound();
        }

        var isAdmin = _signatureValidator.IsAdmin(address);

        // Check authorization: only the owning wallet or an admin can update
        if (existing.UserId != address && !isAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "You can only update your own company");
        }

        // companyWalletAddress records who created the company and never changes. Refused rather
        // than ignored because a caller sending a different one has misunderstood the field, and a
        // round-trip of a company read from the API sends the stored value back unchanged.
        if (company.CompanyWalletAddress != existing.CompanyWalletAddress)
        {
            return BadRequest("companyWalletAddress cannot be changed");
        }

        // Assigning userId to another wallet hands the company over: after this the caller is no
        // longer its owner and cannot edit it again.
        existing.UserId = company.UserId;
        existing.Name = company.Name;
        existing.Email = company.Email;
        existing.Logo = company.Logo;
        existing.Website = company.Website;
        existing.Summary = company.Summary;
        existing.Address = company.Address;

        if (isAdmin)
        {
            existing.Permission = company.Permission;
        }

        existing.UpdatedAt = Timestamps.UtcNow();

        await _context.SaveChangesAsync();
        return Ok(existing);
    }

    // DELETE: api/companies/company_3kQ8ZrW7yVn1pLd2XmTgQa
    [HttpDelete("{companyId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCompanyAsync(string companyId)
    {
        // Verify authentication headers
        var address = Request.Headers["X-SS58-Address"].FirstOrDefault();
        var signature = Request.Headers["X-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-Timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return Unauthorized("Missing authentication headers");
        }

        // Validate signature
        var result = await _signatureValidator.ValidateAsync(
            address,
            signature,
            timestamp,
            "DELETE",
            $"{BasePath}/{companyId}",
            new EmptyPayloadBody());

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        var company = await _context.Companies.FindAsync(companyId);
        if (company == null)
        {
            return NotFound();
        }

        // Check authorization: only the owning wallet or an admin can delete
        if (company.UserId != address && !_signatureValidator.IsAdmin(address))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "You can only delete your own company");
        }

        _context.Companies.Remove(company);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    // POST: api/companies/company_3kQ8ZrW7yVn1pLd2XmTgQa/logo
    // The company-logo counterpart of the profile-picture endpoint, with the same limits: see
    // ImageUploads for why the content type comes from the extension and never from the client.
    // NOTE: any reverse proxy in front of the API (e.g. nginx client_max_body_size) must allow at
    // least the same request size, or uploads fail with 413 before ever reaching this endpoint.
    [HttpPost("{companyId}/logo")]
    [RequestSizeLimit(ImageUploads.RequestSizeLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageUploads.RequestSizeLimit)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<string>> UploadLogoAsync(string companyId, IFormFile image)
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
        //
        // Validate signature
        var result = await _signatureValidator.ValidateAsync(
            address,
            signature,
            timestamp,
            "POST",
            $"{BasePath}/{companyId}/logo",
            new EmptyPayloadBody());

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        var company = await _context.Companies.FindAsync(companyId);
        if (company == null)
        {
            return NotFound();
        }

        // Check authorization: only the owning wallet or an admin can upload
        if (company.UserId != address && !_signatureValidator.IsAdmin(address))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden, "You can only upload a logo for your own company");
        }

        if (image.Length <= 0)
        {
            return BadRequest("No image file provided");
        }

        var fileName = Path.GetFileName(image.FileName);
        if (!ImageUploads.TryGetContentType(Path.GetExtension(fileName), out var contentType))
        {
            return BadRequest(ImageUploads.UnsupportedTypeMessage);
        }

        using var stream = image.OpenReadStream();

        // Key is derived from the filename only, so uploading a file with the same name rewrites
        // the existing object instead of creating a new one
        var key = $"companies/{companyId}/{fileName}";
        var url = await _s3Service.UploadImageAsync(ImageBucket, key, stream, contentType);

        company.Logo = url;
        company.UpdatedAt = Timestamps.UtcNow();
        await _context.SaveChangesAsync();

        return Ok(url);
    }

    /// <summary>
    /// The refusal message for a body that cannot be stored, or null when it is fine. Runs after the
    /// signature check, so the caller is known by the time anything is reported back.
    /// </summary>
    private static string? Invalid(Company company) =>
        FieldValidation.FirstFailure(
            !FieldValidation.IsWalletAddress(company.UserId)
                ? "userId must be a valid SS58 or Solana address"
                : null,
            !FieldValidation.IsWalletAddress(company.CompanyWalletAddress)
                ? "companyWalletAddress must be a valid SS58 or Solana address"
                : null,
            string.IsNullOrWhiteSpace(company.Name) ? "name is required" : null,
            FieldValidation.TooLong("name", company.Name, 128),
            string.IsNullOrWhiteSpace(company.Email) ? "email is required" : null,
            FieldValidation.TooLong("email", company.Email, 256),
            !string.IsNullOrWhiteSpace(company.Email) && !FieldValidation.IsEmail(company.Email)
                ? "email must be a valid email address"
                : null,
            FieldValidation.TooLong("website", company.Website, 512),
            FieldValidation.TooLong("summary", company.Summary, 2000),
            FieldValidation.TooLong("address", company.Address, 512));
}
