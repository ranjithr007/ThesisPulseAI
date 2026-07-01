var failures = await Phase32AcceptanceTests.RunAsync();
foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;
