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
  return Number.isNaN(parsed)
    ? value
    : new Intl.DateTimeFormat("en-IN", {
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
  return remainingMinutes < 60
    ? `${remainingMinutes}m remaining`
    : `${Math.ceil(remainingMinutes / 60)}h remaining`;
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

function matchesFilters(
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
  const scanner = useSignalScanner();
  const [filters, setFilters] = useState(initialFilters);
  const filteredSignals = useMemo(
    () => scanner.signals.filter((signal) => matchesFilters(signal, filters)),
    [filters, scanner.signals],
  );
  const timeframes = useMemo(
    () =>
      Array.from(
        new Set(scanner.signals.map((signal) => signal.primaryTimeframe)),
      ).sort(),
    [scanner.signals],
  );
  const counts = useMemo(() => {
    const result = new Map<SignalStatus, number>();
    statusOrder.forEach((status) => result.set(status, 0));
    scanner.signals.forEach((signal) =>
      result.set(signal.status, (result.get(signal.status) ?? 0) + 1),
    );
    return result;
  }, [scanner.signals]);
  const riskApproved = useMemo(
    () =>
      scanner.signals.filter(
        (signal) => signal.riskDecisionStatus === "APPROVED",
      ).length,
    [scanner.signals],
  );
  const plansReady = useMemo(
    () =>
      scanner.signals.filter((signal) => signal.tradePlan?.status === "READY")
        .length,
    [scanner.signals],
  );

  return (
    <section className="signal-scanner" aria-labelledby="signal-scanner-title">
      <div className="scanner-heading">
        <div>
          <p className="eyebrow">REAL-TIME INTELLIGENCE</p>
          <h2 id="signal-scanner-title">Signal Scanner</h2>
          <p className="scanner-description">
            Versioned PAPER signals with authoritative Risk decisions, Trade Plan
            readiness, freshness, and lifecycle visibility. Execution remains disabled.
          </p>
        </div>
        <div className="scanner-actions">
          <div
            className={`connection-pill ${scanner.connectionState.toLowerCase()}`}
            role="status"
          >
            <span className="status-dot" aria-hidden="true" />
            <div>
              <strong>{connectionLabel(scanner.connectionState)}</strong>
              <span>
                Last update: {formatTime(scanner.lastEventAtUtc ?? scanner.lastSnapshotAtUtc)}
              </span>
            </div>
          </div>
          <button
            className="refresh-button"
            type="button"
            onClick={() => void scanner.refresh()}
          >
            Refresh snapshot
          </button>
        </div>
      </div>

      {scanner.error ? (
        <div className="scanner-alert" role="alert">
          <strong>Live update warning</strong>
          <span>{scanner.error}</span>
        </div>
      ) : null}

      <div className="signal-metrics" aria-label="Signal decision counts">
        <article><span>Total signals</span><strong>{scanner.signals.length}</strong></article>
        <article><span>Validated</span><strong>{counts.get("VALIDATED") ?? 0}</strong></article>
        <article><span>Risk approved</span><strong>{riskApproved}</strong></article>
        <article><span>Plans ready</span><strong>{plansReady}</strong></article>
        <article><span>Expired</span><strong>{counts.get("EXPIRED") ?? 0}</strong></article>
      </div>

      <div className="scanner-filters" aria-label="Signal filters">
        <label>
          <span>Search</span>
          <input
            type="search"
            value={filters.search}
            placeholder="Instrument or strategy"
            onChange={(event) =>
              setFilters((current) => ({ ...current, search: event.target.value }))
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
              <option key={status} value={status}>{status}</option>
            ))}
          </select>
        </label>
        <label>
          <span>Timeframe</span>
          <select
            value={filters.timeframe}
            onChange={(event) =>
              setFilters((current) => ({ ...current, timeframe: event.target.value }))
            }
          >
            <option value="ALL">All timeframes</option>
            {timeframes.map((timeframe) => (
              <option key={timeframe} value={timeframe}>{timeframe}</option>
            ))}
          </select>
        </label>
      </div>

      <div className="signal-table-shell">
        <table className="signal-table">
          <thead>
            <tr>
              <th>Instrument</th><th>Direction</th><th>Signal</th><th>Risk</th>
              <th>Trade plan</th><th>Timeframe</th><th>Strength</th>
              <th>Confidence</th><th>Validity</th><th>Strategy</th>
            </tr>
          </thead>
          <tbody>
            {filteredSignals.map((signal) => {
              const riskStatus = signal.riskDecisionStatus ?? "NOT_EVALUATED";
              const planStatus = signal.tradePlan?.status ?? "NOT_AVAILABLE";
              return (
                <tr key={signal.signalUid}>
                  <td>
                    <a
                      className="instrument-link"
                      href={`#/signals/${encodeURIComponent(signal.signalUid)}`}
                    >
                      {signal.instrumentKey}
                    </a>
                    <span>{formatTime(signal.generatedAtUtc)}</span>
                  </td>
                  <td><span className={`direction-badge ${signal.direction.toLowerCase()}`}>{signal.direction}</span></td>
                  <td><span className={`status-badge ${signal.status.toLowerCase()}`}>{signal.status}</span></td>
                  <td><span className={`decision-badge ${riskStatus.toLowerCase()}`}>{riskStatus}</span></td>
                  <td>
                    <span className={`decision-badge ${planStatus.toLowerCase()}`}>{planStatus}</span>
                    {signal.tradePlan?.approvedQuantity !== null &&
                    signal.tradePlan?.approvedQuantity !== undefined ? (
                      <span>{signal.tradePlan.approvedQuantity} units</span>
                    ) : null}
                  </td>
                  <td>{signal.primaryTimeframe}</td>
                  <td><div className="score-cell"><span>{formatPercentage(signal.strength)}</span><meter min="0" max="1" value={signal.strength} /></div></td>
                  <td><div className="score-cell"><span>{formatPercentage(signal.confidence)}</span><meter min="0" max="1" value={signal.confidence} /></div></td>
                  <td><strong>{formatRemaining(signal.validUntilUtc)}</strong><span>{formatTime(signal.validUntilUtc)}</span></td>
                  <td><strong>{signal.strategyCode}</strong><span>v{signal.strategyVersion}</span></td>
                </tr>
              );
            })}
          </tbody>
        </table>

        {filteredSignals.length === 0 ? (
          <div className="empty-state">
            <strong>No matching signals</strong>
            <span>The scanner will update automatically when a PAPER signal is published.</span>
          </div>
        ) : null}
      </div>
    </section>
  );
}
