using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;
using XcavateProfileApi.Data;
using XcavateProfileApi.Middleware;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// Registers Polkadot → Solana wallet migrations. A registration is an SS58 address paired with
/// the Solana address it migrates to, and is only accepted when the request carries a valid
/// sr25519 signature from that SS58 address — so every stored pair is proof of intent by the
/// Polkadot wallet's owner. Reads are public, like the rest of the API.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MigrationsController : ControllerBase
{
    /// <summary>
    /// Used purely as address-format validators (<see cref="ISignatureScheme.CanVerify"/>), not
    /// for verification — that stays in <see cref="ISignatureValidator"/>. Reusing the schemes
    /// keeps "what is an SS58 address" and "what is a Solana address" defined in exactly one
    /// place for both the signature path and the body validation here.
    /// </summary>
    private static readonly Sr25519SignatureScheme Sr25519Format = new();
    private static readonly SolanaSignatureScheme SolanaFormat = new();

    private readonly ProfileDbContext _context;
    private readonly ISignatureValidator _signatureValidator;

    public MigrationsController(
        ProfileDbContext context,
        ISignatureValidator signatureValidator)
    {
        _context = context;
        _signatureValidator = signatureValidator;
    }

    // GET: api/migrations
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<WalletMigration>>> GetWalletMigrationsAsync()
    {
        var migrations = await _context.WalletMigrations.ToListAsync();
        return Ok(migrations);
    }

    // GET: api/migrations/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
    [HttpGet("{ss58address}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WalletMigration>> GetWalletMigrationAsync(string ss58address)
    {
        var migration = await _context.WalletMigrations.FindAsync(ss58address);
        if (migration == null)
        {
            return NotFound();
        }
        return Ok(migration);
    }

    // POST: api/migrations
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<WalletMigration>> RegisterWalletMigrationAsync(
        [FromBody] WalletMigration migration)
    {
        // Verify authentication headers from request
        var address = Request.Headers["X-SS58-Address"].FirstOrDefault();
        var signature = Request.Headers["X-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-Timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return Unauthorized("Missing authentication headers");
        }

        // The migrated account must be a checksummed SS58 address. Together with the ownership
        // check below this is also what pins the signature scheme to sr25519: the validator
        // selects the scheme from the header address, and the header address must equal this
        // one — a Solana address can never satisfy both.
        if (!Sr25519Format.CanVerify(migration.Ss58Address))
        {
            return BadRequest("ss58address must be a valid SS58 address");
        }

        // The destination must be a Solana base58 public key.
        if (!SolanaFormat.CanVerify(migration.SolanaAddress))
        {
            return BadRequest("solanaAddress must be a valid Solana base58 address");
        }

        // Validate signature
        var result = await _signatureValidator.ValidateAsync(
            address,
            signature,
            timestamp,
            "POST",
            "/api/migrations",
            migration);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        // Only the owner of the Polkadot account may register its migration
        if (address != migration.Ss58Address)
        {
            return Unauthorized("Can only register a migration for the authenticated address");
        }

        // One registration per Polkadot account
        if (await _context.WalletMigrations.FindAsync(migration.Ss58Address) != null)
        {
            return BadRequest("A migration is already registered for this address");
        }

        _context.WalletMigrations.Add(migration);
        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetWalletMigrationAsync), new { ss58address = migration.Ss58Address }, migration);
    }
}
