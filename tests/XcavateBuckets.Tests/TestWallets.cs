using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi.Model.Types;
using static Substrate.NetApi.Mnemonic;

namespace XcavateBuckets.Tests;

/// <summary>
/// Deterministic sr25519 accounts for the REST fixtures: the same <paramref name="fill"/> byte
/// always yields the same address, so a test that needs "some other wallet" can name one without
/// hard-coding an address.
/// </summary>
internal static class TestWallets
{
    public static Account Substrate(byte fill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(fill, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "RestTests" }, KeyType.Sr25519)
            .Account;
    }
}
