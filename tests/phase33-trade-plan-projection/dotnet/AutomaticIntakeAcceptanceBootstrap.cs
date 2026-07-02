using System.Runtime.CompilerServices;

internal static class AutomaticIntakeAcceptanceBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var failures = AutomaticIntakeAcceptanceTests.Run();
        foreach (var failure in failures)
            Console.Error.WriteLine(failure);
        if (failures.Count > 0)
            throw new InvalidOperationException("Automatic Trade Plan intake acceptance tests failed.");
    }
}
