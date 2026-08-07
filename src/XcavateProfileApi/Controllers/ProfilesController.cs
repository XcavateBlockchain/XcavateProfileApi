using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;
using XcavateProfileApi.Data;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Services;
using XcavateProfileApiClient;

namespace XcavateProfileApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProfilesController : ControllerBase
{
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
    [HttpGet("nickname/{nickname}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Profile>> GetProfileByNicknameAsync(string nickname)
    {
        var profile = await _context.Profiles.FirstOrDefaultAsync(p => p.Nickname == nickname);
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
            "/api/profiles",
            profile);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
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

        // Check nickname uniqueness
        if (!string.IsNullOrEmpty(profile.Nickname) &&
            await _context.Profiles.AnyAsync(p => p.Nickname == profile.Nickname))
        {
            return BadRequest("Nickname already exists");
        }

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
            $"/api/profiles/{ss58address}",
            profile);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        // Check authorization: can only update own profile or is admin
        if (address != ss58address && !_signatureValidator.IsAdmin(address))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "You can only update your own profile");
        }

        var existingProfile = await _context.Profiles.FindAsync(ss58address);

        // Check nickname uniqueness if nickname is being set or changed
        if (!string.IsNullOrEmpty(profile.Nickname) && profile.Nickname != existingProfile?.Nickname)
        {
            if (await _context.Profiles.AnyAsync(p => p.Nickname == profile.Nickname && p.Ss58Address != ss58address, CancellationToken.None))
            {
                return BadRequest("Nickname already exists");
            }
        }

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
                X25519Key = profile.X25519Key
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
            $"/api/profiles/{ss58address}",
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
    [RequestSizeLimit(26 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 26 * 1024 * 1024)]
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
            $"/api/profiles/{ss58address}/image",
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
            // The bucket serves objects publicly, so the Content-Type must never come
            // from the client (an attacker could store text/html for XSS/phishing).
            // Derive it from the extension via an image-only allowlist instead.
            var fileName = Path.GetFileName(image.FileName);
            var extension = Path.GetExtension(fileName);
            if (!AllowedImageTypes.TryGetValue(extension, out var contentType))
            {
                return BadRequest("Unsupported image type. Allowed: " + string.Join(", ", AllowedImageTypes.Keys));
            }

            using (var stream = image.OpenReadStream())
            {
                // Key is derived from the filename only, so uploading a file with the
                // same name rewrites the existing object instead of creating a new one
                var key = $"profiles/{ss58address}/{fileName}";
                var url = await _s3Service.UploadImageAsync("xcavate-profile", key, stream, contentType);

                // Update profile picture URL
                profile.ProfilePicture = url;
                await _context.SaveChangesAsync();

                return Ok(url);
            }
        }

        return BadRequest("No image file provided");
    }

    // SVG is deliberately excluded: it can embed scripts and the bucket serves
    // objects publicly.
    private static readonly Dictionary<string, string> AllowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };
}
