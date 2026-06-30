using System.Runtime.CompilerServices;

internal static class PublicationTestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var failures = new List<string>();
        PublicationTestSuite.Run(failures);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failures.Count} Market Data publication test(s) failed: " +
                string.Join(" | ", failures));
        }
    }
}
