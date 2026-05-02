using cdk_arkade_payment_processor.Configuration;
using cdk_arkade_payment_processor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

LoadDotEnv(builder.Environment.ContentRootPath);

builder.Services.AddArkadePaymentProcessor(builder.Configuration);

var app = builder.Build();

await EnsureDatabaseCreatedAsync(app.Services);

app.MapGrpcService<CdkPaymentProcessorGrpcService>();
app.MapGet("/", () => "CDK Arkade payment processor gRPC server");

await app.RunAsync();

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

static async Task EnsureDatabaseCreatedAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    await EnsureDatabaseExistsAsync(scope.ServiceProvider);

    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProcessorDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

static async Task EnsureDatabaseExistsAsync(IServiceProvider services)
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("Ark");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return;
    }

    var csb = new NpgsqlConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(csb.Database))
    {
        return;
    }

    var targetDatabase = csb.Database;
    var adminCsb = new NpgsqlConnectionStringBuilder(connectionString)
    {
        Database = "postgres"
    };

    await using var connection = new NpgsqlConnection(adminCsb.ConnectionString);
    await connection.OpenAsync();

    await using var existsCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
    existsCmd.Parameters.AddWithValue("name", targetDatabase);
    var exists = await existsCmd.ExecuteScalarAsync() is not null;

    if (!exists)
    {
        var escaped = targetDatabase.Replace("\"", "\"\"");
        await using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{escaped}\"", connection);
        await createCmd.ExecuteNonQueryAsync();
    }
}
