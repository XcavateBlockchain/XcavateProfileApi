using Solnet.Wallet;
using SolMnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolWordList = Solnet.Wallet.Bip39.WordList;

namespace XcavateProfile.ApiTests;

/// <summary>
/// Solana counterparts of the personas in <see cref="TestMnemonics"/>. The existing phrases are
/// already valid BIP39 with correct checksums, so Solnet accepts them unchanged and each persona
/// simply gains a second address alongside its SS58 one.
/// </summary>
/// <remarks>
/// Derivation is Solnet's default <c>SeedMode.Ed25519Bip32</c> (m/44'/501'/0'/0'), which is what
/// Phantom and Solflare use, so these addresses match what a wallet would show for the same phrase.
/// </remarks>
public static class SolanaAccounts
{
    public static Account From(string mnemonic) =>
        new Wallet(new SolMnemonic(mnemonic, SolWordList.English)).Account;

    /// <summary>DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b — must be in ADMIN_ADDRESSES.</summary>
    public static Account Admin => From(TestMnemonics.AdminMnemonic);

    /// <summary>AK7AACuihtCk6abEywXtg7sPW2Qh9iYg5C6BA38h9ciE</summary>
    public static Account Base => From(TestMnemonics.BaseMnemonic);

    /// <summary>EkkGCbQ73M3V8NGvLH3o9kYZQTjRKadqFCH95YP4cKJf</summary>
    public static Account User1 => From(TestMnemonics.User1Mnemonic);

    /// <summary>Di2WEEU8vXxbzxe7qKbK23d4dvByPeQjDsrDpWjXd16e</summary>
    public static Account User2 => From(TestMnemonics.User2Mnemonic);
}
