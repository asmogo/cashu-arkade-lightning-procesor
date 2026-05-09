namespace cdk_arkade_payment_processor.Configuration;

internal static class NetworkParser
{
    internal static NBitcoin.Network Parse(string value) =>
        value.ToLowerInvariant() switch
        {
            "mainnet" => NBitcoin.Network.Main,
            "testnet" => NBitcoin.Network.TestNet,
            "regtest" => NBitcoin.Network.RegTest,
            "signet" => NBitcoin.Bitcoin.Instance.Signet,
            "mutinynet" => NBitcoin.Bitcoin.Instance.Mutinynet,
            _ => throw new InvalidOperationException($"Unsupported NBXplorer network '{value}'.")
        };
}
