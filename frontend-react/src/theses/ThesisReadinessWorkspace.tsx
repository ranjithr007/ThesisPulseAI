import { useEffect, useMemo, useState } from "react";
import "./thesis-readiness-workspace.css";

type ThesisStatus = {
  mode?: string;
  environment?: string;
  immutableVersioningRequired?: boolean;
  evaluationEnabled?: boolean;
  signalProjectionEnabled?: boolean;
  signalProjectionContractVersion?: string;
  authority?: string;
  riskAuthority?: boolean;
  tradePlanAuthority?: boolean;
  executionAuthority?: boolean;
};

type LoadState = "loading" | "ready" | "degraded" | "unavailable";

const thesisApiBaseUrl = (import.meta.env.VITE_THESIS_API_BASE_URL ?? "").replace(/\/$/, "");

export function ThesisReadinessWorkspace() {
  const [status, setStatus] = useState<ThesisStatus | null>(null);
  const [loadState, setLoadState] = useState<LoadState>("loading");
  const [observedAt, setObservedAt] = useState<Date | null>(null);

  useEffect(() => {
    let disposed = false;

    async function loadStatus() {
      try {
        const response = await fetch(`${thesisApiBaseUrl}/api/v1/status`, {
          headers: { Accept: "application/json" },
        });

        if (!response.ok) {
          if (!disposed) setLoadState("unavailable");
          return;
        }

        const nextStatus = (await response.json()) as ThesisStatus;
        if (disposed) return;

        setStatus(nextStatus);
        setObservedAt(new Date());
        setLoadState(
          nextStatus.riskAuthority === false &&
            nextStatus.tradePlanAuthority === false &&
            nextStatus.executionAuthority === false
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
    if (!observedAt) return "Awaiting Thesis Service";
    return `Updated ${observedAt.toLocaleTimeString()}`;
  }, [observedAt]);

  return (
    <section className="thesis-workspace" aria-labelledby="thesis-heading">
      <div className="thesis-hero">
        <div>
          <p className="eyebrow">THESIS READINESS</p>
          <h2 id="thesis-heading">Validated reasoning, visible boundaries</h2>
          <p>
            Read-only visibility into deterministic thesis fusion and signal projection readiness.
            This phase does not submit evaluations or create thesis records.
          </p>
        </div>
        <div className={`thesis-state thesis-state-${loadState}`} role="status">
          <strong>{loadState.toUpperCase()}</strong>
          <span>{freshness}</span>
        </div>
      </div>

      <div className="thesis-card-grid">
        <article className="thesis-card">
          <span>Fusion mode</span>
          <strong>{status?.mode ?? "UNKNOWN"}</strong>
          <p>Deterministic evidence fusion only.</p>
        </article>
        <article className="thesis-card">
          <span>Environment</span>
          <strong>{status?.environment ?? "PAPER"}</strong>
          <p>No live broker execution from this workspace.</p>
        </article>
        <article className="thesis-card">
          <span>Evaluation</span>
          <strong>{status?.evaluationEnabled ? "AVAILABLE" : "DISABLED"}</strong>
          <p>Service capability only; no UI submission in Phase 5.1.</p>
        </article>
        <article className="thesis-card">
          <span>Signal projection</span>
          <strong>{status?.signalProjectionEnabled ? "AVAILABLE" : "DISABLED"}</strong>
          <p>{status?.signalProjectionContractVersion ?? "Contract unavailable"}</p>
        </article>
      </div>

      <div className="thesis-boundaries" aria-label="Thesis authority boundaries">
        <div>
          <span>Candidate authority</span>
          <strong>{status?.authority ?? "UNKNOWN"}</strong>
        </div>
        <div>
          <span>Risk authority</span>
          <strong>{status?.riskAuthority ? "ENABLED" : "FALSE"}</strong>
        </div>
        <div>
          <span>Trade-plan authority</span>
          <strong>{status?.tradePlanAuthority ? "ENABLED" : "FALSE"}</strong>
        </div>
        <div>
          <span>Execution authority</span>
          <strong>{status?.executionAuthority ? "ENABLED" : "FALSE"}</strong>
        </div>
      </div>

      <div className="thesis-notice">
        <strong>History not available yet</strong>
        <p>
          Thesis Service currently exposes deterministic evaluation and status endpoints, but no
          durable thesis-list endpoint. This UI intentionally does not fabricate thesis history.
        </p>
      </div>

      {loadState === "unavailable" ? (
        <div className="thesis-alert" role="alert">
          Thesis Service status is unavailable. Treat thesis readiness as unknown and fail closed.
        </div>
      ) : null}
    </section>
  );
}
