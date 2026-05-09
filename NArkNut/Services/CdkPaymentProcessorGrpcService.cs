using BTCPayServer.Lightning;
using Grpc.Core;
using cdk_arkade_payment_processor.Configuration;
using NArk.Swaps.Models;
using Proto = global::CdkPaymentProcessor;

namespace cdk_arkade_payment_processor.Services;

public sealed class CdkPaymentProcessorGrpcService : Proto.CdkPaymentProcessor.CdkPaymentProcessorBase
{
    private readonly ProcessorContext _context;
    private readonly ArkSwapLightningService _lightning;
    private readonly IncomingPaymentEventBus _incomingPaymentEvents;
    private readonly ILogger<CdkPaymentProcessorGrpcService> _logger;
    private readonly NBitcoin.Network _network;

    public CdkPaymentProcessorGrpcService(
        ProcessorContext context,
        ArkSwapLightningService lightning,
        IncomingPaymentEventBus incomingPaymentEvents,
        Microsoft.Extensions.Options.IOptions<NbxplorerOptions> nbxplorerOptions,
        ILogger<CdkPaymentProcessorGrpcService> logger)
    {
        _context = context;
        _lightning = lightning;
        _incomingPaymentEvents = incomingPaymentEvents;
        _network = NetworkParser.Parse(nbxplorerOptions.Value.Network);
        _logger = logger;
    }

    public override Task<Proto.SettingsResponse> GetSettings(
        Proto.EmptyRequest request,
        ServerCallContext context)
    {
        var response = new Proto.SettingsResponse
        {
            Unit = _context.Options.Unit,
            Bolt11 = new Proto.Bolt11Settings
            {
                Amountless = false,
                InvoiceDescription = true
            }
        };
        return Task.FromResult(response);
    }

    public override async Task<Proto.CreatePaymentResponse> CreatePayment(
        Proto.CreatePaymentRequest request,
        ServerCallContext context)
    {
        try
        {
            var bolt11 = request.Options?.Bolt11 ?? throw BadRequest("Only bolt11 incoming options are supported");
            var amount = bolt11.Amount?.Value ?? 0;
            if (amount == 0) throw BadRequest("Amount is required");

            var expirySeconds = bolt11.HasUnixExpiry
                ? Math.Max(60, (long)bolt11.UnixExpiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                : 3600;

            var invoice = await _lightning.CreateInvoice(
                _context.Options.WalletId,
                (long)amount,
                bolt11.Description ?? string.Empty,
                TimeSpan.FromSeconds(expirySeconds),
                context.CancellationToken);

            var invoiceHash = invoice.PaymentHash
                ?? throw new InvalidOperationException("Created invoice has no payment hash.");

            return new Proto.CreatePaymentResponse
            {
                RequestIdentifier = new Proto.PaymentIdentifier
                {
                    Type = Proto.PaymentIdentifierType.PaymentHash,
                    Hash = invoiceHash
                },
                Request = invoice.BOLT11,
                Expiry = (ulong)invoice.ExpiresAt.ToUnixTimeSeconds()
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreatePayment failed for wallet {WalletId}", _context.Options.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, $"CreatePayment failed: {ex.Message}"));
        }
    }

    public override Task<Proto.PaymentQuoteResponse> GetPaymentQuote(
        Proto.PaymentQuoteRequest request,
        ServerCallContext context)
    {
        if (request.RequestType != Proto.OutgoingPaymentRequestType.Bolt11Invoice)
            throw BadRequest("Only bolt11 outgoing quote is supported");

        var pr = BOLT11PaymentRequest.Parse(request.Request, _network);
        var amount = (ulong)(pr.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
        var paymentHash = pr.PaymentHash?.ToString()
            ?? throw new InvalidOperationException("Invoice has no payment hash.");

        return Task.FromResult(new Proto.PaymentQuoteResponse
        {
            RequestIdentifier = new Proto.PaymentIdentifier
            {
                Type = Proto.PaymentIdentifierType.PaymentHash,
                Hash = paymentHash
            },
            Amount = new Proto.AmountMessage { Value = amount, Unit = _context.Options.Unit },
            Fee = new Proto.AmountMessage { Value = 0, Unit = _context.Options.Unit },
            State = Proto.QuoteState.Issued
        });
    }

    public override async Task<Proto.MakePaymentResponse> MakePayment(
        Proto.MakePaymentRequest request,
        ServerCallContext context)
    {
        var bolt11 = request.PaymentOptions?.Bolt11?.Bolt11;
        if (string.IsNullOrWhiteSpace(bolt11)) throw BadRequest("Only bolt11 outgoing payment is supported");

        var payment = await _lightning.PayInvoice(_context.Options.WalletId, bolt11, context.CancellationToken);
        return MapOutgoing(payment);
    }

    public override async Task<Proto.CheckIncomingPaymentResponse> CheckIncomingPayment(
        Proto.CheckIncomingPaymentRequest request,
        ServerCallContext context)
    {
        var invoice = await ResolveIncoming(request.RequestIdentifier, context.CancellationToken);
        var response = new Proto.CheckIncomingPaymentResponse();

        if (invoice?.Status == LightningInvoiceStatus.Paid)
        {
            var paymentHash = invoice.PaymentHash;
            Proto.PaymentIdentifier identifier;
            string paymentId;
            if (!string.IsNullOrWhiteSpace(paymentHash))
            {
                identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.PaymentHash, Hash = paymentHash };
                paymentId = paymentHash;
            }
            else
            {
                identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.CustomId, Id = invoice.Id };
                paymentId = invoice.Id;
            }

            response.Payments.Add(new Proto.WaitIncomingPaymentResponse
            {
                PaymentIdentifier = identifier,
                PaymentAmount = new Proto.AmountMessage
                {
                    Value = (ulong)(invoice.Amount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                    Unit = _context.Options.Unit
                },
                PaymentId = paymentId
            });
        }

        return response;
    }

    public override async Task<Proto.MakePaymentResponse> CheckOutgoingPayment(
        Proto.CheckOutgoingPaymentRequest request,
        ServerCallContext context)
    {
        var payment = await ResolveOutgoing(request.RequestIdentifier, context.CancellationToken);
        if (payment is null)
            return new Proto.MakePaymentResponse
            {
                Status = Proto.QuoteState.Unknown,
                TotalSpent = new Proto.AmountMessage { Value = 0, Unit = _context.Options.Unit }
            };

        return MapOutgoing(payment);
    }

    public override async Task WaitPaymentEvent(
        Proto.EmptyRequest request,
        IServerStreamWriter<Proto.PaymentEventResponse> responseStream,
        ServerCallContext context)
    {
        await StreamWalletSwapEvents(
            responseStream,
            swap => new Proto.PaymentEventResponse { PaymentReceived = MapIncomingPayment(swap) },
            context.CancellationToken);
    }

    public override async Task WaitIncomingPayment(
        Proto.EmptyRequest request,
        IServerStreamWriter<Proto.WaitIncomingPaymentResponse> responseStream,
        ServerCallContext context)
    {
        await StreamWalletSwapEvents(responseStream, MapIncomingPayment, context.CancellationToken);
    }

    private async Task StreamWalletSwapEvents<T>(
        IServerStreamWriter<T> stream,
        Func<ArkSwap, T> map,
        CancellationToken ct)
    {
        await foreach (var swap in _incomingPaymentEvents.Subscribe(ct))
        {
            if (!string.Equals(swap.WalletId, _context.Options.WalletId, StringComparison.Ordinal))
                continue;
            await stream.WriteAsync(map(swap));
        }
    }

    private Proto.WaitIncomingPaymentResponse MapIncomingPayment(ArkSwap swap)
    {
        var invoice = BOLT11PaymentRequest.Parse(swap.Invoice, _network);
        var paymentHash = invoice.PaymentHash?.ToString() ?? swap.Hash;

        Proto.PaymentIdentifier identifier;
        string paymentId;
        if (!string.IsNullOrWhiteSpace(paymentHash))
        {
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.PaymentHash, Hash = paymentHash };
            paymentId = paymentHash;
        }
        else
        {
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.CustomId, Id = swap.SwapId };
            paymentId = swap.SwapId;
        }

        return new Proto.WaitIncomingPaymentResponse
        {
            PaymentIdentifier = identifier,
            PaymentAmount = new Proto.AmountMessage
            {
                Value = (ulong)(invoice.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                Unit = _context.Options.Unit
            },
            PaymentId = paymentId
        };
    }

    private async Task<LightningInvoice?> ResolveIncoming(Proto.PaymentIdentifier id, CancellationToken ct)
    {
        return id.Type switch
        {
            Proto.PaymentIdentifierType.PaymentHash or
            Proto.PaymentIdentifierType.Bolt12PaymentHash
                => await _lightning.GetIncomingByHash(_context.Options.WalletId, id.Hash, ct),
            _ => await _lightning.GetIncomingById(_context.Options.WalletId, id.Id, ct)
        };
    }

    private async Task<LightningPayment?> ResolveOutgoing(Proto.PaymentIdentifier id, CancellationToken ct)
    {
        return id.Type == Proto.PaymentIdentifierType.PaymentHash
            ? await _lightning.GetOutgoingByHash(_context.Options.WalletId, id.Hash, ct)
            : await _lightning.GetOutgoingById(_context.Options.WalletId, id.Id, ct);
    }

    private Proto.MakePaymentResponse MapOutgoing(LightningPayment payment)
    {
        var state = payment.Status switch
        {
            LightningPaymentStatus.Complete => Proto.QuoteState.Paid,
            LightningPaymentStatus.Pending => Proto.QuoteState.Pending,
            LightningPaymentStatus.Failed => Proto.QuoteState.Failed,
            _ => Proto.QuoteState.Unknown
        };

        var paymentHash = payment.PaymentHash ?? string.Empty;
        var identifierType = string.IsNullOrWhiteSpace(paymentHash)
            ? Proto.PaymentIdentifierType.CustomId
            : Proto.PaymentIdentifierType.PaymentHash;

        var paymentIdentifier = new Proto.PaymentIdentifier
        {
            Type = identifierType
        };
        if (identifierType == Proto.PaymentIdentifierType.PaymentHash)
        {
            paymentIdentifier.Hash = paymentHash;
        }
        else
        {
            paymentIdentifier.Id = payment.Id;
        }

        return new Proto.MakePaymentResponse
        {
            PaymentIdentifier = paymentIdentifier,
            PaymentProof = payment.Preimage ?? string.Empty,
            Status = state,
            TotalSpent = new Proto.AmountMessage
            {
                Value = (ulong)(payment.AmountSent?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                Unit = _context.Options.Unit
            }
        };
    }

    private static RpcException BadRequest(string message)
        => new(new Status(StatusCode.InvalidArgument, message));
}
