using BTCPayServer.Lightning;
using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NBitcoin;

namespace cdk_arkade_payment_processor.Services;

public sealed class ArkSwapLightningService(
    IClientTransport clientTransport,
    SwapsManagementService swapsManagementService,
    BoltzLimitsValidator boltzLimitsValidator,
    ISwapStorage swapStorage,
    IContractStorage contractStorage,
    ILogger<ArkSwapLightningService> logger)
{
    public async Task<LightningInvoice> CreateInvoice(string walletId, long amountSats, string description, TimeSpan expiry, CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var amount = LightMoney.Satoshis(amountSats);

        var (isValid, error) = await boltzLimitsValidator.ValidateAmountAsync(amountSats, isReverse: true, ct);
        if (!isValid)
            throw new PaymentValidationException(error ?? "Invalid reverse swap amount");

        var request = new CreateInvoiceParams(amount, description, expiry);
        var bolt11 = await swapsManagementService.InitiateReverseSwap(walletId, request, ct);

        var swap = await GetSwapByInvoice(walletId, bolt11, ct)
                   ?? throw new InvalidOperationException("Failed to locate created reverse swap");
        var contract = await GetContract(walletId, swap.ContractScript, ct);
        return MapInvoice(swap, contract, serverInfo.Network);
    }

    public async Task<LightningPayment> PayInvoice(string walletId, string bolt11, CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var pr = BOLT11PaymentRequest.Parse(bolt11, serverInfo.Network);
        var amountSats = (long)(pr.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);

        var (isValid, error) = await boltzLimitsValidator.ValidateAmountAsync(amountSats, isReverse: false, ct);
        if (!isValid)
        {
            throw new PaymentValidationException(error ?? "Invalid submarine swap amount");
        }

        await swapsManagementService.InitiateSubmarineSwap(walletId, pr, autoPay: true, ct);

        var swap = await GetSwapByInvoice(walletId, bolt11, ct)
                   ?? throw new InvalidOperationException("Failed to locate created submarine swap");
        var contract = await GetContract(walletId, swap.ContractScript, ct);
        return MapPayment(swap, contract, serverInfo.Network);
    }

    public async Task<LightningInvoice?> GetIncomingById(string walletId, string id, CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var swaps = await swapStorage.GetSwaps(walletIds: [walletId], swapIds: [id], cancellationToken: ct);
        var swap = swaps.FirstOrDefault();
        if (swap is null || swap.SwapType != ArkSwapType.ReverseSubmarine) return null;
        var contract = await GetContract(walletId, swap.ContractScript, ct);
        return MapInvoice(swap, contract, serverInfo.Network);
    }

    public async Task<LightningInvoice?> GetIncomingByHash(string walletId, string hash, CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var candidateHashes = CandidateHashes(hash);
        var swaps = await swapStorage.GetSwaps(walletIds: [walletId], swapTypes: [ArkSwapType.ReverseSubmarine], hashes: candidateHashes, cancellationToken: ct);
        var swap = swaps.FirstOrDefault();
        if (swap is null) return null;
        var contract = await GetContract(walletId, swap.ContractScript, ct);
        return MapInvoice(swap, contract, serverInfo.Network);
    }

    public async Task<LightningPayment?> GetOutgoingById(string walletId, string id, CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var swaps = await swapStorage.GetSwaps(walletIds: [walletId], swapIds: [id], cancellationToken: ct);
        var swap = swaps.FirstOrDefault();
        if (swap is null || swap.SwapType != ArkSwapType.Submarine) return null;
        var contract = await GetContract(walletId, swap.ContractScript, ct);
        return MapPayment(swap, contract, serverInfo.Network);
    }

    public async Task<LightningPayment?> GetOutgoingByHash(string walletId, string hash, CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var candidateHashes = CandidateHashes(hash);
        var swaps = await swapStorage.GetSwaps(walletIds: [walletId], swapTypes: [ArkSwapType.Submarine], hashes: candidateHashes, cancellationToken: ct);
        var swap = swaps.FirstOrDefault();
        if (swap is null) return null;
        var contract = await GetContract(walletId, swap.ContractScript, ct);
        return MapPayment(swap, contract, serverInfo.Network);
    }

    public async Task<IReadOnlyList<LightningInvoice>> GetNewlyPaidIncoming(string walletId, DateTimeOffset since, CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var swaps = await swapStorage.GetSwaps(walletIds: [walletId], swapTypes: [ArkSwapType.ReverseSubmarine], status: [ArkSwapStatus.Settled], cancellationToken: ct);
        var recent = swaps.Where(s => s.UpdatedAt >= since).ToArray();

        var result = new List<LightningInvoice>(recent.Length);
        foreach (var swap in recent)
        {
            var contract = await GetContract(walletId, swap.ContractScript, ct);
            result.Add(MapInvoice(swap, contract, serverInfo.Network));
        }

        return result;
    }

    private async Task<ArkSwap?> GetSwapByInvoice(string walletId, string invoice, CancellationToken ct)
    {
        var swaps = await swapStorage.GetSwaps(walletIds: [walletId], invoices: [invoice], cancellationToken: ct);
        return swaps.FirstOrDefault();
    }

    private async Task<ArkContractEntity?> GetContract(string walletId, string script, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        var contracts = await contractStorage.GetContracts(walletIds: [walletId], scripts: [script], cancellationToken: ct);
        return contracts.FirstOrDefault();
    }

    private static LightningInvoice MapInvoice(ArkSwap swap, ArkContractEntity? contractEntity, Network network)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network);
        var status = swap.Status switch
        {
            ArkSwapStatus.Settled => LightningInvoiceStatus.Paid,
            ArkSwapStatus.Failed => LightningInvoiceStatus.Expired,
            ArkSwapStatus.Pending => LightningInvoiceStatus.Unpaid,
            _ => LightningInvoiceStatus.Unpaid
        };

        VHTLCContract? contract = null;
        if (contractEntity is not null)
        {
            contract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, network) as VHTLCContract;
        }

        return new LightningInvoice
        {
            Id = swap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = status,
            ExpiresAt = bolt11.ExpiryDate,
            BOLT11 = swap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            PaidAt = status == LightningInvoiceStatus.Paid ? swap.UpdatedAt.ToUniversalTime() : null,
            Preimage = contract?.Preimage is null ? null : Convert.ToHexString(contract.Preimage).ToLowerInvariant()
        };
    }

    private static LightningPayment MapPayment(ArkSwap swap, ArkContractEntity? contractEntity, Network network)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network);
        var status = swap.Status switch
        {
            ArkSwapStatus.Settled => LightningPaymentStatus.Complete,
            ArkSwapStatus.Failed => LightningPaymentStatus.Failed,
            ArkSwapStatus.Pending => LightningPaymentStatus.Pending,
            _ => LightningPaymentStatus.Unknown
        };

        VHTLCContract? contract = null;
        if (contractEntity is not null)
        {
            contract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, network) as VHTLCContract;
        }

        return new LightningPayment
        {
            Id = swap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = status,
            BOLT11 = swap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            Preimage = contract?.Preimage is null ? null : Convert.ToHexString(contract.Preimage).ToLowerInvariant(),
            CreatedAt = swap.CreatedAt,
            AmountSent = LightMoney.Satoshis(swap.ExpectedAmount)
        };
    }

    private static string[] CandidateHashes(string hash)
    {
        var normalized = hash.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        if (TryReverseHexByByte(normalized, out var reversed) && !string.Equals(reversed, normalized, StringComparison.Ordinal))
            return [normalized, reversed];

        return [normalized];
    }

    private static bool TryReverseHexByByte(string hex, out string reversedHex)
    {
        reversedHex = string.Empty;
        if (hex.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(hex);
            Array.Reverse(bytes);
            reversedHex = Convert.ToHexString(bytes).ToLowerInvariant();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
