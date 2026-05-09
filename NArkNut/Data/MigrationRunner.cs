using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace cdk_arkade_payment_processor.Configuration;

public sealed class MigrationRunner(
    IConfiguration configuration,
    IDbContextFactory<ProcessorDbContext> dbFactory,
    ILogger<MigrationRunner> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseExistsAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("No database migrations to apply");
            return;
        }

        logger.LogInformation("Applying {Count} database migration(s)", pending.Count);
        await db.Database.MigrateAsync(cancellationToken);
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("Ark");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(csb.Database))
            return;

        var targetDatabase = csb.Database;
        var adminCsb = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };

        await using var connection = new NpgsqlConnection(adminCsb.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var existsCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        existsCmd.Parameters.AddWithValue("name", targetDatabase);
        var exists = await existsCmd.ExecuteScalarAsync(cancellationToken) is not null;

        if (exists)
            return;

        var escaped = targetDatabase.Replace("\"", "\"\"");
        await using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{escaped}\"", connection);
        await createCmd.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Created database {Database}", targetDatabase);
    }
}
