namespace XWolf.NanoWolf.Tests;

/// <summary>The contract of the stores on the in-memory implementation.</summary>
public sealed class MemoryStoreContractTests : StoreContractTests
{
    protected override Task<StoreSet> CreateAsync()
        => Task.FromResult(new StoreSet(new MemoryUserStore(), new MemorySessionStore(), new MemoryTokenStore(), new MemoryThrottleStore(), new MemorySettingsStore(), () => Task.CompletedTask));
}
