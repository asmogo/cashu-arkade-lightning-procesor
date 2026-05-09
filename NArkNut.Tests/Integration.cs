using DotNut;
using DotNut.Abstractions;
using DotNut.Api;

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
        dynamic? mintHandler = null;
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
        await DockerHelper.WaitForLndInvoicePaidByBolt11(
            mintQuote.Request,
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500));
        IEnumerable<DotNut.Proof>? proofs = null;
        for (var i = 0; i < 120; i++)
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

        var meltInvoice = await DockerHelper.CreateLndInvoice(1000);
        var meltQuote = await wallet
            .CreateMeltQuote()
            .WithInvoice(meltInvoice)
            .ProcessAsyncBolt11();

        await DockerHelper.WaitForLndInvoicePaidByBolt11(
            meltInvoice,
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500));
        var change = await meltQuote.Melt(enumerable);
        Assert.NotNull(change);
        
    }

    private static async Task EnsureFulmineLiquidity()
    {
        using var http = new HttpClient();
        http.BaseAddress = new Uri(FulmineUrl);
        http.Timeout = TimeSpan.FromSeconds(8);

        try
        {
            await http.GetAsync("/api/v1/settle");
        }
        catch
        {
            // best effort; readiness is validated by succeeding quote creation
        }

        for (var i = 0; i < 6; i++)
            await DockerHelper.MineBlocks();

        await Task.Delay(5000);

        try
        {
            await http.GetStringAsync("/api/v1/balance");
        }
        catch
        {
            // best effort, quote creation retry handles transient readiness
        }
    }
}
