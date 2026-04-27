using Grpc.Core;
using Proto = global::CdkPaymentProcessor;

namespace cdk_arkade_payment_processor.Services;

public sealed class CdkPaymentProcessorGrpcService : Proto.CdkPaymentProcessor.CdkPaymentProcessorBase
{
    public override Task<Proto.SettingsResponse> GetSettings(
        Proto.EmptyRequest request,
        ServerCallContext context)
    {
        throw Unimplemented();
    }

    public override Task<Proto.CreatePaymentResponse> CreatePayment(
        Proto.CreatePaymentRequest request,
        ServerCallContext context)
    {
        throw Unimplemented();
    }

    public override Task<Proto.PaymentQuoteResponse> GetPaymentQuote(
        Proto.PaymentQuoteRequest request,
        ServerCallContext context)
    {
        throw Unimplemented();
    }

    public override Task<Proto.MakePaymentResponse> MakePayment(
        Proto.MakePaymentRequest request,
        ServerCallContext context)
    {
        throw Unimplemented();
    }

    public override Task<Proto.CheckIncomingPaymentResponse> CheckIncomingPayment(
        Proto.CheckIncomingPaymentRequest request,
        ServerCallContext context)
    {
        throw Unimplemented();
    }

    public override Task<Proto.MakePaymentResponse> CheckOutgoingPayment(
        Proto.CheckOutgoingPaymentRequest request,
        ServerCallContext context)
    {
        throw Unimplemented();
    }

    public override Task WaitPaymentEvent(
        Proto.EmptyRequest request,
        IServerStreamWriter<Proto.PaymentEventResponse> responseStream,
        ServerCallContext context)
    {
        throw Unimplemented();
    }

    private static RpcException Unimplemented() =>
        new(new Status(StatusCode.Unimplemented, "Boltz integration is not wired yet."));
}
