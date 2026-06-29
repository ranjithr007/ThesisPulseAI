using ThesisPulse.DatabaseMigrator;

var tests = new (string Name, Action Execute)[]
{
    ("checksum_normalizes_line_endings", ChecksumNormalizesLineEndings),
    ("go_splits_batches_and_repeats", GoSplitsBatchesAndRepeats),
    ("go_inside_multiline_string_is_not_separator", GoInsideMultilineStringIsNotSeparator),
    ("discovery_orders_contiguous_scripts", DiscoveryOrdersContiguousScripts),
    ("discovery_rejects_sequence_gap", DiscoveryRejectsSequenceGap),
    ("discovery_rejects_invalid_filename", DiscoveryRejectsInvalidFilename)
};

var failureCount = 0;
foreach (var test in tests)
{
    try
    {
        test.Execute();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failureCount++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

if (failureCount > 0)
{
    Console.Error.WriteLine($"{failureCount} migrator test(s) failed.");
    return 1;
}

Console.WriteLine($"All {tests.Length} migrator tests passed.");
return 0;

static void ChecksumNormalizesLineEndings()
{
    const string lf = "SELECT 1;\nGO\nSELECT 2;\n";
    const string crlf = "SELECT 1;\r\nGO\r\nSELECT 2;\r\n";

    var left = MigrationScript.ComputeChecksum(MigrationScript.NormalizeText(lf));
    var right = MigrationScript.ComputeChecksum(MigrationScript.NormalizeText(crlf));

    AssertEqual(left, right, "Checksums must be stable across LF and CRLF checkouts.");
}

static void GoSplitsBatchesAndRepeats()
{
    const string script = """
        SELECT 'GO';
        GO
        /* GO inside this block comment is not a separator.
        GO
        */
        SELECT 2;
        GO 2 -- repeat the second batch
        """;

    var batches = SqlBatchSplitter.Split(script);

    AssertEqual(3, batches.Count, "Expected one first batch plus two repetitions.");
    AssertTrue(batches[0].Text.Contains("SELECT 'GO'", StringComparison.Ordinal), "First batch was not preserved.");
    AssertEqual(1, batches[1].RepeatIndex, "First repeated batch should have repeat index 1.");
    AssertEqual(2, batches[2].RepeatIndex, "Second repeated batch should have repeat index 2.");
    AssertEqual(batches[1].Text, batches[2].Text, "Repeated GO batches must be identical.");
}

static void GoInsideMultilineStringIsNotSeparator()
{
    const string script = """
        SELECT 'first line
        GO
        last line';
        GO
        SELECT 2;
        """;

    var batches = SqlBatchSplitter.Split(script);

    AssertEqual(2, batches.Count, "GO inside a multiline string must not split the batch.");
    AssertTrue(batches[0].Text.Contains("last line", StringComparison.Ordinal), "Multiline string was truncated.");
}

static void DiscoveryOrdersContiguousScripts()
{
    using var directory = TemporaryDirectory.Create();
    File.WriteAllText(Path.Combine(directory.Path, "V0002__second.sql"), "SELECT 2;");
    File.WriteAllText(Path.Combine(directory.Path, "V0001__first.sql"), "SELECT 1;");

    var scripts = MigrationScript.Discover(directory.Path);

    AssertEqual(2, scripts.Count, "Expected two migration scripts.");
    AssertEqual(1, scripts[0].Sequence, "V0001 should be first.");
    AssertEqual(2, scripts[1].Sequence, "V0002 should be second.");
}

static void DiscoveryRejectsSequenceGap()
{
    using var directory = TemporaryDirectory.Create();
    File.WriteAllText(Path.Combine(directory.Path, "V0001__first.sql"), "SELECT 1;");
    File.WriteAllText(Path.Combine(directory.Path, "V0003__third.sql"), "SELECT 3;");

    AssertThrows<MigrationValidationException>(
        () => MigrationScript.Discover(directory.Path),
        "A missing V0002 must fail validation.");
}

static void DiscoveryRejectsInvalidFilename()
{
    using var directory = TemporaryDirectory.Create();
    File.WriteAllText(Path.Combine(directory.Path, "V0001-invalid.sql"), "SELECT 1;");

    AssertThrows<MigrationValidationException>(
        () => MigrationScript.Discover(directory.Path),
        "A filename outside the migration convention must fail validation.");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

internal sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"thesispulse-migrator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
