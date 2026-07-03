import { useEffect, useMemo, useState } from "react";
import "./pnl-workspace.css";

type PositionPnlSnapshot = {
  positionUid?: string;
  instrumentKey?: string;
  productType?: string;
  direction?: string;
  quantity?: number;
  averageOpenPrice?: number;
  markPrice?: number;
  marketValueAmount?: number;
  realizedPnlAmount?: number;
  unrealizedPnlAmount?: number;
  feesAmount?: number;
  taxesAmount?: number;
  netPnlAmount?: number;
  grossExposureAmount?: number;
  netExposureAmount?: number;
  valuedAtUtc?: string;
};

type PortfolioPnlSnapshot = {
  pnlSnapshotUid?: string;
  portfolioCode?: string;
  environment?: string;
  currencyCode?: string;
  realizedPnlAmount?: number;
  unrealizedPnlAmount?: number;
  grossPnlAmount?: number;
  feesAmount?: number;
  taxesAmount?: number;
  netPnlAmount?: number;
  grossExposureAmount?: number;
  netExposureAmount?: number;
  cashBalanceAmount?: number;
  netLiquidationValueAmount?: number;
  strategyDrawdownFraction?: number;
  portfolioDrawdownFraction?: number;
  asOfUtc?: string;
  generatedAtUtc?: string;
  positions?: PositionPnlSnapshot[];
};

type LoadState = "loading" | "ready" | "empty" | "stale" | "unavailable";

const apiBase = (import.meta.env.VITE_PORTFOLIO_API_BASE_URL ?? "").replace(/\/$/, "");
const portfolioCode = import.meta.env.VITE_PORTFOLIO_CODE ?? "PAPER-DEFAULT";
const maximumAgeMinutes = Number(import.meta.env.VITE_PNL_MAXIMUM_AGE_MINUTES ?? 10);

function money(value: number | undefined, currency: string) {
  if (value === undefined) return "—";
  return new Intl.NumberFormat(undefined, { style: "currency", currency }).format(value);
}

function numberValue(value: number | undefined) {
  return value === undefined ? "—" : new Intl.NumberFormat(undefined, { maximumFractionDigits: 4 }).format(value);
}

function percentage(value: number | undefined) {
  return value === undefined ? "—" : `${(value * 100).toFixed(2)}%`;
}

export function PnlWorkspace() {
  const [snapshot, setSnapshot] = useState<PortfolioPnlSnapshot | null>(null);
  const [state, setState] = useState<LoadState>("loading");
  const [observedAt, setObservedAt] = useState<Date | null>(null);

  useEffect(() => {
    let disposed = false;

    async function load() {
      try {
        const response = await fetch(
          `${apiBase}/api/v1/portfolio/${encodeURIComponent(portfolioCode)}/pnl/latest`,
          { headers: { Accept: "application/json" } },
        );

        if (disposed) return;
        if (response.status === 404) {
          setSnapshot(null);
          setObservedAt(new Date());
          setState("empty");
          return;
        }
        if (!response.ok) {
          setState("unavailable");
          return;
        }

        const nextSnapshot = (await response.json()) as PortfolioPnlSnapshot;
        setSnapshot(nextSnapshot);
        setObservedAt(new Date());

        const asOf = nextSnapshot.asOfUtc ? new Date(nextSnapshot.asOfUtc) : null;
        const stale =
          !asOf ||
          Number.isNaN(asOf.getTime()) ||
          Date.now() - asOf.getTime() > maximumAgeMinutes * 60_000;
        setState(stale ? "stale" : "ready");
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

  const currency = snapshot?.currencyCode ?? "INR";
  const freshness = useMemo(
    () => (observedAt ? `Checked ${observedAt.toLocaleTimeString()}` : "Awaiting Portfolio Service"),
    [observedAt],
  );

  return (
    <section className="pnl-workspace" aria-labelledby="pnl-heading">
      <div className="pnl-hero">
        <div>
          <p className="eyebrow">P&amp;L ANALYTICS</p>
          <h2 id="pnl-heading">Authoritative PAPER valuation snapshot</h2>
          <p>
            Latest snapshot for <strong>{portfolioCode}</strong>. This page does not fabricate history,
            synthetic chart series, or missing valuation values.
          </p>
        </div>
        <div className={`pnl-state pnl-state-${state}`} role="status">
          <strong>{state.toUpperCase()}</strong>
          <span>{freshness}</span>
        </div>
      </div>

      <div className="pnl-card-grid">
        <article className="pnl-card"><span>Net P&amp;L</span><strong>{money(snapshot?.netPnlAmount, currency)}</strong><p>Gross {money(snapshot?.grossPnlAmount, currency)}</p></article>
        <article className="pnl-card"><span>Realized</span><strong>{money(snapshot?.realizedPnlAmount, currency)}</strong><p>Unrealized {money(snapshot?.unrealizedPnlAmount, currency)}</p></article>
        <article className="pnl-card"><span>Fees &amp; taxes</span><strong>{money((snapshot?.feesAmount ?? 0) + (snapshot?.taxesAmount ?? 0), currency)}</strong><p>Fees {money(snapshot?.feesAmount, currency)} · Taxes {money(snapshot?.taxesAmount, currency)}</p></article>
        <article className="pnl-card"><span>Net liquidation value</span><strong>{money(snapshot?.netLiquidationValueAmount, currency)}</strong><p>Cash {money(snapshot?.cashBalanceAmount, currency)}</p></article>
        <article className="pnl-card"><span>Gross exposure</span><strong>{money(snapshot?.grossExposureAmount, currency)}</strong><p>Net {money(snapshot?.netExposureAmount, currency)}</p></article>
        <article className="pnl-card"><span>Strategy drawdown</span><strong>{percentage(snapshot?.strategyDrawdownFraction)}</strong><p>Portfolio {percentage(snapshot?.portfolioDrawdownFraction)}</p></article>
      </div>

      <div className="pnl-meta-grid">
        <div><span>Environment</span><strong>{snapshot?.environment ?? "PAPER"}</strong></div>
        <div><span>Currency</span><strong>{currency}</strong></div>
        <div><span>Valuation as of</span><strong>{snapshot?.asOfUtc ? new Date(snapshot.asOfUtc).toLocaleString() : "—"}</strong></div>
        <div><span>Generated at</span><strong>{snapshot?.generatedAtUtc ? new Date(snapshot.generatedAtUtc).toLocaleString() : "—"}</strong></div>
      </div>

      <article className="pnl-positions">
        <div className="pnl-section-heading">
          <div><p className="eyebrow">POSITION MARKS</p><h3>Position-level P&amp;L</h3></div>
          <span>{snapshot?.positions?.length ?? 0} positions</span>
        </div>
        {snapshot?.positions?.length ? (
          <div className="pnl-table-wrap">
            <table>
              <thead><tr><th>Instrument</th><th>Side</th><th>Qty</th><th>Avg / Mark</th><th>Market value</th><th>Realized</th><th>Unrealized</th><th>Net P&amp;L</th><th>Exposure</th></tr></thead>
              <tbody>
                {snapshot.positions.map((position) => (
                  <tr key={position.positionUid ?? `${position.instrumentKey}-${position.valuedAtUtc}`}>
                    <td><strong>{position.instrumentKey ?? "UNKNOWN"}</strong><span>{position.productType ?? "—"}</span></td>
                    <td>{position.direction ?? "—"}</td>
                    <td>{numberValue(position.quantity)}</td>
                    <td>{money(position.averageOpenPrice, currency)}<span>{money(position.markPrice, currency)}</span></td>
                    <td>{money(position.marketValueAmount, currency)}</td>
                    <td>{money(position.realizedPnlAmount, currency)}</td>
                    <td>{money(position.unrealizedPnlAmount, currency)}</td>
                    <td>{money(position.netPnlAmount, currency)}<span>Fees {money(position.feesAmount, currency)} · Tax {money(position.taxesAmount, currency)}</span></td>
                    <td>{money(position.grossExposureAmount, currency)}<span>Net {money(position.netExposureAmount, currency)}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <p className="pnl-empty">No authoritative position-level valuation is available.</p>}
      </article>

      {state === "empty" ? <div className="pnl-notice">No authoritative P&amp;L snapshot exists for this portfolio.</div> : null}
      {state === "stale" ? <div className="pnl-notice">The latest authoritative snapshot is older than the configured freshness threshold.</div> : null}
      {state === "unavailable" ? <div className="pnl-alert" role="alert">P&amp;L data is unavailable. Treat current valuation as unknown.</div> : null}
    </section>
  );
}
