using BTCPayServer.Lightning;
using Grpc.Core;
using cdk_arkade_payment_processor.Configuration;
using Proto = global::CdkPaymentProcessor;

namespace cdk_arkade_payment_processor.Services;

public sealed class CdkPaymentProcessorGrpcService : Proto.CdkPaymentProcessor.CdkPaymentProcessorBase
{
    private readonly ProcessorContext _context;
    private readonly ArkSwapLightningService _lightning;
    private readonly ILogger<CdkPaymentProcessorGrpcService> _logger;
    private DateTimeOffset _eventCursor = DateTimeOffset.UtcNow;

    public CdkPaymentProcessorGrpcService(
        ProcessorContext context,
        ArkSwapLightningService lightning,
        ILogger<CdkPaymentProcessorGrpcService> logger)
    {
        _context = context;
        _lightning = lightning;
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
                Mpp = true,
                Amountless = false,
                InvoiceDescription = true
            },
            Bolt12 = new Proto.Bolt12Settings
            {
                Amountless = false
            }
        };

        response.Custom["wallet_id"] = _context.Options.WalletId;
        response.Custom["ark_uri"] = _context.NetworkConfig.ArkUri;

        if (!string.IsNullOrWhiteSpace(_context.NetworkConfig.BoltzUri))
            response.Custom["boltz_uri"] = _context.NetworkConfig.BoltzUri;

        if (!string.IsNullOrWhiteSpace(_context.Options.FundingAddress))
            response.Custom["funding_address"] = _context.Options.FundingAddress;

        return Task.FromResult(response);
    }

    public override async Task<Proto.CreatePaymentResponse> CreatePayment(
        Proto.CreatePaymentRequest request,
        ServerCallContext context)
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

        return new Proto.CreatePaymentResponse
        {
            RequestIdentifier = new Proto.PaymentIdentifier
            {
                Type = Proto.PaymentIdentifierType.PaymentId,
                Id = invoice.Id
            },
            Request = invoice.BOLT11,
            Expiry = (ulong)invoice.ExpiresAt.ToUnixTimeSeconds()
        };
    }

    public override Task<Proto.PaymentQuoteResponse> GetPaymentQuote(
        Proto.PaymentQuoteRequest request,
        ServerCallContext context)
    {
        if (request.RequestType != Proto.OutgoingPaymentRequestType.Bolt11Invoice)
            throw BadRequest("Only bolt11 outgoing quote is supported");

        var pr = BOLT11PaymentRequest.Parse(request.Request, NBitcoin.Network.Main);
        var amount = (ulong)(pr.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);

        return Task.FromResult(new Proto.PaymentQuoteResponse
        {
            RequestIdentifier = new Proto.PaymentIdentifier
            {
                Type = Proto.PaymentIdentifierType.QuoteId,
                Id = pr.PaymentHash?.ToString() ?? Guid.NewGuid().ToString("N")
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
            response.Payments.Add(new Proto.WaitIncomingPaymentResponse
            {
                PaymentIdentifier = new Proto.PaymentIdentifier
                {
                    Type = Proto.PaymentIdentifierType.PaymentId,
                    Id = invoice.Id
                },
                PaymentAmount = new Proto.AmountMessage
                {
                    Value = (ulong)(invoice.Amount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                    Unit = _context.Options.Unit
                },
                PaymentId = invoice.Id
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
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var paid = await _lightning.GetNewlyPaidIncoming(_context.Options.WalletId, _eventCursor, context.CancellationToken);
            _eventCursor = DateTimeOffset.UtcNow;

            foreach (var invoice in paid)
            {
                await responseStream.WriteAsync(new Proto.PaymentEventResponse
                {
                    PaymentReceived = new Proto.WaitIncomingPaymentResponse
                    {
                        PaymentIdentifier = new Proto.PaymentIdentifier
                        {
                            Type = Proto.PaymentIdentifierType.PaymentId,
                            Id = invoice.Id
                        },
                        PaymentAmount = new Proto.AmountMessage
                        {
                            Value = (ulong)(invoice.Amount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                            Unit = _context.Options.Unit
                        },
                        PaymentId = invoice.Id
                    }
                });
            }

            await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken);
        }
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

        return new Proto.MakePaymentResponse
        {
            PaymentIdentifier = new Proto.PaymentIdentifier
            {
                Type = Proto.PaymentIdentifierType.PaymentId,
                Id = payment.Id
            },
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
