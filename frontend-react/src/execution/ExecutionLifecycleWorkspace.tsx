import { useCallback, useEffect, useMemo, useState } from "react";

import {
  classifyLifecycleDetail,
  classifyLifecycleList,
  humanizeLifecycleCode,
  lifecycleCompletion,
  lifecycleTone,
} from "./lifecycleState";
import type {
  LifecycleLoadState,
  PaperTradeLifecycleAcceptanceReport,
  PaperTradeLifecycleDetail,
  PaperTradeLifecycleList,
  PaperTradeLifecycleSummary,
} from "./types";
import "./execution-lifecycle.css";

const executionApiBaseUrl =
  import.meta.env.VITE_EXECUTION_API_BASE_URL?.replace(/\/$/, "") ??
  "http://localhost:59482";
const portfolioCode = import.meta.env.VITE_PORTFOLIO_CODE ?? "PAPER-DEFAULT";
const currencyCode = import.meta.env.VITE_PORTFOLIO_CURRENCY ?? "INR";
const lifecycleLimit = Number(import.meta.env.VITE_EXECUTION_LIFECYCLE_LIMIT ?? 50);

function formatDate(value: string | null | undefined): string {
  if (!value) return "Not available";
  const timestamp = Date.parse(value);
  if (Number.isNaN(timestamp)) return value;
  return new Intl.DateTimeFormat("en-IN", {
    dateStyle: "medium",
    timeStyle: "medium",
    timeZone: "Asia/Kolkata",
  }).format(timestamp);
}

function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return new Intl.NumberFormat("en-IN", { maximumFractionDigits: 4 }).format(value);
}

function formatMoney(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency: currencyCode,
    maximumFractionDigits: 2,
  }).format(value);
}

async function readJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    headers: { Accept: "application/json" },
    signal,
  });
  if (!response.ok) {
    const error = new Error(`Execution API returned HTTP ${response.status}.`);
    Object.assign(error, { status: response.status });
    throw error;
  }
  return (await response.json()) as T;
}

function StatusBadge({ value }: { value: string }) {
  const tone = lifecycleTone(value);
  return (
    <span className={`execution-status execution-status-${tone}`}>
      {humanizeLifecycleCode(value)}
    </span>
  );
}

function stateLabel(state: LifecycleLoadState): string {
  return state === "unavailable" ? "UNAVAILABLE" : state.toUpperCase();
}

export function ExecutionLifecycleWorkspace() {
  const [items, setItems] = useState<PaperTradeLifecycleSummary[]>([]);
  const [selectedCorrelationUid, setSelectedCorrelationUid] = useState<string | null>(null);
  const [detail, setDetail] = useState<PaperTradeLifecycleDetail | null>(null);
  const [acceptance, setAcceptance] = useState<PaperTradeLifecycleAcceptanceReport | null>(null);
  const [listState, setListState] = useState<LifecycleLoadState>("loading");
  const [detailState, setDetailState] = useState<LifecycleLoadState>("loading");
  const [acceptanceState, setAcceptanceState] = useState<LifecycleLoadState>("loading");
  const [error, setError] = useState<string | null>(null);
  const [acceptanceError, setAcceptanceError] = useState<string | null>(null);
  const [lastRefreshAtUtc, setLastRefreshAtUtc] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const listUrl = useMemo(() => {
    const parameters = new URLSearchParams({
      portfolioCode,
      limit: String(Number.isFinite(lifecycleLimit) ? lifecycleLimit : 50),
    });
    return `${executionApiBaseUrl}/api/v1/execution/lifecycles?${parameters.toString()}`;
  }, []);

  const loadList = useCallback(async (signal?: AbortSignal) => {
    const payload = await readJson<PaperTradeLifecycleList>(listUrl, signal);
    setItems(payload.items);
    setListState(classifyLifecycleList(payload.items));
    setSelectedCorrelationUid((current) => {
      if (current && payload.items.some((item) => item.correlationUid === current)) {
        return current;
      }
      return payload.items[0]?.correlationUid ?? null;
    });
    setLastRefreshAtUtc(new Date().toISOString());
    setError(null);
  }, [listUrl]);

  const loadDetail = useCallback(async (
    correlationUid: string,
    signal?: AbortSignal,
  ) => {
    const parameters = new URLSearchParams({ portfolioCode });
    const payload = await readJson<PaperTradeLifecycleDetail>(
      `${executionApiBaseUrl}/api/v1/execution/lifecycles/${encodeURIComponent(correlationUid)}?${parameters.toString()}`,
      signal,
    );
    setDetail(payload);
    setDetailState(classifyLifecycleDetail(payload));
    setError(null);
  }, []);

  const loadAcceptance = useCallback(async (
    correlationUid: string,
    signal?: AbortSignal,
  ) => {
    const parameters = new URLSearchParams({ portfolioCode });
    const payload = await readJson<PaperTradeLifecycleAcceptanceReport>(
      `${executionApiBaseUrl}/api/v1/execution/lifecycles/${encodeURIComponent(correlationUid)}/acceptance?${parameters.toString()}`,
      signal,
    );
    setAcceptance(payload);
    setAcceptanceState(payload.outcome === "PASS" ? "ready" : payload.outcome === "INCOMPLETE" ? "stale" : "unavailable");
    setAcceptanceError(null);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setListState("loading");
    void loadList(controller.signal).catch((requestError: unknown) => {
      if (controller.signal.aborted) return;
      setListState("unavailable");
      setError(requestError instanceof Error ? requestError.message : "Lifecycle data is unavailable.");
    });

    const timer = window.setInterval(() => {
      void loadList().catch((requestError: unknown) => {
        setListState("unavailable");
        setError(requestError instanceof Error ? requestError.message : "Lifecycle data is unavailable.");
      });
    }, 15_000);

    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [loadList]);

  useEffect(() => {
    if (!selectedCorrelationUid) {
      setDetail(null);
      setAcceptance(null);
      setDetailState(items.length === 0 ? "empty" : "loading");
      setAcceptanceState(items.length === 0 ? "empty" : "loading");
      return;
    }

    const controller = new AbortController();
    setDetailState("loading");
    setAcceptanceState("loading");

    void loadDetail(selectedCorrelationUid, controller.signal).catch((requestError: unknown) => {
      if (controller.signal.aborted) return;
      const status = (requestError as { status?: number }).status;
      setDetail(null);
      setDetailState(status === 404 ? "empty" : "unavailable");
      setError(requestError instanceof Error ? requestError.message : "Lifecycle detail is unavailable.");
    });

    void loadAcceptance(selectedCorrelationUid, controller.signal).catch((requestError: unknown) => {
      if (controller.signal.aborted) return;
      const status = (requestError as { status?: number }).status;
      setAcceptance(null);
      setAcceptanceState(status === 404 ? "empty" : "unavailable");
      setAcceptanceError(
        requestError instanceof Error
          ? requestError.message
          : "Lifecycle acceptance evidence is unavailable.",
      );
    });

    return () => controller.abort();
  }, [items.length, loadAcceptance, loadDetail, selectedCorrelationUid]);

  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await loadList();
      if (selectedCorrelationUid) {
        await Promise.all([
          loadDetail(selectedCorrelationUid),
          loadAcceptance(selectedCorrelationUid),
        ]);
      }
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : "Lifecycle refresh failed.");
    } finally {
      setRefreshing(false);
    }
  }, [loadAcceptance, loadDetail, loadList, selectedCorrelationUid]);

  const metrics = useMemo(() => ({
    total: items.length,
    complete: items.filter((item) => item.lifecycleStatus === "COMPLETE").length,
    attention: items.filter((item) =>
      ["FAILED", "REJECTED", "PARTIAL_LINEAGE"].includes(item.lifecycleStatus),
    ).length,
    stale: items.filter((item) => item.isStale).length,
  }), [items]);

  const completion = detail ? lifecycleCompletion(detail.stages) : { complete: 0, total: 0 };
  const effectiveState = acceptance?.operationalStates[0] ?? null;

  return (
    <section className="execution-workspace" aria-labelledby="execution-title">
      <div className="execution-hero">
        <div>
          <p className="eyebrow">PAPER EXECUTION OBSERVABILITY</p>
          <h2 id="execution-title">Trade lifecycle and acceptance proof</h2>
          <p>
            Read-only trace and deterministic acceptance checks from canonical signal through
            execution, position, P&amp;L, and applicable operating controls for <strong>{portfolioCode}</strong>.
          </p>
        </div>
        <div className="execution-hero-actions">
          <div className={`execution-page-state execution-page-state-${listState}`} role="status">
            <strong>{stateLabel(listState)}</strong>
            <span>{lastRefreshAtUtc ? `Checked ${formatDate(lastRefreshAtUtc)}` : "Awaiting Execution Service"}</span>
          </div>
          <button type="button" className="refresh-button" disabled={refreshing} onClick={() => void refresh()}>
            {refreshing ? "Refreshing..." : "Refresh now"}
          </button>
        </div>
      </div>

      {error ? (
        <div className="execution-alert" role="alert">
          <strong>Lifecycle warning</strong>
          <span>{error} Treat missing execution or P&amp;L evidence as unknown.</span>
        </div>
      ) : null}

      <div className="execution-metrics">
        <article><span>Recent lifecycles</span><strong>{metrics.total}</strong><small>Bounded authoritative records</small></article>
        <article><span>Complete through P&amp;L</span><strong>{metrics.complete}</strong><small>Latest valuation linked</small></article>
        <article><span>Needs attention</span><strong>{metrics.attention}</strong><small>Rejected, failed, or partial lineage</small></article>
        <article><span>Stale</span><strong>{metrics.stale}</strong><small>Past the backend freshness threshold</small></article>
      </div>

      <div className="execution-layout">
        <article className="execution-panel execution-list-panel">
          <div className="execution-section-heading">
            <div><p className="eyebrow">RECENT PAPER FLOW</p><h3>Lifecycle records</h3></div>
            <span>{items.length} records</span>
          </div>

          {listState === "loading" ? <div className="execution-empty">Loading authoritative lifecycles…</div> : null}
          {listState === "unavailable" ? <div className="execution-empty">Execution lifecycle data is unavailable.</div> : null}
          {listState === "empty" ? <div className="execution-empty">No PAPER lifecycle records exist for this portfolio.</div> : null}

          {items.length > 0 ? (
            <div className="execution-table-wrap">
              <table>
                <thead>
                  <tr><th>Instrument</th><th>Stage</th><th>Status</th><th>Fill</th><th>P&amp;L</th><th>Last activity</th></tr>
                </thead>
                <tbody>
                  {items.map((item) => (
                    <tr className={item.correlationUid === selectedCorrelationUid ? "selected" : ""} key={`${item.correlationUid}-${item.tradePlanUid}`}>
                      <td>
                        <button className="execution-row-button" type="button" onClick={() => setSelectedCorrelationUid(item.correlationUid)}>
                          <strong>{item.instrumentKey}</strong>
                          <span>{item.direction} · {item.strategyCode}</span>
                        </button>
                      </td>
                      <td>{humanizeLifecycleCode(item.lifecycleStage)}</td>
                      <td><StatusBadge value={item.isStale ? "STALE" : item.lifecycleStatus} /></td>
                      <td>{formatNumber(item.filledQuantity)}<span>{item.fillCount} fill event{item.fillCount === 1 ? "" : "s"}</span></td>
                      <td>{formatMoney(item.netPnlAmount)}</td>
                      <td>{formatDate(item.lastActivityAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
        </article>

        <article className="execution-panel execution-detail-panel">
          <div className="execution-section-heading">
            <div><p className="eyebrow">SELECTED LINEAGE</p><h3>Lifecycle timeline</h3></div>
            <span>{completion.complete}/{completion.total} stages available</span>
          </div>

          {detailState === "loading" ? <div className="execution-empty">Loading lifecycle detail…</div> : null}
          {detailState === "unavailable" ? <div className="execution-empty">Lifecycle detail is unavailable.</div> : null}
          {detailState === "empty" ? <div className="execution-empty">Select a lifecycle record to inspect its lineage.</div> : null}

          {detail ? (
            <>
              <div className="execution-detail-summary">
                <div><span>Instrument</span><strong>{detail.summary.instrumentKey}</strong></div>
                <div><span>Lifecycle</span><StatusBadge value={detail.summary.lifecycleStatus} /></div>
                <div><span>Requested / filled</span><strong>{formatNumber(detail.summary.requestedQuantity)} / {formatNumber(detail.summary.filledQuantity)}</strong></div>
                <div><span>Average fill</span><strong>{formatMoney(detail.summary.averageFillPrice)}</strong></div>
              </div>

              <div className="execution-timeline">
                {detail.stages.map((stage, index) => (
                  <article className={`execution-stage execution-stage-${lifecycleTone(stage.status)}`} key={`${stage.stage}-${index}`}>
                    <div className="execution-stage-marker"><span>{index + 1}</span></div>
                    <div className="execution-stage-content">
                      <div><strong>{humanizeLifecycleCode(stage.stage)}</strong><StatusBadge value={stage.status} /></div>
                      <p>{formatDate(stage.occurredAtUtc)}</p>
                      {stage.entityUid ? <code>{stage.entityUid}</code> : <small>No authoritative entity UID is available.</small>}
                      {stage.reasonCode || stage.reasonMessage ? (
                        <div className="execution-stage-reason">
                          <strong>{stage.reasonCode ?? "Stage reason"}</strong>
                          <span>{stage.reasonMessage ?? "No reason message supplied."}</span>
                        </div>
                      ) : null}
                    </div>
                  </article>
                ))}
              </div>

              <div className="execution-lineage-grid">
                <div><span>Correlation UID</span><code>{detail.summary.correlationUid}</code></div>
                <div><span>Signal UID</span><code>{detail.summary.signalUid}</code></div>
                <div><span>Thesis UID</span><code>{detail.summary.thesisUid ?? "Not available"}</code></div>
                <div><span>Risk decision UID</span><code>{detail.summary.riskDecisionUid ?? "Not available"}</code></div>
                <div><span>Trade plan UID</span><code>{detail.summary.tradePlanUid}</code></div>
                <div><span>Execution command UID</span><code>{detail.summary.executionCommandUid ?? "Not available"}</code></div>
                <div><span>Order UID</span><code>{detail.summary.orderUid ?? "Not available"}</code></div>
                <div><span>Position UID</span><code>{detail.summary.positionUid ?? "Not available"}</code></div>
                <div><span>P&amp;L snapshot UID</span><code>{detail.summary.pnlSnapshotUid ?? "Not available"}</code></div>
              </div>

              {detail.summary.warnings.length > 0 ? (
                <div className="execution-warning-list">
                  <strong>Authoritative warnings</strong>
                  {detail.summary.warnings.map((warning) => <span key={warning}>{humanizeLifecycleCode(warning)}</span>)}
                </div>
              ) : null}
            </>
          ) : null}
        </article>
      </div>

      <article className="execution-panel execution-acceptance-panel">
        <div className="execution-section-heading">
          <div><p className="eyebrow">ARCHITECTURE EXIT GATE</p><h3>PAPER lifecycle acceptance</h3></div>
          {acceptance ? <StatusBadge value={acceptance.outcome} /> : <span>{stateLabel(acceptanceState)}</span>}
        </div>

        {acceptanceError ? <div className="execution-alert"><strong>Acceptance unavailable</strong><span>{acceptanceError}</span></div> : null}
        {acceptanceState === "loading" ? <div className="execution-empty">Evaluating authoritative lineage and controls…</div> : null}
        {acceptanceState === "empty" ? <div className="execution-empty">No acceptance report exists for the selected correlation.</div> : null}

        {acceptance ? (
          <>
            <div className="execution-acceptance-summary">
              <div><span>Outcome</span><StatusBadge value={acceptance.outcome} /></div>
              <div><span>Environment</span><strong>{acceptance.environment}</strong></div>
              <div><span>Evaluated</span><strong>{formatDate(acceptance.evaluatedAtUtc)}</strong></div>
              <div><span>Effective mode</span><strong>{effectiveState?.effectiveOperatingMode ?? "Not available"}</strong></div>
            </div>

            <div className="execution-acceptance-checks">
              {acceptance.checks.map((check) => (
                <article key={check.code} className={`execution-acceptance-check execution-acceptance-check-${lifecycleTone(check.outcome)}`}>
                  <div><strong>{humanizeLifecycleCode(check.code)}</strong><StatusBadge value={check.outcome} /></div>
                  <p>{check.message}</p>
                  {check.evidenceReferences.length > 0 ? (
                    <details>
                      <summary>{check.evidenceReferences.length} evidence reference{check.evidenceReferences.length === 1 ? "" : "s"}</summary>
                      {check.evidenceReferences.map((reference) => <code key={reference}>{reference}</code>)}
                    </details>
                  ) : null}
                </article>
              ))}
            </div>

            <div className="execution-evidence-columns">
              <section>
                <div className="execution-section-heading"><h4>Correlation and causation</h4><span>{acceptance.lineage.length} stages</span></div>
                <div className="execution-lineage-audit">
                  {acceptance.lineage.map((evidence) => (
                    <article key={`${evidence.stage}-${evidence.entityUid}`}>
                      <div><strong>{humanizeLifecycleCode(evidence.stage)}</strong><span>{formatDate(evidence.occurredAtUtc)}</span></div>
                      <small>{evidence.sourceTable}</small>
                      <code>entity: {evidence.entityUid}</code>
                      <code>correlation: {evidence.correlationUid}</code>
                      <code>causation: {evidence.causationUid ?? "Not available"}</code>
                    </article>
                  ))}
                </div>
              </section>

              <section>
                <div className="execution-section-heading"><h4>Applicable operating states</h4><span>{acceptance.operationalStates.length} scopes</span></div>
                <div className="execution-control-audit">
                  {acceptance.operationalStates.length === 0 ? <div className="execution-empty">No authoritative operating-state evidence is available.</div> : null}
                  {acceptance.operationalStates.map((state) => (
                    <article key={`${state.scopeType}-${state.scopeId}`}>
                      <div><strong>{state.scopeType}: {state.scopeId}</strong><StatusBadge value={state.effectiveOperatingMode} /></div>
                      <span>New exposure: {state.allowsNewExposure ? "Allowed" : "Blocked"}</span>
                      <span>Risk-reducing exits: {state.allowsRiskReducingExits ? "Allowed" : "Blocked"}</span>
                      <span>Operator review: {state.requiresOperatorReview ? "Required" : "Not required"}</span>
                      <code>control: {state.sourceControlUid ?? "NORMAL projection"}</code>
                      <small>{formatDate(state.evaluatedAtUtc)} · {state.evaluationVersion}</small>
                    </article>
                  ))}
                </div>
              </section>
            </div>
          </>
        ) : null}
      </article>

      <aside className="safety-note">
        <strong>Read-only PAPER proof</strong>
        <span>
          This workspace cannot submit, modify, cancel, retry, approve, or override orders, risk decisions,
          or operational controls. Missing evidence remains incomplete rather than being inferred.
          {" "}<a href="#/portfolio">Portfolio</a> · <a href="#/pnl">P&amp;L</a> · <a href="#/operations">Operations</a>
        </span>
      </aside>
    </section>
  );
}
