using Substrate.NET.Wallet;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi.Model.Types;

namespace XcavateProfile.ApiTests;

/// <summary>
/// Turns a BIP39 phrase from <see cref="TestMnemonics"/> into a Substrate sr25519
/// <see cref="Account"/>. The Solana counterpart is <see cref="SolanaAccounts"/>.
/// </summary>
public static class MnemonicsModel
{
    private static readonly Meta META = new() { Name = "XcavateProfile.ApiTests" };

    public static Account GetAccountFromMnemonics(string mnemonics)
    {
        var keyring = new Keyring();

        Wallet wallet = keyring.AddFromMnemonic(mnemonics, META, KeyType.Sr25519);

        return wallet.Account;
    }
}
