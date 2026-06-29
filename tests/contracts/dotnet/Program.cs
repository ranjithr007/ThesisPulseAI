using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
var contractsRoot = Path.Combine(repoRoot, "contracts", "v1");
var fixturesRoot = Path.Combine(contractsRoot, "fixtures");
var manifestPath = Path.Combine(fixturesRoot, "manifest.json");

using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
var failures = new List<string>();
var caseCount = 0;

foreach (var testCase in manifestDocument.RootElement.GetProperty("cases").EnumerateArray())
{
    caseCount++;
    var name = testCase.GetProperty("name").GetString() ?? throw new InvalidOperationException("Fixture name is required.");
    var schemaFile = testCase.GetProperty("schema").GetString() ?? throw new InvalidOperationException("Schema path is required.");
    var fixtureFile = testCase.GetProperty("fixture").GetString() ?? throw new InvalidOperationException("Fixture path is required.");
    var expectedValid = testCase.GetProperty("expected_valid").GetBoolean();

    var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(contractsRoot, schemaFile)));
    var instance = JsonNode.Parse(File.ReadAllText(Path.Combine(fixturesRoot, fixtureFile)))
        ?? throw new InvalidOperationException($"Fixture {fixtureFile} is empty.");

    var result = schema.Evaluate(instance, new EvaluationOptions
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true
    });

    if (result.IsValid != expectedValid)
    {
        var detail = result.ToJsonDocument().RootElement.ToString();
        failures.Add($"{name}: {detail}");
        Console.WriteLine($"FAIL {name}: {detail}");
    }
    else
    {
        Console.WriteLine($"PASS {name}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} contract fixture case(s) failed.");
    return 1;
}

Console.WriteLine($"All {caseCount} contract fixture cases passed.");
return 0;

static string FindRepoRoot(string startDirectory)
{
    var current = new DirectoryInfo(startDirectory);

    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "contracts", "v1")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate repository root containing contracts/v1.");
}
