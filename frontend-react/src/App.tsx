import { useEffect, useMemo, useState } from "react";

import {
  createSignalConnection,
  fetchRecentSignals,
  mergeSignalEvent,
  type SignalStreamEvent,
  type StreamConnectionState,
} from "./signals";

const navigation = ["Market", "Signals", "Theses", "Risk", "Portfolio", "P&L", "Operations"];
const tradingApiBaseUrl = import.meta.env.VITE_TRADING_API_BASE_URL ?? "http://localhost:5100";

export function App() {
  const [signals, setSignals] = useState<SignalStreamEvent[]>([]);
  const [connectionState, setConnectionState] = useState<StreamConnectionState>("connecting");
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState("ALL");
  const [directionFilter, setDirectionFilter] = useState("ALL");

  useEffect(() => {
    let disposed = false;
    const connection = createSignalConnection(
      tradingApiBaseUrl,
      (event) => {
        if (!disposed) {
          setSignals((current) => mergeSignalEvent(current, event));
        }
      },
      setConnectionState,
    );

    async function start() {
      try {
        const recent = await fetchRecentSignals(tradingApiBaseUrl);
        if (!disposed) {
          setSignals(recent.reduce(mergeSignalEvent, [] as SignalStreamEvent[]));
        }

        await connection.start();
        if (!disposed) {
          setConnectionState("connected");
          setError(null);
        }
      } catch (reason) {
        if (!disposed) {
          setConnectionState("disconnected");
          setError(reason instanceof Error ? reason.message : "Signal stream connection failed");
        }
      }
    }

    void start();
    return () => {
      disposed = true;
      void connection.stop();
    };
  }, []);

  const filteredSignals = useMemo(
    () =>
      signals.filter(
        (signal) =>
          (statusFilter === "ALL" || signal.status === statusFilter) &&
          (directionFilter === "ALL" || signal.direction === directionFilter),
      ),
    [signals, statusFilter, directionFilter],
  );

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">THESISPULSE AI</p>
          <h1>Intelligent signals. Validated theses. Adaptive decisions.</h1>
        </div>
        <div className="environment-badge" role="status">
          <strong>PAPER TRADING</strong>
          <span>No real orders will be submitted</span>
        </div>
      </header>

      <div className="workspace">
        <aside className="sidebar" aria-label="Primary navigation">
          <nav>
            {navigation.map((item) => (
              <button className={item === "Signals" ? "nav-item active" : "nav-item"} key={item}>
                {item}
              </button>
            ))}
          </nav>
          <div className={`connection-status ${connectionState}`}>
            <span className="status-dot" aria-hidden="true" />
            Signal stream: {connectionState}
          </div>
        </aside>

        <main className="content">
          <section className="scanner-header">
            <div>
              <p className="eyebrow">LIVE PAPER SIGNALS</p>
              <h2>Signal scanner</h2>
              <p>Real-time lifecycle updates from Trading API SignalR.</p>
            </div>
            <div className="scanner-count">{filteredSignals.length} visible</div>
          </section>

          <section className="scanner-filters" aria-label="Signal filters">
            <label>
              Status
              <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)}>
                {['ALL', 'CANDIDATE', 'VALIDATED', 'REJECTED', 'EXPIRED', 'SUPERSEDED', 'CONSUMED'].map((value) => (
                  <option value={value} key={value}>{value}</option>
                ))}
              </select>
            </label>
            <label>
              Direction
              <select value={directionFilter} onChange={(event) => setDirectionFilter(event.target.value)}>
                {['ALL', 'LONG', 'SHORT'].map((value) => (
                  <option value={value} key={value}>{value}</option>
                ))}
              </select>
            </label>
          </section>

          {error && <div className="stream-error">{error}</div>}

          <section className="signal-table-wrap" aria-label="Signal scanner results">
            <table className="signal-table">
              <thead>
                <tr>
                  <th>Instrument</th>
                  <th>Direction</th>
                  <th>Timeframe</th>
                  <th>Confidence</th>
                  <th>Strength</th>
                  <th>Status</th>
                  <th>Valid until</th>
                </tr>
              </thead>
              <tbody>
                {filteredSignals.map((signal) => (
                  <tr key={signal.signalUid}>
                    <td><strong>{signal.instrumentKey}</strong><small>{signal.signalUid}</small></td>
                    <td><span className={`direction ${signal.direction.toLowerCase()}`}>{signal.direction}</span></td>
                    <td>{signal.primaryTimeframe}</td>
                    <td>{Math.round(signal.confidence * 100)}%</td>
                    <td>{Math.round(signal.strength * 100)}%</td>
                    <td><span className={`signal-status ${signal.status.toLowerCase()}`}>{signal.status}</span></td>
                    <td>{new Date(signal.validUntilUtc).toLocaleString()}</td>
                  </tr>
                ))}
                {filteredSignals.length === 0 && (
                  <tr><td className="empty-state" colSpan={7}>No matching signal events yet.</td></tr>
                )}
              </tbody>
            </table>
          </section>
        </main>
      </div>
    </div>
  );
}
