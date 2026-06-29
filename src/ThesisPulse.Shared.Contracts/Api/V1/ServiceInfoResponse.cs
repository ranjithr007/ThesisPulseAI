namespace ThesisPulse.Shared.Contracts.Api.V1;

public sealed record ServiceInfoResponse(
    string ServiceName,
    string ServiceVersion,
    string ContractVersion,
    string ConfigurationVersion,
    string Environment,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CurrentTimeUtc);
