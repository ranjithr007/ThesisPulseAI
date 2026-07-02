using System.Runtime.CompilerServices;

internal static class LifecycleAcceptanceBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var failures = LifecycleAcceptanceTests.Run();
        foreach (var failure in failures)
            Console.Error.WriteLine(failure);
        if (failures.Count > 0)
            throw new InvalidOperationException("Phase 3.3 lifecycle acceptance tests failed.");
    }
}
