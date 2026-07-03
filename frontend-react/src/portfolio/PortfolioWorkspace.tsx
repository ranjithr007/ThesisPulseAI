import { useEffect, useMemo, useState } from "react";
import "./portfolio-workspace.css";

type PortfolioStatus = {
  mode?: string;
  environment?: string;
  persistenceProvider?: string;
  sqlServerSourceOfTruth?: boolean;
  authority?: string;
  automaticFillProjectionAuthority?: boolean;
  automaticValuationAuthority?: boolean;
  markToMarketAuthority?: boolean;
  pnlSnapshotAuthority?: boolean;
  reconciliationAuthority?: boolean;
  exitOrderAuthority?: boolean;
  riskDecisionAuthority?: boolean;
  executionAuthority?: boolean;
  brokerAuthority?: boolean;
  livePortfolioAuthority?: boolean;
};

type JsonRecord = Record<string, unknown>;
type LoadState = "loading" | "ready" | "degraded" | "empty" | "unavailable";

const apiBase = (import.meta.env.VITE_PORTFOLIO_API_BASE_URL ?? "").replace(/\/$/, "");
const portfolioCode = import.meta.env.VITE_PORTFOLIO_CODE ?? "PAPER-DEFAULT";

export function PortfolioWorkspace() {
  const [status, setStatus] = useState<PortfolioStatus | null>(null);
  const [snapshot, setSnapshot] = useState<JsonRecord | null>(null);
  const [pnl, setPnl] = useState<JsonRecord | null>(null);
  const [state, setState] = useState<LoadState>("loading");
  const [observedAt, setObservedAt] = useState<Date | null>(null);

  useEffect(() => {
    let disposed = false;

    async function load() {
      try {
        const [statusResponse, snapshotResponse, pnlResponse] = await Promise.all([
          fetch(`${apiBase}/api/v1/status`, { headers: { Accept: "application/json" } }),
          fetch(`${apiBase}/api/v1/portfolio/${encodeURIComponent(portfolioCode)}`, { headers: { Accept: "application/json" } }),
          fetch(`${apiBase}/api/v1/portfolio/${encodeURIComponent(portfolioCode)}/pnl/latest`, { headers: { Accept: "application/json" } }),
        ]);

        if (!statusResponse.ok) {
          if (!disposed) setState("unavailable");
          return;
        }

        const nextStatus = (await statusResponse.json()) as PortfolioStatus;
        const nextSnapshot = snapshotResponse.ok ? ((await snapshotResponse.json()) as JsonRecord) : null;
        const nextPnl = pnlResponse.ok ? ((await pnlResponse.json()) as JsonRecord) : null;
        if (disposed) return;

        setStatus(nextStatus);
        setSnapshot(nextSnapshot);
        setPnl(nextPnl);
        setObservedAt(new Date());
        if (nextStatus.executionAuthority || nextStatus.brokerAuthority || nextStatus.livePortfolioAuthority) setState("degraded");
        else if (!nextSnapshot && !nextPnl) setState("empty");
        else setState("ready");
      } catch {
        if (!disposed) setState("unavailable");
      }
    }

    void load();
    const interval = window.setInterval(load, 30_000);
    return () => {
      disposed = true;
      window.clearInterval(interval);
    };
  }, []);

  const freshness = useMemo(() => observedAt ? `Updated ${observedAt.toLocaleTimeString()}` : "Awaiting Portfolio Service", [observedAt]);

  return (
    <section className="portfolio-workspace" aria-labelledby="portfolio-heading">
      <div className="portfolio-hero">
        <div>
          <p className="eyebrow">PORTFOLIO LEDGER</p>
          <h2 id="portfolio-heading">PAPER positions and valuation visibility</h2>
          <p>Read-only ledger and P&amp;L visibility for <strong>{portfolioCode}</strong>. No projection, reconciliation, correction, order, broker, or execution action is available here.</p>
        </div>
        <div className={`portfolio-state portfolio-state-${state}`} role="status"><strong>{state.toUpperCase()}</strong><span>{freshness}</span></div>
      </div>

      <div className="portfolio-card-grid">
        <article className="portfolio-card"><span>Mode</span><strong>{status?.mode ?? "UNKNOWN"}</strong><p>{status?.environment ?? "PAPER"}</p></article>
        <article className="portfolio-card"><span>Persistence</span><strong>{status?.persistenceProvider ?? "UNKNOWN"}</strong><p>SQL source of truth: {status?.sqlServerSourceOfTruth ? "YES" : "NO"}</p></article>
        <article className="portfolio-card"><span>Fill projection</span><strong>{status?.automaticFillProjectionAuthority ? "ACTIVE" : "DISABLED"}</strong><p>Service authority only.</p></article>
        <article className="portfolio-card"><span>Valuation</span><strong>{status?.automaticValuationAuthority ? "ACTIVE" : "DISABLED"}</strong><p>Mark to market: {status?.markToMarketAuthority ? "YES" : "NO"}</p></article>
      </div>

      <div className="portfolio-data-grid">
        <article><h3>Portfolio snapshot</h3>{snapshot ? <pre>{JSON.stringify(snapshot, null, 2)}</pre> : <p>No authoritative portfolio snapshot is available.</p>}</article>
        <article><h3>Latest P&amp;L snapshot</h3>{pnl ? <pre>{JSON.stringify(pnl, null, 2)}</pre> : <p>No authoritative P&amp;L snapshot is available.</p>}</article>
      </div>

      <div className="portfolio-authority-grid" aria-label="Portfolio authority boundaries">
        <div><span>Ledger/valuation</span><strong>{status?.authority ?? "UNKNOWN"}</strong></div>
        <div><span>P&amp;L snapshot</span><strong>{status?.pnlSnapshotAuthority ? "AUTHORITATIVE" : "FALSE"}</strong></div>
        <div><span>Reconciliation</span><strong>{status?.reconciliationAuthority ? "AUTHORITATIVE" : "FALSE"}</strong></div>
        <div><span>Exit orders</span><strong>{status?.exitOrderAuthority ? "ENABLED" : "FALSE"}</strong></div>
        <div><span>Risk decisions</span><strong>{status?.riskDecisionAuthority ? "ENABLED" : "FALSE"}</strong></div>
        <div><span>Execution</span><strong>{status?.executionAuthority ? "ENABLED" : "FALSE"}</strong></div>
        <div><span>Broker</span><strong>{status?.brokerAuthority ? "ENABLED" : "FALSE"}</strong></div>
        <div><span>Live portfolio</span><strong>{status?.livePortfolioAuthority ? "ENABLED" : "FALSE"}</strong></div>
      </div>

      {state === "unavailable" ? <div className="portfolio-alert" role="alert">Portfolio Service is unavailable. Treat portfolio and P&amp;L state as unknown.</div> : null}
    </section>
  );
}
