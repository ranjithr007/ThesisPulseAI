using System.Runtime.CompilerServices;

internal static class Phase32AcceptanceTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var failures = Phase32AcceptanceTests.RunAsync().GetAwaiter().GetResult();
        if (failures.Count == 0)
            return;

        throw new InvalidOperationException(
            "Phase 3.2 acceptance regressions failed: " + string.Join(" | ", failures));
    }
}
