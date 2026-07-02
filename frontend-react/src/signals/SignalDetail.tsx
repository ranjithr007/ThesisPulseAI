import { useEffect, useState } from "react";

import type {
  SignalDecisionProjection,
  SignalSummary,
} from "./types";

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

function formatTime(value: string | null | undefined): string {
  if (!value) {
    return "Not available";
  }

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

function formatNumber(value: number | null | undefined): string {
  return value === null || value === undefined
    ? "Not available"
    : new Intl.NumberFormat("en-IN", { maximumFractionDigits: 6 }).format(value);
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error(
      response.status === 404
        ? "The selected signal was not found."
        : `Signal Service returned HTTP ${response.status}.`,
    );
  }
  return (await response.json()) as T;
}

export function SignalDetail({ signalUid, onBack }: SignalDetailProps) {
  const [signal, setSignal] = useState<SignalSummary | null>(null);
  const [decision, setDecision] = useState<SignalDecisionProjection | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    const load = async () => {
      setLoading(true);
      setError(null);

      try {
        const [signalResponse, decisionResponse] = await Promise.all([
          fetch(`${signalApiBaseUrl}/api/v1/signals/${encodeURIComponent(signalUid)}`, {
            headers: { Accept: "application/json" },
            signal: controller.signal,
          }),
          fetch(
            `${signalApiBaseUrl}/api/v1/signals/${encodeURIComponent(signalUid)}/decision-projection`,
            {
              headers: { Accept: "application/json" },
              signal: controller.signal,
            },
          ),
        ]);

        const payload = await readJson<SignalSummary>(signalResponse);
        const projection = await readJson<SignalDecisionProjection>(decisionResponse);
        setSignal({
          ...payload,
          strength: Number(payload.strength),
          confidence: Number(payload.confidence),
        });
        setDecision({
          ...projection,
          tradePlan: {
            ...projection.tradePlan,
            approvedQuantity:
              projection.tradePlan.approvedQuantity === null
                ? null
                : Number(projection.tradePlan.approvedQuantity),
            entryReferencePrice:
              projection.tradePlan.entryReferencePrice === null
                ? null
                : Number(projection.tradePlan.entryReferencePrice),
            stopLossPrice:
              projection.tradePlan.stopLossPrice === null
                ? null
                : Number(projection.tradePlan.stopLossPrice),
          },
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
        <div className="page-state-card">Loading canonical signal decisions...</div>
      </section>
    );
  }

  if (error || !signal || !decision) {
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

  const plan = decision.tradePlan;

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
          <span>Risk decision</span>
          <strong>{decision.riskDecisionStatus}</strong>
          <small>{formatTime(decision.riskEvaluatedAtUtc)}</small>
        </article>
        <article>
          <span>Trade plan</span>
          <strong>{plan.status}</strong>
          <small>Execution authorized: No</small>
        </article>
      </div>

      <div className="detail-grid">
        <article className="detail-card">
          <h3>Signal lifecycle</h3>
          <dl>
            <div><dt>Status</dt><dd>{signal.status}</dd></div>
            <div><dt>Generated</dt><dd>{formatTime(signal.generatedAtUtc)}</dd></div>
            <div><dt>Valid until</dt><dd>{formatTime(signal.validUntilUtc)}</dd></div>
            <div><dt>Freshness</dt><dd>{formatRemaining(signal.validUntilUtc)}</dd></div>
          </dl>
        </article>

        <article className="detail-card">
          <h3>Authoritative Risk</h3>
          <dl>
            <div><dt>Decision</dt><dd>{decision.riskDecisionStatus}</dd></div>
            <div><dt>Evaluated</dt><dd>{formatTime(decision.riskEvaluatedAtUtc)}</dd></div>
            <div><dt>Decision UID</dt><dd className="monospace">{decision.riskDecisionUid ?? "Not available"}</dd></div>
            <div><dt>Authority</dt><dd>Risk Service</dd></div>
          </dl>
        </article>

        <article className="detail-card">
          <h3>Trade Plan</h3>
          <dl>
            <div><dt>Status</dt><dd>{plan.status}</dd></div>
            <div><dt>Quantity</dt><dd>{formatNumber(plan.approvedQuantity)}</dd></div>
            <div><dt>Entry reference</dt><dd>{formatNumber(plan.entryReferencePrice)}</dd></div>
            <div><dt>Stop loss</dt><dd>{formatNumber(plan.stopLossPrice)}</dd></div>
            <div><dt>Generated</dt><dd>{formatTime(plan.generatedAtUtc)}</dd></div>
            <div><dt>Valid until</dt><dd>{formatTime(plan.validUntilUtc)}</dd></div>
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
            <div><dt>Risk decision UID</dt><dd className="monospace">{decision.riskDecisionUid ?? "Unavailable"}</dd></div>
            <div><dt>Trade Plan UID</dt><dd className="monospace">{plan.tradePlanUid ?? "Unavailable"}</dd></div>
            <div><dt>Database ID</dt><dd>{signal.signalId ?? "Not projected"}</dd></div>
            <div><dt>Instrument key</dt><dd>{signal.instrumentKey}</dd></div>
          </dl>
        </article>
      </div>

      <aside className="safety-note">
        <strong>Controlled boundary</strong>
        <span>
          Risk and Trade Plan values are authoritative read-only projections. The plan
          cannot submit an order, contact a broker, or mutate the portfolio. Execution
          remains a separate service boundary.
        </span>
      </aside>
    </section>
  );
}
