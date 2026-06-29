namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed record UpstoxLiveFeedHealthSnapshot(
    bool Enabled,
    string Status,
    bool Connected,
    string Mode,
    int SubscriptionCount,
    int ConnectionAttempt,
    int ReconnectCount,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastMessageAtUtc,
    DateTimeOffset? LastPersistedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    long MessagesReceived,
    long UpdatesPersisted,
    string? LastMessageType,
    string? LastError,
    IReadOnlyDictionary<string, string> SegmentStatuses,
    bool HasOpenMarketSegment);

public interface IUpstoxLiveFeedHealthState
{
    UpstoxLiveFeedHealthSnapshot GetSnapshot();
}

public sealed class UpstoxLiveFeedHealthState : IUpstoxLiveFeedHealthState
{
    private readonly object _sync = new();
    private UpstoxLiveFeedHealthSnapshot _snapshot;

    public UpstoxLiveFeedHealthState(UpstoxLiveFeedOptions options)
    {
        _snapshot = new UpstoxLiveFeedHealthSnapshot(
            options.Enabled,
            options.Enabled ? "STOPPED" : "DISABLED",
            Connected: false,
            options.Mode,
            options.GetNormalizedInstrumentKeys().Length,
            ConnectionAttempt: 0,
            ReconnectCount: 0,
            StartedAtUtc: null,
            ConnectedAtUtc: null,
            LastMessageAtUtc: null,
            LastPersistedAtUtc: null,
            NextRetryAtUtc: null,
            MessagesReceived: 0,
            UpdatesPersisted: 0,
            LastMessageType: null,
            LastError: null,
            SegmentStatuses: new Dictionary<string, string>(),
            HasOpenMarketSegment: false);
    }

    public UpstoxLiveFeedHealthSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot with
            {
                SegmentStatuses = new Dictionary<string, string>(
                    _snapshot.SegmentStatuses,
                    StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    public void Starting(DateTimeOffset nowUtc)
    {
        Update(snapshot => snapshot with
        {
            Status = "STARTING",
            StartedAtUtc = nowUtc,
            LastError = null,
            NextRetryAtUtc = null,
        });
    }

    public void Authorizing(int attempt)
    {
        Update(snapshot => snapshot with
        {
            Status = "AUTHORIZING",
            Connected = false,
            ConnectionAttempt = attempt,
            NextRetryAtUtc = null,
        });
    }

    public void Connecting()
    {
        Update(snapshot => snapshot with
        {
            Status = "CONNECTING",
            Connected = false,
        });
    }

    public void Connected(DateTimeOffset nowUtc)
    {
        Update(snapshot => snapshot with
        {
            Status = "SUBSCRIBING",
            Connected = true,
            ConnectedAtUtc = nowUtc,
            LastError = null,
            NextRetryAtUtc = null,
        });
    }

    public void Subscribed()
    {
        Update(snapshot => snapshot with
        {
            Status = "SYNCHRONIZING",
        });
    }

    public void MessageReceived(
        DateTimeOffset nowUtc,
        string messageType,
        IReadOnlyDictionary<string, string> segmentStatuses)
    {
        Update(snapshot =>
        {
            var statuses = segmentStatuses.Count > 0
                ? new Dictionary<string, string>(
                    segmentStatuses,
                    StringComparer.OrdinalIgnoreCase)
                : snapshot.SegmentStatuses;
            var hasOpenSegment = statuses.Values.Any(IsOpenStatus);

            return snapshot with
            {
                Status = messageType.Equals(
                    "market_info",
                    StringComparison.OrdinalIgnoreCase)
                    ? "SYNCHRONIZING"
                    : "STREAMING",
                LastMessageAtUtc = nowUtc,
                MessagesReceived = snapshot.MessagesReceived + 1,
                LastMessageType = messageType,
                SegmentStatuses = statuses,
                HasOpenMarketSegment = hasOpenSegment,
                LastError = null,
            };
        });
    }

    public void Persisted(DateTimeOffset nowUtc, int updateCount)
    {
        Update(snapshot => snapshot with
        {
            Status = "STREAMING",
            LastPersistedAtUtc = nowUtc,
            UpdatesPersisted = snapshot.UpdatesPersisted + updateCount,
        });
    }

    public void Reconnecting(
        string error,
        DateTimeOffset nextRetryAtUtc,
        int reconnectCount)
    {
        Update(snapshot => snapshot with
        {
            Status = "RECONNECTING",
            Connected = false,
            ReconnectCount = reconnectCount,
            LastError = error,
            NextRetryAtUtc = nextRetryAtUtc,
        });
    }

    public void Stopped(DateTimeOffset nowUtc)
    {
        Update(snapshot => snapshot with
        {
            Status = snapshot.Enabled ? "STOPPED" : "DISABLED",
            Connected = false,
            NextRetryAtUtc = null,
            LastMessageAtUtc = snapshot.LastMessageAtUtc ?? nowUtc,
        });
    }

    private void Update(
        Func<UpstoxLiveFeedHealthSnapshot, UpstoxLiveFeedHealthSnapshot> update)
    {
        lock (_sync)
        {
            _snapshot = update(_snapshot);
        }
    }

    private static bool IsOpenStatus(string status) =>
        status.Equals("PRE_OPEN_START", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("PRE_OPEN_END", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("NORMAL_OPEN", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("CLOSING_START", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("CLOSING_END", StringComparison.OrdinalIgnoreCase);
}
