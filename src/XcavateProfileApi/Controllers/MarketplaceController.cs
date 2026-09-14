using Microsoft.AspNetCore.Mvc;
using XcavateProfile.Client;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Models;
using XcavateProfileApi.Swagger;
using XcavateProfileApiClient.Signing;
using XcavateProfileApi.Services;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// Server-side signing for the realXmarket Solana marketplace program. The rent collector is
/// the fee payer for certain marketplace transactions (buy/claim of property shares) but is
/// never an on-chain instruction signer — so the investor's wallet cannot co-sign on its own
/// machine. This endpoint holds the rent collector's key (in the server's environment) and
/// returns its signature over a message the client has already assembled, after checking the
/// message is one of the expected marketplace transactions and that the authenticated investor
/// is a required signer of it.
/// </summary>
[ApiController]
[Route("api/marketplace")]
public class MarketplaceController : ControllerBase
{
    /// <summary>
    /// Address-format validator only (<see cref="ISignatureScheme.CanVerify"/>). The validator
    /// pins the signature scheme from the header address, so gating it here keeps the request
    /// in the Solana signing path and ensures the investor address we later check against the
    /// message is a well-formed base58 key.
    /// </summary>
    private static readonly SolanaSignatureScheme SolanaFormat = new();

    private readonly ISignatureValidator _signatureValidator;
    private readonly MarketplaceRentCollectorSigningService _rentCollectorService;

    public MarketplaceController(
        ISignatureValidator signatureValidator,
        MarketplaceRentCollectorSigningService rentCollectorService)
    {
        _signatureValidator = signatureValidator;
        _rentCollectorService = rentCollectorService;
    }

    /// <summary>
    /// Signs a serialized Solana message with the rent collector key. The caller signs the
    /// same message as the fee payer's wallet; the response is the 64-byte signature in base58.
    /// </summary>
    [SignedRequest]
    // POST: api/marketplace/rent-collector-signature
    [HttpPost("rent-collector-signature")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RentCollectorSignatureResponse>> RentCollectorSignatureAsync(
        [FromBody] RentCollectorSignatureRequest body)
    {
        // Verify authentication headers from request
        var address = Request.Headers["X-SS58-Address"].FirstOrDefault();
        var signature = Request.Headers["X-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-Timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return Unauthorized("Missing authentication headers");
        }

        // The authenticated address must be a Solana base58 public key — it is the investor
        // and the validator pins the scheme to Solana from it.
        if (!SolanaFormat.CanVerify(address))
        {
            return BadRequest("X-SS58-Address must be a valid Solana address");
        }

        // Validate signature
        var result = await _signatureValidator.ValidateAsync(
            address,
            signature,
            timestamp,
            "POST",
            "/api/marketplace/rent-collector-signature",
            body);

        if (!result.IsValid)
        {
            return Unauthorized(result.Error);
        }

        byte[] message;
        try
        {
            message = Convert.FromBase64String(body.Message);
        }
        catch (FormatException)
        {
            return BadRequest("message must be base64-encoded bytes");
        }

        var (outcome, error, signatureBase58) = _rentCollectorService.ValidateAndSign(message, address);
        switch (outcome)
        {
            case MarketplaceRentCollectorSigningService.Outcome.NotConfigured:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
            case MarketplaceRentCollectorSigningService.Outcome.Error:
                return BadRequest(error);
            case MarketplaceRentCollectorSigningService.Outcome.Signed:
                return Ok(new RentCollectorSignatureResponse { Signature = signatureBase58! });
            default:
                return StatusCode(StatusCodes.Status500InternalServerError, "Unexpected signing outcome");
        }
    }
}
