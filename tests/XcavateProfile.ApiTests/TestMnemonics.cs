using System.Linq;
using static Substrate.NetApi.Mnemonic;

namespace XcavateProfile.ApiTests;

/// <summary>
/// Contains known valid BIP39 mnemonics for testing.
/// All mnemonics except <see cref="BaseMnemonic"/> are derived from fixed entropy
/// via <see cref="Substrate.NetApi.Mnemonic.MnemonicFromEntropy"/>, which guarantees
/// a valid BIP39 checksum while staying deterministic across test runs.
/// </summary>
public static class TestMnemonics
{
    /// <summary>
    /// Base mnemonic for standard tests (the well-known Substrate dev phrase)
    /// </summary>
    public const string BaseMnemonic = "bottom drive obey lake curtain smoke basket hold race lonely fit walk";

    private static string FromEntropy(byte fill) =>
        string.Join(" ", MnemonicFromEntropy(Enumerable.Repeat(fill, 16).ToArray(), BIP39Wordlist.English));

    /// <summary>
    /// Valid admin mnemonic. The derived SS58 address must be listed in the
    /// server's ADMIN_ADDRESSES environment variable for admin tests to pass.
    /// </summary>
    public static readonly string AdminMnemonic = FromEntropy(0x01);

    /// <summary>
    /// Valid user1 mnemonic
    /// </summary>
    public static readonly string User1Mnemonic = FromEntropy(0x02);

    /// <summary>
    /// Valid user2 mnemonic
    /// </summary>
    public static readonly string User2Mnemonic = FromEntropy(0x03);

    /// <summary>
    /// Valid user mnemonic
    /// </summary>
    public static readonly string UserMnemonic = FromEntropy(0x04);

    /// <summary>
    /// Valid image mnemonic
    /// </summary>
    public static readonly string ImageMnemonic = FromEntropy(0x05);

    /// <summary>
    /// Valid image2 mnemonic
    /// </summary>
    public static readonly string Image2Mnemonic = FromEntropy(0x06);

    /// <summary>
    /// Valid nick mnemonic
    /// </summary>
    public static readonly string NickMnemonic = FromEntropy(0x07);

    /// <summary>
    /// Valid nick2 mnemonic
    /// </summary>
    public static readonly string Nick2Mnemonic = FromEntropy(0x08);

    /// <summary>
    /// Valid mnemonic for user3
    /// </summary>
    public static readonly string User3Mnemonic = FromEntropy(0x09);

    /// <summary>
    /// Valid mnemonic for user4
    /// </summary>
    public static readonly string User4Mnemonic = FromEntropy(0x0A);

    /// <summary>
    /// Valid mnemonic for zero
    /// </summary>
    public static readonly string ZeroMnemonic = FromEntropy(0x0B);

    /// <summary>
    /// Valid image3 mnemonic
    /// </summary>
    public static readonly string Image3Mnemonic = FromEntropy(0x0C);

    /// <summary>
    /// Valid image4 mnemonic
    /// </summary>
    public static readonly string Image4Mnemonic = FromEntropy(0x0D);

    /// <summary>
    /// Valid image5 mnemonic
    /// </summary>
    public static readonly string Image5Mnemonic = FromEntropy(0x0E);

    /// <summary>
    /// Owns the companies in <see cref="CompanyApiTests"/>
    /// </summary>
    public static readonly string CompanyOwnerMnemonic = FromEntropy(0x10);

    /// <summary>
    /// A second account for <see cref="CompanyApiTests"/>, used as the wallet that owns nothing
    /// </summary>
    public static readonly string CompanyStrangerMnemonic = FromEntropy(0x11);

    /// <summary>
    /// Owns the profile in <see cref="ProfileAttributeApiTests"/>
    /// </summary>
    public static readonly string ProfileAttributesMnemonic = FromEntropy(0x12);
}
