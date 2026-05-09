using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace cdk_arkade_payment_processor.Configuration;

public sealed class ProcessorDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ProcessorDbContext>
{
    public ProcessorDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Ark")
            ?? "Host=localhost;Port=5432;Database=cdk_arkade_processor;Username=postgres;Password=postgres;GSS Encryption Mode=Disable";

        var builder = new DbContextOptionsBuilder<ProcessorDbContext>();
        builder.UseNpgsql(connectionString);
        return new ProcessorDbContext(builder.Options);
    }
}
