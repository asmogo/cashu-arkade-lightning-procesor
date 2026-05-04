namespace cdk_arkade_payment_processor.Configuration;

public sealed class ProcessorOptions
{
    public string WalletId { get; init; } = "arkade-mint";

    public string Unit { get; init; } = "sat";

    public string? FundingAddress { get; init; }

    public string? WalletSecret { get; init; }
}

public sealed class NbxplorerOptions
{
    public string Uri { get; init; } = "http://localhost:24444/";

    public string Network { get; init; } = "Regtest";
}
