using Microsoft.AspNetCore.Mvc;
using XcavateProfile.Client;
using XcavateProfileApi.Models;
using XcavateProfileApiClient.Signing;
using XcavateProfileApi.Services;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// Server-side signing for the realXmarket Solana marketplace program. The rent collector is
/// the fee payer for certain marketplace transactions (buy/claim of property shares) but is
/// never an on-chain instruction signer — so the investor's wallet cannot co-sign on its own
/// machine. This endpoint holds the rent collector's key (in the server's environment) and
/// returns its signature over a message the client has already assembled, after checking the
/// message is one of the expected marketplace transactions and that the investor named by the
/// X-SS58-Address header is a required signer of it.
/// </summary>
[ApiController]
[Route("api/marketplace")]
public class MarketplaceController : ControllerBase
{
    /// <summary>
    /// Address-format validator only (<see cref="ISignatureScheme.CanVerify"/>). Gating the
    /// header address here keeps the investor address we later check against the message a
    /// well-formed Solana base58 key.
    /// </summary>
    private static readonly SolanaSignatureScheme SolanaFormat = new();

    private readonly MarketplaceRentCollectorSigningService _rentCollectorService;

    public MarketplaceController(MarketplaceRentCollectorSigningService rentCollectorService)
    {
        _rentCollectorService = rentCollectorService;
    }

    /// <summary>
    /// Signs a serialized Solana message with the rent collector key. The caller signs the
    /// same message as the fee payer's wallet; the response is the 64-byte signature in base58.
    /// The request itself is not authenticated: the message validation only ever signs a
    /// message that also requires the investor's own on-chain signature, so the rent
    /// collector's half is useless to a caller who does not hold the investor key.
    /// </summary>
    // POST: api/marketplace/rent-collector-signature
    [HttpPost("rent-collector-signature")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<RentCollectorSignatureResponse> RentCollectorSignature(
        [FromBody] RentCollectorSignatureRequest body)
    {
        // The investor the signature is for. Unauthenticated - the request carries no
        // signature any more - but still required: the signing service refuses any
        // message that does not need this key's on-chain signature too.
        var address = Request.Headers["X-SS58-Address"].FirstOrDefault();

        if (string.IsNullOrEmpty(address))
        {
            return BadRequest("Missing X-SS58-Address header");
        }

        if (!SolanaFormat.CanVerify(address))
        {
            return BadRequest("X-SS58-Address must be a valid Solana address");
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
