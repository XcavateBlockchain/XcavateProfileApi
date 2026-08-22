using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;
using XcavateProfileApi.Data;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Services;
using XcavateProfileApiClient;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// User profiles, keyed by the wallet address that owns them.
/// </summary>
/// <remarks>
/// <c>userId</c>, <c>createdAt</c> and <c>updatedAt</c> are assigned by the server, and
/// <c>permission</c> is admin-only — a signature proves who the caller is, never that they are
/// compliant, so a profile cannot record its own clearance. The three server-owned fields are
/// overwritten rather than refused when a caller sends them, so reading a profile, editing one field
/// and PUTting it back is safe for any caller; <c>permission</c> is likewise left as it was stored.
/// The one exception is <c>userId</c>, which is refused when it contradicts the address it belongs to
/// rather than being silently corrected.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class ProfilesController : ControllerBase
{
    private const string BasePath = "/api/profiles";

    /// <summary>The bucket profile pictures are stored in.</summary>
    private const string ImageBucket = "xcavate-profile";

    private readonly ProfileDbContext _context;
    private readonly ISignatureValidator _signatureValidator;
    private readonly IS3Service _s3Service;

    public ProfilesController(
        ProfileDbContext context,
        ISignatureValidator signatureValidator,
        IS3Service s3Service)
    {
        _context = context;
        _signatureValidator = signatureValidator;
        _s3Service = s3Service;
    }

    // GET: api/profiles
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<Profile>>> GetProfilesAsync()
    {
        var profiles = await _context.Profiles.ToListAsync();
        return Ok(profiles);
    }

    // GET: api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
    [HttpGet("{ss58address}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Profile>> GetProfileAsync(string ss58address)
    {
        var profile = await _context.Profiles.FindAsync(ss58address);
        if (profile == null)
        {
            return NotFound();
        }
        return Ok(profile);
    }

    // GET: api/profiles/nickname/xena
    // The lookup ignores case: /nickname/xena and /nickname/Xena are the same request, because
    // nicknames are unique that way — see Nicknames.
    [HttpGet("nickname/{nickname}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Profile>> GetProfileByNicknameAsync(string nickname)
    {
        var profile = await _context.Profiles.WithNickname(nickname).FirstOrDefaultAsync();
        if (profile == null)
        {
            return NotFound();
        }
        return Ok(profile);
    }

    // POST: api/profiles
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Profile>> CreateProfileAsync([FromBody] Profile profile)
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
            profile);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        if (Invalid(profile, profile.Ss58Address) is { } error)
        {
            return BadRequest(error);
        }

        // Check if the user is trying to create a profile for someone else
        if (!string.IsNullOrEmpty(address) && address != profile.Ss58Address)
        {
            return Unauthorized("Can only create profile for authenticated address");
        }

        // Check if profile already exists
        if (await _context.Profiles.FindAsync(profile.Ss58Address) != null)
        {
            return BadRequest("Profile already exists");
        }

        // Check nickname uniqueness, ignoring case: "Tester" is taken once "tester" is
        if (await _context.Profiles.WithNickname(profile.Nickname).AnyAsync())
        {
            return BadRequest("Nickname already exists");
        }

        var createdAt = Timestamps.UtcNow();

        // Server-owned fields, overwritten whatever the body said.
        profile.UserId = profile.Ss58Address;
        profile.Roles = AsSet(profile.Roles);
        profile.Permission = _signatureValidator.IsAdmin(address) ? profile.Permission : null;
        profile.CreatedAt = createdAt;
        profile.UpdatedAt = createdAt;

        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetProfileAsync), new { ss58address = profile.Ss58Address }, profile);
    }

    // PUT: api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
    // Upsert: creates the profile when it does not exist yet
    [HttpPut("{ss58address}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Profile>> UpdateProfileAsync(string ss58address, [FromBody] Profile profile)
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
            $"{BasePath}/{ss58address}",
            profile);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        var isAdmin = _signatureValidator.IsAdmin(address);

        // Check authorization: can only update own profile or is admin
        if (address != ss58address && !isAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "You can only update your own profile");
        }

        if (Invalid(profile, ss58address) is { } error)
        {
            return BadRequest(error);
        }

        var existingProfile = await _context.Profiles.FindAsync(ss58address);

        // Check nickname uniqueness if the nickname is being set or changed. Case alone is not a
        // change worth checking — an owner recasing "tester" to "Tester" keeps the name they hold.
        if (!Nicknames.AreSame(profile.Nickname, existingProfile?.Nickname))
        {
            if (await _context.Profiles
                    .WithNickname(profile.Nickname)
                    .AnyAsync(p => p.Ss58Address != ss58address, CancellationToken.None))
            {
                return BadRequest("Nickname already exists");
            }
        }

        var now = Timestamps.UtcNow();

        // Create the profile when it does not exist yet (upsert)
        if (existingProfile == null)
        {
            var newProfile = new Profile
            {
                // The route address is authoritative, same as the update path below
                Ss58Address = ss58address,
                Nickname = profile.Nickname,
                Bio = profile.Bio,
                ProfilePicture = profile.ProfilePicture,
                X25519Key = profile.X25519Key,
                UserId = ss58address,
                Name = profile.Name,
                Email = profile.Email,
                Phone = profile.Phone,
                Address = profile.Address,
                Title = profile.Title,
                Background = profile.Background,
                Roles = AsSet(profile.Roles),
                Permission = isAdmin ? profile.Permission : null,
                CreatedAt = now,
                UpdatedAt = now
            };

            _context.Profiles.Add(newProfile);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetProfileAsync), new { ss58address = newProfile.Ss58Address }, newProfile);
        }

        // Update profile properties
        existingProfile.Nickname = profile.Nickname;
        existingProfile.Bio = profile.Bio;
        existingProfile.ProfilePicture = profile.ProfilePicture;
        existingProfile.X25519Key = profile.X25519Key;
        existingProfile.UserId = ss58address;
        existingProfile.Name = profile.Name;
        existingProfile.Email = profile.Email;
        existingProfile.Phone = profile.Phone;
        existingProfile.Address = profile.Address;
        existingProfile.Title = profile.Title;
        existingProfile.Background = profile.Background;
        existingProfile.Roles = AsSet(profile.Roles);

        // Clearance is the admin's record about this user, so a non-admin update carries the stored
        // map through untouched instead of the (ignored) one in the body.
        if (isAdmin)
        {
            existingProfile.Permission = profile.Permission;
        }

        // Backfills rows written before the column existed; otherwise createdAt never moves.
        existingProfile.CreatedAt ??= now;
        existingProfile.UpdatedAt = now;

        await _context.SaveChangesAsync();
        return Ok(existingProfile);
    }

    // DELETE: api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
    [HttpDelete("{ss58address}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteProfileAsync(string ss58address)
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
            $"{BasePath}/{ss58address}",
            new EmptyPayloadBody());

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        // Check authorization: only admin or profile owner can delete
        if (address != ss58address && !_signatureValidator.IsAdmin(address))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "You can only delete your own profile");
        }

        var profile = await _context.Profiles.FindAsync(ss58address);
        if (profile == null)
        {
            return NotFound();
        }

        _context.Profiles.Remove(profile);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    // POST: api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W/image
    // Images up to 25MB are supported; the extra 1MB covers multipart encoding overhead.
    // NOTE: any reverse proxy in front of the API (e.g. nginx client_max_body_size)
    // must allow at least the same request size, or uploads fail with 413 before
    // ever reaching this endpoint.
    [HttpPost("{ss58address}/image")]
    [RequestSizeLimit(ImageUploads.RequestSizeLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageUploads.RequestSizeLimit)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<string>> UploadImageAsync(string ss58address, IFormFile image)
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
            $"{BasePath}/{ss58address}/image",
            new EmptyPayloadBody());

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        // Check authorization: can only upload image for own profile or is admin
        if (address != ss58address && !_signatureValidator.IsAdmin(address))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "You can only upload image for your own profile");
        }

        // Check if profile exists
        var profile = await _context.Profiles.FindAsync(ss58address);
        if (profile == null)
        {
            return NotFound();
        }

        // Upload to S3
        if (image.Length > 0)
        {
            var fileName = Path.GetFileName(image.FileName);
            if (!ImageUploads.TryGetContentType(Path.GetExtension(fileName), out var contentType))
            {
                return BadRequest(ImageUploads.UnsupportedTypeMessage);
            }

            using (var stream = image.OpenReadStream())
            {
                // Key is derived from the filename only, so uploading a file with the
                // same name rewrites the existing object instead of creating a new one
                var key = $"profiles/{ss58address}/{fileName}";
                var url = await _s3Service.UploadImageAsync(ImageBucket, key, stream, contentType);

                // Update profile picture URL
                profile.ProfilePicture = url;
                profile.UpdatedAt = Timestamps.UtcNow();
                await _context.SaveChangesAsync();

                return Ok(url);
            }
        }

        return BadRequest("No image file provided");
    }

    /// <summary>
    /// The refusal message for a body that cannot be stored, or null when it is fine. Runs after the
    /// signature check, so the caller is known by the time anything is reported back.
    /// </summary>
    /// <param name="userId">
    /// The address the profile belongs to — the body's own for a create, the route's for an update.
    /// </param>
    private static string? Invalid(Profile profile, string userId) =>
        FieldValidation.FirstFailure(
            profile.UserId is not null && profile.UserId != userId
                ? "userId must equal the profile's wallet address"
                : null,
            FieldValidation.TooLong("name", profile.Name, 128),
            FieldValidation.TooLong("email", profile.Email, 256),
            !string.IsNullOrWhiteSpace(profile.Email) && !FieldValidation.IsEmail(profile.Email)
                ? "email must be a valid email address"
                : null,
            FieldValidation.TooLong("phone", profile.Phone, 32),
            FieldValidation.TooLong("address", profile.Address, 512),
            FieldValidation.TooLong("title", profile.Title, 128),
            FieldValidation.TooLong("background", profile.Background, 2000));

    /// <summary>
    /// Roles are a set in the schema this ports, and JSON has no set type. Collapsing duplicates on
    /// the way in keeps <c>["investor","investor"]</c> from being stored and read back as two roles.
    /// </summary>
    private static List<UserRole>? AsSet(List<UserRole>? roles) => roles?.Distinct().ToList();
}
