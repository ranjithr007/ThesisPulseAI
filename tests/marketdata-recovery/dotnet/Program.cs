using ThesisPulse.Shared.Infrastructure.MarketData;

var failures = new List<string>();
var options = new MarketDataFreshnessOptions();
options.Validate();
var evaluator = new MarketDataFreshnessEvaluator(options);

RecoveryTestSuite.Run(failures, evaluator);

if (failures.Count != 0)
{
    Console.Error.WriteLine("Market Data recovery tests failed.");
    return 1;
}

Console.WriteLine("Market Data recovery tests passed.");
return 0;
