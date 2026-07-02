var failures = Phase39PortfolioRiskTests.Run();
foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;
