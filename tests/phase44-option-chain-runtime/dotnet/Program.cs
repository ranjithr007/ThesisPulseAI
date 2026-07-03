using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ThesisPulse.Signal.Service;

var services = new ServiceCollection();
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OptionChainRuntime:Enabled"] = "false",
        ["OptionChainRuntime:PersistenceProvider"] = "SqlServer",
        ["OptionChainRuntime:MaximumAgeSeconds"] = "420",
        ["OptionChainRuntime:MinimumConfidence"] = "0",
        ["OptionChainRuntime:CommandTimeoutSeconds"] = "30",
    })
    .Build();

services.AddOptionChainFusionRuntime(configuration);
await using var provider = services.BuildServiceProvider();

Assert(provider.GetRequiredService<IOptionChainIntelligenceOutputStore>() is not null, "store registration");
Assert(provider.GetRequiredService<IOptionChainFusionEvidenceProvider>() is not null, "evidence provider registration");
Assert(provider.GetRequiredService<OptionChainFusionRequestComposer>() is not null, "composer registration");
Assert(provider.GetRequiredService<OptionChainFusionRuntimeDiagnostics>() is not null, "diagnostics registration");

var health = provider.GetRequiredService<HealthCheckService>();
var report = await health.CheckHealthAsync();
Assert(report.Status == HealthStatus.Healthy, "disabled runtime health");

var diagnostics = provider.GetRequiredService<OptionChainFusionRuntimeDiagnostics>();
var snapshot = diagnostics.Snapshot(new OptionChainFusionEvidenceResult(
    null,
    new[] { "OPTION_CHAIN_WORKFLOW_STALE" },
    Array.Empty<string>(),
    Guid.NewGuid(),
    1,
    Array.Empty<Guid>()));
Assert(snapshot.Outcome == "WARNING_ONLY", "warning-only diagnostics");
Assert(!snapshot.Enabled, "disabled flag");

var invalid = new OptionChainRuntimeOptions
{
    Enabled = true,
    PersistenceProvider = "SqlServer",
    ConnectionString = "",
};
var failedClosed = false;
try
{
    invalid.Validate();
}
catch (ArgumentException)
{
    failedClosed = true;
}
Assert(failedClosed, "missing SQL connection must fail closed");

Console.WriteLine("PASS: Phase 4.4 option-chain runtime acceptance");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
