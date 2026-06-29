import { useEffect, useState } from "react";

import type { SignalSummary } from "./types";

const signalApiBaseUrl =
  import.meta.env.VITE_SIGNAL_API_BASE_URL?.replace(/\/$/, "") ??
  "http://localhost:5102";

interface SignalDetailProps {
  signalUid: string;
  onBack: () => void;
}

function formatPercentage(value: number): string {
  return `${Math.round(Number(value) * 100)}%`;
}

function formatTime(value: string): string {
  const timestamp = Date.parse(value);

  if (Number.isNaN(timestamp)) {
    return value;
  }

  return new Intl.DateTimeFormat("en-IN", {
    dateStyle: "medium",
    timeStyle: "medium",
    timeZone: "Asia/Kolkata",
  }).format(timestamp);
}

function formatRemaining(validUntilUtc: string): string {
  const remaining = Date.parse(validUntilUtc) - Date.now();

  if (remaining <= 0) {
    return "Validity window elapsed";
  }

  const minutes = Math.ceil(remaining / 60_000);
  return minutes < 60
    ? `${minutes} minutes remaining`
    : `${Math.ceil(minutes / 60)} hours remaining`;
}

export function SignalDetail({ signalUid, onBack }: SignalDetailProps) {
  const [signal, setSignal] = useState<SignalSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    const load = async () => {
      setLoading(true);
      setError(null);

      try {
        const response = await fetch(
          `${signalApiBaseUrl}/api/v1/signals/${encodeURIComponent(signalUid)}`,
          {
            headers: { Accept: "application/json" },
            signal: controller.signal,
          },
        );

        if (!response.ok) {
          throw new Error(
            response.status === 404
              ? "The selected signal was not found."
              : `Signal Service returned HTTP ${response.status}.`,
          );
        }

        const payload = (await response.json()) as SignalSummary;
        setSignal({
          ...payload,
          strength: Number(payload.strength),
          confidence: Number(payload.confidence),
        });
      } catch (requestError) {
        if (!controller.signal.aborted) {
          setError(
            requestError instanceof Error
              ? requestError.message
              : "Signal details could not be loaded.",
          );
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    };

    void load();
    return () => controller.abort();
  }, [signalUid]);

  if (loading) {
    return (
      <section className="detail-page" aria-busy="true">
        <button className="back-button" type="button" onClick={onBack}>
          Back to scanner
        </button>
        <div className="page-state-card">Loading canonical signal details...</div>
      </section>
    );
  }

  if (error || !signal) {
    return (
      <section className="detail-page">
        <button className="back-button" type="button" onClick={onBack}>
          Back to scanner
        </button>
        <div className="page-state-card error-state" role="alert">
          <strong>Signal details unavailable</strong>
          <span>{error ?? "The signal could not be loaded."}</span>
        </div>
      </section>
    );
  }

  return (
    <section className="detail-page" aria-labelledby="signal-detail-title">
      <div className="detail-toolbar">
        <button className="back-button" type="button" onClick={onBack}>
          Back to scanner
        </button>
        <span className={`status-badge ${signal.status.toLowerCase()}`}>
          {signal.status}
        </span>
      </div>

      <header className="detail-hero">
        <div>
          <p className="eyebrow">SIGNAL INTELLIGENCE</p>
          <h2 id="signal-detail-title">{signal.instrumentKey}</h2>
          <p>
            {signal.direction} / {signal.primaryTimeframe} / {signal.strategyCode}
          </p>
        </div>
        <div className={`direction-panel ${signal.direction.toLowerCase()}`}>
          <span>Directional view</span>
          <strong>{signal.direction}</strong>
          <small>{formatRemaining(signal.validUntilUtc)}</small>
        </div>
      </header>

      <div className="detail-metrics">
        <article>
          <span>Strength</span>
          <strong>{formatPercentage(signal.strength)}</strong>
          <meter min="0" max="1" value={signal.strength} />
        </article>
        <article>
          <span>Confidence</span>
          <strong>{formatPercentage(signal.confidence)}</strong>
          <meter min="0" max="1" value={signal.confidence} />
        </article>
        <article>
          <span>Timeframe</span>
          <strong>{signal.primaryTimeframe}</strong>
          <small>Primary horizon</small>
        </article>
        <article>
          <span>Environment</span>
          <strong>PAPER</strong>
          <small>Live orders disabled</small>
        </article>
      </div>

      <div className="detail-grid">
        <article className="detail-card">
          <h3>Lifecycle</h3>
          <dl>
            <div><dt>Status</dt><dd>{signal.status}</dd></div>
            <div><dt>Generated</dt><dd>{formatTime(signal.generatedAtUtc)}</dd></div>
            <div><dt>Valid until</dt><dd>{formatTime(signal.validUntilUtc)}</dd></div>
            <div><dt>Freshness</dt><dd>{formatRemaining(signal.validUntilUtc)}</dd></div>
          </dl>
        </article>

        <article className="detail-card">
          <h3>Strategy identity</h3>
          <dl>
            <div><dt>Strategy</dt><dd>{signal.strategyCode}</dd></div>
            <div><dt>Version</dt><dd>{signal.strategyVersion}</dd></div>
            <div><dt>Creator engine</dt><dd>{signal.creatorEngineCode}</dd></div>
            <div><dt>Producer</dt><dd>{signal.producer}</dd></div>
          </dl>
        </article>

        <article className="detail-card full-width">
          <h3>Immutable lineage</h3>
          <dl className="lineage-grid">
            <div><dt>Signal UID</dt><dd className="monospace">{signal.signalUid}</dd></div>
            <div><dt>Message UID</dt><dd className="monospace">{signal.messageId || "Unavailable"}</dd></div>
            <div><dt>Database ID</dt><dd>{signal.signalId ?? "Local memory"}</dd></div>
            <div><dt>Instrument key</dt><dd>{signal.instrumentKey}</dd></div>
          </dl>
        </article>
      </div>

      <aside className="safety-note">
        <strong>Controlled boundary</strong>
        <span>
          This view contains signal intelligence only. Risk approval and order
          handling remain separate services.
        </span>
      </aside>
    </section>
  );
}
