using cdk_arkade_payment_processor.Services;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Services;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Swaps.Boltz.Client;

namespace cdk_arkade_payment_processor.Configuration;

public static class ServiceCollectionExtensions
{
    public static void AddArkadePaymentProcessor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddArkadePaymentProcessorConfiguration(configuration);
        services.AddHttpClient();

        var connectionString = configuration.GetConnectionString("Ark")
                               ?? "Host=localhost;Port=5432;Database=cdk_arkade_processor;Username=postgres;Password=postgres;GSS Encryption Mode=Disable";

        services.AddDbContextFactory<ProcessorDbContext>(options => options.UseNpgsql(connectionString));
        services.AddArkEfCoreStorage<ProcessorDbContext>();

        services.AddArkCoreServices();
        services.AddArkSwapServices();

        services.AddSingleton<IIntentScheduler, SimpleIntentScheduler>();
        services.AddSingleton<ISafetyService, AsyncSafetyService>();
        services.AddSingleton<IChainTimeProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NbxplorerOptions>>().Value;
            return new ChainTimeProvider(ParseNetwork(opts.Network), new Uri(opts.Uri));
        });
        services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
        services.AddSingleton<IAssetManager, AssetManager>();
        services.AddSingleton<IBoardingUtxoProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NbxplorerOptions>>().Value;
            return new NBXplorerBoardingUtxoProvider(ParseNetwork(opts.Network), new Uri(opts.Uri));
        });
        services.AddSingleton<BoardingUtxoSyncService>();

        services.AddSingleton<CachedBoltzClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NArk.Swaps.Boltz.Models.BoltzClientOptions>>();
            return new CachedBoltzClient(new HttpClient(), options);
        });
        services.AddSingleton<BoltzClient>(sp => sp.GetRequiredService<CachedBoltzClient>());
        services.AddSingleton<ArkSwapLightningService>();
        services.AddSingleton<IncomingPaymentEventBus>();
        services.AddGrpc();
    }

    public static void AddArkadePaymentProcessorConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var processorSection = configuration.GetSection("Processor");
        services.Configure<ProcessorOptions>(processorSection);
        var processorOptions = processorSection.Get<ProcessorOptions>() ?? new ProcessorOptions();
        services.Configure<NbxplorerOptions>(configuration.GetRequiredSection("NBXplorer"));

        var networkSection = configuration.GetRequiredSection("ArkNetwork");
        var arkUri = networkSection.GetRequiredSection("ArkUri").Value
                     ?? throw new InvalidOperationException("ArkNetwork:ArkUri is required.");

        var config = new ArkNetworkConfig(
            ArkUri: arkUri,
            ArkadeWalletUri: networkSection["ArkadeWalletUri"],
            BoltzUri: networkSection["BoltzUri"],
            ExplorerUri: networkSection["ExplorerUri"]);

        services.AddArkNetwork(config);

        services.AddSingleton(sp =>
            new ProcessorContext(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProcessorOptions>>().Value,
                sp.GetRequiredService<ArkNetworkConfig>()));
    }

    private static NBitcoin.Network ParseNetwork(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "mainnet" => NBitcoin.Network.Main,
            "testnet" => NBitcoin.Network.TestNet,
            "regtest" => NBitcoin.Network.RegTest,
            "signet" => NBitcoin.Bitcoin.Instance.Signet,
            "mutinynet" => NBitcoin.Bitcoin.Instance.Mutinynet,
            _ => throw new InvalidOperationException($"Unsupported NBXplorer network '{value}'.")
        };
    }
}
