using System.Text.Json.Nodes;
using DotNut;
using DotNut.Abstractions;
using DotNut.Api;
using DotNut.ApiModels;

namespace cdk_arkade_payment_processor.Tests;

public class Integration
{
    private const string ProcessorUrl = "http://localhost:8080/";
    private const string MintUrl = "http://localhost:3338";
    private const string FulmineUrl = "http://localhost:7003";

    [Fact]
    public async Task PaymentProcessor_IsReachable()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var request = new HttpRequestMessage(HttpMethod.Get, ProcessorUrl)
        {
            Version = new Version(2, 0),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("payment processor", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegtestLnd_CanCreateInvoice()
    {
        var invoice = await DockerHelper.CreateLndInvoice(amtSats: 1500, expirySecs: 60);
        Assert.False(string.IsNullOrWhiteSpace(invoice));
        Assert.StartsWith("ln", invoice, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task CdkMint_Returns_Info()
    {
        var info = await Wallet
            .Create()
            .WithMint(MintUrl)
            .GetInfo();
        Assert.NotNull(info);
    }

    [Fact]
    public async Task CanRoundtripProofz()
    {
        await EnsureFulmineLiquidity();

        var wallet = Wallet.Create().WithMint(MintUrl);
        const ulong mintAmount = 50_000;
        IMintHandler<PostMintQuoteBolt11Response, List<Proof>> mintHandler = null;
        Exception? lastQuoteError = null;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                mintHandler = await wallet
                    .CreateMintQuote()
                    .WithAmount(mintAmount)
                    .ProcessAsyncBolt11();
                break;
            }
            catch (Exception ex)
            {
                lastQuoteError = ex;
                await EnsureFulmineLiquidity();
                await Task.Delay(1500);
            }
        }

        if (mintHandler is null)
        {
            throw new InvalidOperationException("Could not create mint quote after Fulmine settle retries.", lastQuoteError);
        }

        var mintQuote = mintHandler.GetQuote();
        Assert.NotNull(mintQuote.Request);
        await DockerHelper.PayLndInvoice(mintQuote.Request);

        IEnumerable<DotNut.Proof>? proofs = null;
        for (var i = 0; i < 240; i++)
        {
            try
            {
                proofs = await mintHandler.Mint();
                break;
            }
            catch (CashuProtocolException ex) when (ex.Message.Contains("Quote not paid", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(500);
            }
        }

        if (proofs is null)
        {
            throw new TimeoutException("Mint quote did not transition to paid state in time.");
        }

        var enumerable = proofs as Proof[] ?? proofs.ToArray();
        Assert.Equal(mintAmount, enumerable.Select(p => p.Amount).Aggregate(0UL, (a, b) => a + b));

        var meltInvoice = await DockerHelper.CreateLndInvoice(1000, expirySecs: 120);
        var meltQuote = await wallet
            .CreateMeltQuote()
            .WithInvoice(meltInvoice)
            .ProcessAsyncBolt11();

        var change = await meltQuote.Melt(enumerable);
        Assert.NotNull(change);
        
    }

    private static async Task EnsureFulmineLiquidity(long minBalanceSats = 200_000, int maxAttempts = 10)
    {
        using var http = new HttpClient { BaseAddress = new Uri(FulmineUrl), Timeout = TimeSpan.FromSeconds(15) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var balance = await GetFulmineArkBalance(http);
            if (balance >= minBalanceSats)
                return;

            await FundFulmineBoarding(http);

            for (var i = 0; i < 6; i++)
                await DockerHelper.MineBlocks();
            await Task.Delay(TimeSpan.FromSeconds(2));

            try { await http.GetAsync("/api/v1/settle"); } catch { }

            await Task.Delay(TimeSpan.FromSeconds(15));

            for (var i = 0; i < 6; i++)
                await DockerHelper.MineBlocks();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<long> GetFulmineArkBalance(HttpClient http)
    {
        try
        {
            var json = JsonNode.Parse(await http.GetStringAsync("/api/v1/balance"));
            var value = json?["offchain"] ?? json?["amount"];
            return long.TryParse(value?.ToString(), out var b) ? b : 0;
        }
        catch { return 0; }
    }

    private static async Task FundFulmineBoarding(HttpClient http)
    {
        try
        {
            var json = JsonNode.Parse(await http.GetStringAsync("/api/v1/address"));
            var arkAddress = json?["address"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(arkAddress)) return;

            // Ark address may be in URI format (e.g. "ark:bcrt1p...") — extract the path component
            var onchainAddress = Uri.TryCreate(arkAddress, UriKind.Absolute, out var uri)
                ? uri.AbsolutePath.TrimStart('/')
                : arkAddress;

            await DockerHelper.SendBitcoinToAddress(onchainAddress);
        }
        catch { }
    }
}
