using cdk_arkade_payment_processor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<CdkPaymentProcessorGrpcService>();
app.MapGet("/", () => "CDK Arkade payment processor gRPC server");

app.Run();
