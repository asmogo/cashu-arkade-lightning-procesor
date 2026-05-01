using cdk_arkade_payment_processor.Configuration;
using cdk_arkade_payment_processor.Services;

var builder = WebApplication.CreateBuilder(args);

LoadDotEnv(builder.Environment.ContentRootPath);

builder.Services.AddArkadePaymentProcessor(builder.Configuration);

var app = builder.Build();

app.MapGrpcService<CdkPaymentProcessorGrpcService>();
app.MapGet("/", () => "CDK Arkade payment processor gRPC server");

app.Run();

static void LoadDotEnv(string rootPath)
{
    var envPath = Path.Combine(rootPath, ".env");
    if (!File.Exists(envPath))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        Environment.SetEnvironmentVariable(key, value);
    }
}
