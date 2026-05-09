namespace cdk_arkade_payment_processor.Services;

public sealed class PaymentValidationException(string message) : Exception(message);
