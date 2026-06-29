using System.Text;
using System.Text.RegularExpressions;

namespace ThesisPulse.DatabaseMigrator;

public sealed record SqlBatch(string Text, int StartLine, int RepeatIndex);

public static class SqlBatchSplitter
{
    private static readonly Regex GoPattern = new(
        @"^\s*GO(?:\s+(?<count>\d+))?\s*(?:--.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<SqlBatch> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var normalized = MigrationScript.NormalizeText(script);
        var lines = normalized.Split('\n');
        var batches = new List<SqlBatch>();
        var current = new StringBuilder();
        var batchStartLine = 1;
        var lexicalState = SqlLexicalState.Normal;

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            var goMatch = lexicalState == SqlLexicalState.Normal
                ? GoPattern.Match(line)
                : Match.Empty;

            if (goMatch.Success)
            {
                var repeatCount = 1;
                if (goMatch.Groups["count"].Success &&
                    (!int.TryParse(goMatch.Groups["count"].Value, out repeatCount) || repeatCount <= 0))
                {
                    throw new MigrationValidationException(
                        $"Invalid GO repeat count at line {lineNumber}.");
                }

                AddBatch(batches, current, batchStartLine, repeatCount);
                current.Clear();
                batchStartLine = lineNumber + 1;
                continue;
            }

            current.AppendLine(line);
            lexicalState = ScanLine(line, lexicalState);
        }

        AddBatch(batches, current, batchStartLine, repeatCount: 1);
        return batches;
    }

    private static void AddBatch(
        ICollection<SqlBatch> batches,
        StringBuilder current,
        int startLine,
        int repeatCount)
    {
        var text = current.ToString().Trim();
        if (text.Length == 0)
        {
            return;
        }

        for (var repeatIndex = 1; repeatIndex <= repeatCount; repeatIndex++)
        {
            batches.Add(new SqlBatch(text, startLine, repeatIndex));
        }
    }

    private static SqlLexicalState ScanLine(string line, SqlLexicalState state)
    {
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            var next = index + 1 < line.Length ? line[index + 1] : '\0';

            switch (state)
            {
                case SqlLexicalState.Normal:
                    if (current == '-' && next == '-')
                    {
                        return SqlLexicalState.Normal;
                    }

                    if (current == '/' && next == '*')
                    {
                        state = SqlLexicalState.BlockComment;
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = SqlLexicalState.SingleQuotedString;
                    }
                    else if (current == '"')
                    {
                        state = SqlLexicalState.DoubleQuotedIdentifier;
                    }
                    else if (current == '[')
                    {
                        state = SqlLexicalState.BracketedIdentifier;
                    }

                    break;

                case SqlLexicalState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        state = SqlLexicalState.Normal;
                        index++;
                    }

                    break;

                case SqlLexicalState.SingleQuotedString:
                    if (current == '\'' && next == '\'')
                    {
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = SqlLexicalState.Normal;
                    }

                    break;

                case SqlLexicalState.DoubleQuotedIdentifier:
                    if (current == '"' && next == '"')
                    {
                        index++;
                    }
                    else if (current == '"')
                    {
                        state = SqlLexicalState.Normal;
                    }

                    break;

                case SqlLexicalState.BracketedIdentifier:
                    if (current == ']' && next == ']')
                    {
                        index++;
                    }
                    else if (current == ']')
                    {
                        state = SqlLexicalState.Normal;
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unsupported SQL lexical state: {state}");
            }
        }

        return state;
    }

    private enum SqlLexicalState
    {
        Normal,
        BlockComment,
        SingleQuotedString,
        DoubleQuotedIdentifier,
        BracketedIdentifier
    }
}
