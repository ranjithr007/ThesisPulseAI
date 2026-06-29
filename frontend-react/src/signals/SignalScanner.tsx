import { useMemo, useState } from "react";

import { useSignalScanner } from "./useSignalScanner";
import type {
  SignalConnectionState,
  SignalScannerFilters,
  SignalStatus,
  SignalSummary,
} from "./types";

const initialFilters: SignalScannerFilters = {
  search: "",
  direction: "ALL",
  status: "ALL",
  timeframe: "ALL",
};

const statusOrder: SignalStatus[] = [
  "VALIDATED",
  "CANDIDATE",
  "CONSUMED",
  "REJECTED",
  "EXPIRED",
  "SUPERSEDED",
];

function formatPercentage(value: number): string {
  return `${Math.round(value * 100)}%`;
}

function formatTime(value: string | null): string {
  if (!value) {
    return "Not available";
  }

  const parsed = Date.parse(value);
  if (Number.isNaN(parsed)) {
    return value;
  }

  return new Intl.DateTimeFormat("en-IN", {
    dateStyle: "medium",
    timeStyle: "medium",
    timeZone: "Asia/Kolkata",
  }).format(parsed);
}

function formatRemaining(validUntilUtc: string): string {
  const remainingMilliseconds = Date.parse(validUntilUtc) - Date.now();

  if (remainingMilliseconds <= 0) {
    return "Due for expiry";
  }

  const remainingMinutes = Math.ceil(remainingMilliseconds / 60_000);
  if (remainingMinutes < 60) {
    return `${remainingMinutes}m remaining`;
  }

  const remainingHours = Math.ceil(remainingMinutes / 60);
  return `${remainingHours}h remaining`;
}

function connectionLabel(state: SignalConnectionState): string {
  switch (state) {
    case "LIVE":
      return "Live SignalR";
    case "CONNECTING":
      return "Connecting";
    case "RECONNECTING":
      return "Reconnecting";
    case "REST_FALLBACK":
      return "REST fallback";
    default:
      return "Offline";
  }
}

function signalMatchesFilters(
  signal: SignalSummary,
  filters: SignalScannerFilters,
): boolean {
  const search = filters.search.trim().toLowerCase();
  const matchesSearch =
    search.length === 0 ||
    signal.instrumentKey.toLowerCase().includes(search) ||
    signal.strategyCode.toLowerCase().includes(search);

  return (
    matchesSearch &&
    (filters.direction === "ALL" || signal.direction === filters.direction) &&
    (filters.status === "ALL" || signal.status === filters.status) &&
    (filters.timeframe === "ALL" ||
      signal.primaryTimeframe === filters.timeframe)
  );
}

export function SignalScanner() {
  const {
    signals,
    connectionState,
    lastEventAtUtc,
    lastSnapshotAtUtc,
    error,
    refresh,
  } = useSignalScanner();
  const [filters, setFilters] = useState(initialFilters);

  const filteredSignals = useMemo(
    () => signals.filter((signal) => signalMatchesFilters(signal, filters)),
    [filters, signals],
  );

  const timeframes = useMemo(
    () =>
      Array.from(new Set(signals.map((signal) => signal.primaryTimeframe))).sort(),
    [signals],
  );

  const counts = useMemo(() => {
    const result = new Map<SignalStatus, number>();
    for (const status of statusOrder) {
      result.set(status, 0);
    }

    for (const signal of signals) {
      result.set(signal.status, (result.get(signal.status) ?? 0) + 1);
    }

    return result;
  }, [signals]);

  return (
    <section className="signal-scanner" aria-labelledby="signal-scanner-title">
      <div className="scanner-heading">
        <div>
          <p className="eyebrow">REAL-TIME INTELLIGENCE</p>
          <h2 id="signal-scanner-title">Signal Scanner</h2>
          <p className="scanner-description">
            Versioned PAPER signals with lifecycle state, confidence, freshness,
            and automatic expiry visibility.
          </p>
        </div>

        <div className="scanner-actions">
          <div
            className={`connection-pill ${connectionState.toLowerCase()}`}
            role="status"
          >
            <span className="status-dot" aria-hidden="true" />
            <div>
              <strong>{connectionLabel(connectionState)}</strong>
              <span>
                Last event: {formatTime(lastEventAtUtc ?? lastSnapshotAtUtc)}
              </span>
            </div>
          </div>
          <button className="refresh-button" type="button" onClick={() => void refresh()}>
            Refresh snapshot
          </button>
        </div>
      </div>

      {error ? (
        <div className="scanner-alert" role="alert">
          <strong>Live update warning</strong>
          <span>{error}</span>
        </div>
      ) : null}

      <div className="signal-metrics" aria-label="Signal lifecycle counts">
        <article>
          <span>Total signals</span>
          <strong>{signals.length}</strong>
        </article>
        <article>
          <span>Validated</span>
          <strong>{counts.get("VALIDATED") ?? 0}</strong>
        </article>
        <article>
          <span>Candidates</span>
          <strong>{counts.get("CANDIDATE") ?? 0}</strong>
        </article>
        <article>
          <span>Expired</span>
          <strong>{counts.get("EXPIRED") ?? 0}</strong>
        </article>
      </div>

      <div className="scanner-filters" aria-label="Signal filters">
        <label>
          <span>Search</span>
          <input
            type="search"
            value={filters.search}
            placeholder="Instrument or strategy"
            onChange={(event) =>
              setFilters((current) => ({
                ...current,
                search: event.target.value,
              }))
            }
          />
        </label>

        <label>
          <span>Direction</span>
          <select
            value={filters.direction}
            onChange={(event) =>
              setFilters((current) => ({
                ...current,
                direction: event.target.value as SignalScannerFilters["direction"],
              }))
            }
          >
            <option value="ALL">All directions</option>
            <option value="LONG">Long</option>
            <option value="SHORT">Short</option>
          </select>
        </label>

        <label>
          <span>Status</span>
          <select
            value={filters.status}
            onChange={(event) =>
              setFilters((current) => ({
                ...current,
                status: event.target.value as SignalScannerFilters["status"],
              }))
            }
          >
            <option value="ALL">All statuses</option>
            {statusOrder.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </select>
        </label>

        <label>
          <span>Timeframe</span>
          <select
            value={filters.timeframe}
            onChange={(event) =>
              setFilters((current) => ({
                ...current,
                timeframe: event.target.value,
              }))
            }
          >
            <option value="ALL">All timeframes</option>
            {timeframes.map((timeframe) => (
              <option key={timeframe} value={timeframe}>
                {timeframe}
              </option>
            ))}
          </select>
        </label>
      </div>

      <div className="signal-table-shell">
        <table className="signal-table">
          <thead>
            <tr>
              <th>Instrument</th>
              <th>Direction</th>
              <th>Status</th>
              <th>Timeframe</th>
              <th>Strength</th>
              <th>Confidence</th>
              <th>Validity</th>
              <th>Strategy</th>
            </tr>
          </thead>
          <tbody>
            {filteredSignals.map((signal) => (
              <tr key={signal.signalUid}>
                <td>
                  <strong>{signal.instrumentKey}</strong>
                  <span>{formatTime(signal.generatedAtUtc)}</span>
                </td>
                <td>
                  <span className={`direction-badge ${signal.direction.toLowerCase()}`}>
                    {signal.direction}
                  </span>
                </td>
                <td>
                  <span className={`status-badge ${signal.status.toLowerCase()}`}>
                    {signal.status}
                  </span>
                </td>
                <td>{signal.primaryTimeframe}</td>
                <td>
                  <div className="score-cell">
                    <span>{formatPercentage(signal.strength)}</span>
                    <meter min="0" max="1" value={signal.strength} />
                  </div>
                </td>
                <td>
                  <div className="score-cell">
                    <span>{formatPercentage(signal.confidence)}</span>
                    <meter min="0" max="1" value={signal.confidence} />
                  </div>
                </td>
                <td>
                  <strong>{formatRemaining(signal.validUntilUtc)}</strong>
                  <span>{formatTime(signal.validUntilUtc)}</span>
                </td>
                <td>
                  <strong>{signal.strategyCode}</strong>
                  <span>v{signal.strategyVersion}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        {filteredSignals.length === 0 ? (
          <div className="empty-state">
            <strong>No matching signals</strong>
            <span>
              The scanner will update automatically when Signal Service publishes a
              new PAPER signal.
            </span>
          </div>
        ) : null}
      </div>
    </section>
  );
}
