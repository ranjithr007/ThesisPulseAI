import { useEffect, useMemo, useState } from "react";
import "./risk-readiness-workspace.css";

type WorkerSnapshot = Record<string, unknown>;

type RiskStatus = {
  mode?: string;
  environment?: string;
  failClosed?: boolean;
  persistenceMode?: string;
  defaultRiskDecision?: string;
  automaticRiskWorkerEnabled?: boolean;
  automaticCanonicalSignalIntakeEnabled?: boolean;
  automaticPortfolioRiskEnabled?: boolean;
  automaticTradePlanIntakeEnabled?: boolean;
  automaticTradePlanWorkerEnabled?: boolean;
  automaticRiskWorkerState?: WorkerSnapshot;
  automaticPortfolioRiskWorkerState?: WorkerSnapshot;
  automaticTradePlanWorkerState?: WorkerSnapshot;
  riskDecisionAuthority?: boolean;
  riskStatusAuthority?: boolean;
  portfolioOperatingModeAuthority?: boolean;
  tradePlanAuthority?: boolean;
  positionSizingAuthority?: boolean;
  executionAuthority?: boolean;
  brokerSubmissionAuthority?: boolean;
};

type LoadState = "loading" | "ready" | "degraded" | "unavailable";

const riskApiBaseUrl = (import.meta.env.VITE_RISK_API_BASE_URL ?? "").replace(/\/$/, "");

function workerSummary(enabled: boolean | undefined, state: WorkerSnapshot | undefined) {
  if (!enabled) return "DISABLED";
  if (!state) return "ENABLED";
  const status = state.status ?? state.state ?? state.outcome;
  return typeof status === "string" ? status : "ENABLED";
}

export function RiskReadinessWorkspace() {
  const [status, setStatus] = useState<RiskStatus | null>(null);
  const [loadState, setLoadState] = useState<LoadState>("loading");
  const [observedAt, setObservedAt] = useState<Date | null>(null);

  useEffect(() => {
    let disposed = false;

    async function loadStatus() {
      try {
        const response = await fetch(`${riskApiBaseUrl}/api/v1/status`, {
          headers: { Accept: "application/json" },
        });
        if (!response.ok) {
          if (!disposed) setLoadState("unavailable");
          return;
        }

        const nextStatus = (await response.json()) as RiskStatus;
        if (disposed) return;
        setStatus(nextStatus);
        setObservedAt(new Date());
        setLoadState(
          nextStatus.failClosed === true &&
            nextStatus.executionAuthority === false &&
            nextStatus.brokerSubmissionAuthority === false
            ? "ready"
            : "degraded",
        );
      } catch {
        if (!disposed) setLoadState("unavailable");
      }
    }

    void loadStatus();
    const interval = window.setInterval(loadStatus, 30_000);
    return () => {
      disposed = true;
      window.clearInterval(interval);
    };
  }, []);

  const freshness = useMemo(() => {
    if (!observedAt) return "Awaiting Risk Service";
    return `Updated ${observedAt.toLocaleTimeString()}`;
  }, [observedAt]);

  return (
    <section className="risk-workspace" aria-labelledby="risk-heading">
      <div className="risk-hero">
        <div>
          <p className="eyebrow">RISK AUTHORITY</p>
          <h2 id="risk-heading">Fail-closed risk and trade-plan readiness</h2>
          <p>
            Read-only visibility into authoritative PAPER risk state. This workspace cannot mutate
            policies, submit orders, or bypass risk decisions.
          </p>
        </div>
        <div className={`risk-state risk-state-${loadState}`} role="status">
          <strong>{loadState.toUpperCase()}</strong>
          <span>{freshness}</span>
        </div>
      </div>

      <div className="risk-card-grid">
        <article className="risk-card"><span>Mode</span><strong>{status?.mode ?? "UNKNOWN"}</strong><p>{status?.environment ?? "PAPER"}</p></article>
        <article className="risk-card"><span>Fail closed</span><strong>{status?.failClosed ? "ENFORCED" : "NOT CONFIRMED"}</strong><p>Unknown state blocks approval.</p></article>
        <article className="risk-card"><span>Persistence</span><strong>{status?.persistenceMode ?? "UNKNOWN"}</strong><p>Authoritative risk-state storage mode.</p></article>
        <article className="risk-card"><span>Default decision</span><strong>{status?.defaultRiskDecision ?? "REJECTED"}</strong><p>Safe fallback for incomplete evidence.</p></article>
      </div>

      <div className="risk-worker-grid" aria-label="Risk worker readiness">
        <div><span>Risk worker</span><strong>{workerSummary(status?.automaticRiskWorkerEnabled, status?.automaticRiskWorkerState)}</strong></div>
        <div><span>Canonical intake</span><strong>{status?.automaticCanonicalSignalIntakeEnabled ? "ACTIVE" : "DISABLED"}</strong></div>
        <div><span>Portfolio risk</span><strong>{workerSummary(status?.automaticPortfolioRiskEnabled, status?.automaticPortfolioRiskWorkerState)}</strong></div>
        <div><span>Trade-plan worker</span><strong>{workerSummary(status?.automaticTradePlanWorkerEnabled, status?.automaticTradePlanWorkerState)}</strong></div>
        <div><span>Trade-plan intake</span><strong>{status?.automaticTradePlanIntakeEnabled ? "ACTIVE" : "DISABLED"}</strong></div>
      </div>

      <div className="risk-authority-grid" aria-label="Risk authority boundaries">
        <div><span>Risk decision</span><strong>{status?.riskDecisionAuthority ? "AUTHORITATIVE" : "FALSE"}</strong></div>
        <div><span>Risk status</span><strong>{status?.riskStatusAuthority ? "AUTHORITATIVE" : "FALSE"}</strong></div>
        <div><span>Portfolio mode</span><strong>{status?.portfolioOperatingModeAuthority ? "AUTHORITATIVE" : "FALSE"}</strong></div>
        <div><span>Trade plan</span><strong>{status?.tradePlanAuthority ? "AUTHORITATIVE" : "FALSE"}</strong></div>
        <div><span>Position sizing</span><strong>{status?.positionSizingAuthority ? "AUTHORITATIVE" : "FALSE"}</strong></div>
        <div><span>Execution</span><strong>{status?.executionAuthority ? "ENABLED" : "FALSE"}</strong></div>
        <div><span>Broker submission</span><strong>{status?.brokerSubmissionAuthority ? "ENABLED" : "FALSE"}</strong></div>
      </div>

      {loadState === "unavailable" ? (
        <div className="risk-alert" role="alert">Risk Service status is unavailable. Treat risk readiness as unknown and fail closed.</div>
      ) : null}
    </section>
  );
}
