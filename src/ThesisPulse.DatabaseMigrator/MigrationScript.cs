using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ThesisPulse.DatabaseMigrator;

public sealed record MigrationScript(
    int Sequence,
    string Name,
    string Description,
    string FullPath,
    string Content,
    string Checksum)
{
    private static readonly Regex FileNamePattern = new(
        @"^V(?<sequence>\d{4,})__(?<description>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static MigrationScript FromFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var fileName = Path.GetFileName(fullPath);
        var match = FileNamePattern.Match(fileName);
        if (!match.Success)
        {
            throw new MigrationValidationException(
                $"Migration file '{fileName}' does not match V<sequence>__<lower_snake_case_description>.sql.");
        }

        if (!int.TryParse(match.Groups["sequence"].Value, out var sequence) || sequence <= 0)
        {
            throw new MigrationValidationException(
                $"Migration file '{fileName}' has an invalid positive sequence.");
        }

        var content = NormalizeText(File.ReadAllText(fullPath));
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new MigrationValidationException($"Migration file '{fileName}' is empty.");
        }

        return new MigrationScript(
            sequence,
            fileName,
            match.Groups["description"].Value,
            Path.GetFullPath(fullPath),
            content,
            ComputeChecksum(content));
    }

    public static IReadOnlyList<MigrationScript> Discover(string migrationsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsPath);

        var scripts = Directory
            .EnumerateFiles(migrationsPath, "V*.sql", SearchOption.TopDirectoryOnly)
            .Select(FromFile)
            .OrderBy(script => script.Sequence)
            .ThenBy(script => script.Name, StringComparer.Ordinal)
            .ToArray();

        if (scripts.Length == 0)
        {
            throw new MigrationValidationException(
                $"No migration scripts were found in '{Path.GetFullPath(migrationsPath)}'.");
        }

        var duplicateSequence = scripts
            .GroupBy(script => script.Sequence)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSequence is not null)
        {
            throw new MigrationValidationException(
                $"Migration sequence V{duplicateSequence.Key:D4} is used by: " +
                string.Join(", ", duplicateSequence.Select(script => script.Name)));
        }

        var duplicateName = scripts
            .GroupBy(script => script.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new MigrationValidationException(
                $"Duplicate migration filename detected: {duplicateName.Key}");
        }

        for (var index = 0; index < scripts.Length; index++)
        {
            var expectedSequence = index + 1;
            if (scripts[index].Sequence != expectedSequence)
            {
                throw new MigrationValidationException(
                    $"Migration history must be contiguous from V0001. Expected V{expectedSequence:D4} but found {scripts[index].Name}.");
            }
        }

        return scripts;
    }

    public static string NormalizeText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    public static string ComputeChecksum(string normalizedContent)
    {
        ArgumentNullException.ThrowIfNull(normalizedContent);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedContent));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class MigrationValidationException : Exception
{
    public MigrationValidationException(string message)
        : base(message)
    {
    }
}
