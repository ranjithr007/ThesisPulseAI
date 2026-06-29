using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThesisPulse.Shared.Contracts.Messaging.V1;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

internal static class SqlServerMessageValues
{
    private const string ConfigurationVersionHeader = "configurationVersion";

    public static Guid ToDatabaseGuid(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (Guid.TryParse(value, out var parsed))
        {
            return parsed;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static Guid? ToOptionalDatabaseGuid(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : ToDatabaseGuid(value, nameof(value));

    public static string ComputePayloadHash(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexString(hash);
    }

    public static string BuildHeadersJson(MessageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [ConfigurationVersionHeader] = metadata.ConfigurationVersion,
            });
    }

    public static string ReadConfigurationVersion(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return "unversioned";
        }

        using var document = JsonDocument.Parse(headersJson);
        return document.RootElement.TryGetProperty(
            ConfigurationVersionHeader,
            out var value)
            ? value.GetString() ?? "unversioned"
            : "unversioned";
    }

    public static DateTimeOffset ReadUtcDateTimeOffset(
        Microsoft.Data.SqlClient.SqlDataReader reader,
        int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    public static DateTimeOffset? ReadNullableUtcDateTimeOffset(
        Microsoft.Data.SqlClient.SqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ReadUtcDateTimeOffset(reader, ordinal);
}
