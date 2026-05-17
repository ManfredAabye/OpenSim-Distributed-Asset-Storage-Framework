namespace Microsoft.Extensions.Options;

// Minimal shim to keep MinIO source buildable when Microsoft.Extensions.Options is not available in bin/.
public interface IOptions<out TOptions>
{
    TOptions Value { get; }
}

public sealed class OptionsWrapper<TOptions> : IOptions<TOptions>
{
    public OptionsWrapper(TOptions value)
    {
        Value = value;
    }

    public TOptions Value { get; }
}

public static class Options
{
    public static IOptions<TOptions> Create<TOptions>(TOptions options)
    {
        return new OptionsWrapper<TOptions>(options);
    }
}
