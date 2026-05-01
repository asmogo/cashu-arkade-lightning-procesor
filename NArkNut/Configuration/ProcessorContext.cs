using NArk.Hosting;

namespace cdk_arkade_payment_processor.Configuration;

public sealed class ProcessorContext
{
    public ProcessorContext(ProcessorOptions options, ArkNetworkConfig networkConfig)
    {
        Options = options;
        NetworkConfig = networkConfig;
    }

    public ProcessorOptions Options { get; }

    public ArkNetworkConfig NetworkConfig { get; }
}
