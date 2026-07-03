import { useEffect, useMemo, useState } from "react";
import "./market-command-center.css";

type PlatformStatus = {
  environment?: string;
  optionChainFusionEnabled?: boolean;
  optionChainSqlOperationsEnabled?: boolean;
  optionChainSchedulerEnabled?: boolean;
  optionChainSelectionAuthority?: boolean;
  optionChainExecutionAuthority?: boolean;
  optionChainActivationMode?: string;
};

type LoadState = "loading" | "ready" | "degraded" | "unavailable";

const apiBaseUrl = (import.meta.env.VITE_SIGNAL_API_BASE_URL ?? "").replace(/\/$/, "");

export function MarketCommandCenter() {
  const [status, setStatus] = useState<PlatformStatus | null>(null);
  const [loadState, setLoadState] = useState<LoadState>("loading");
  const [observedAt, setObservedAt] = useState<Date | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    async function loadStatus() {
      try {
        const response = await fetch(`${apiBaseUrl}/api/v1/status`, {
          signal: controller.signal,
          headers: { Accept: "application/json" },
        });

        if (!response.ok) {
          setLoadState("unavailable");
          return;
        }

        const nextStatus = (await response.json()) as PlatformStatus;
        setStatus(nextStatus);
        setObservedAt(new Date());
        setLoadState(
          nextStatus.optionChainExecutionAuthority === false &&
            nextStatus.optionChainSelectionAuthority === false
            ? "ready"
            : "degraded",
        );
      } catch (error) {
        if ((error as Error).name !== "AbortError") {
          setLoadState("unavailable");
        }
      }
    }

    void loadStatus();
    const interval = window.setInterval(loadStatus, 30_000);

    return () => {
      controller.abort();
      window.clearInterval(interval);
    };
  }, []);

  const freshness = useMemo(() => {
    if (!observedAt) return "Awaiting status";
    return `Updated ${observedAt.toLocaleTimeString()}`;
  }, [observedAt]);

  return (
    <section className="market-command-center" aria-labelledby="market-heading">
      <div className="market-hero">
        <div>
          <p className="eyebrow">MARKET COMMAND CENTER</p>
          <h2 id="market-heading">Live-readiness before market decisions</h2>
          <p>
            Read-only operational visibility for market data, option-chain intelligence,
            and rollout safety. This surface cannot place or authorize trades.
          </p>
        </div>
        <div className={`market-state market-state-${loadState}`} role="status">
          <strong>{loadState.toUpperCase()}</strong>
          <span>{freshness}</span>
        </div>
      </div>

      <div className="market-card-grid">
        <article className="market-card">
          <span className="market-card-label">Environment</span>
          <strong>{status?.environment ?? "PAPER"}</strong>
          <p>No real broker orders are permitted from this UI.</p>
        </article>
        <article className="market-card">
          <span className="market-card-label">Option-chain intelligence</span>
          <strong>{status?.optionChainFusionEnabled ? "ONLINE" : "DISABLED"}</strong>
          <p>Independent evidence only; no direct signal authority.</p>
        </article>
        <article className="market-card">
          <span className="market-card-label">SQL operations</span>
          <strong>{status?.optionChainSqlOperationsEnabled ? "READY" : "DISABLED"}</strong>
          <p>Durable rollout state and scheduler controls.</p>
        </article>
        <article className="market-card">
          <span className="market-card-label">Scheduler</span>
          <strong>{status?.optionChainSchedulerEnabled ? "ACTIVE" : "STOPPED"}</strong>
          <p>Database-leased operational jobs only.</p>
        </article>
      </div>

      <div className="authority-panel" aria-label="Authority boundaries">
        <div>
          <span>Selection authority</span>
          <strong>{status?.optionChainSelectionAuthority ? "ENABLED" : "FALSE"}</strong>
        </div>
        <div>
          <span>Execution authority</span>
          <strong>{status?.optionChainExecutionAuthority ? "ENABLED" : "FALSE"}</strong>
        </div>
        <div>
          <span>Activation mode</span>
          <strong>{status?.optionChainActivationMode ?? "DISABLED"}</strong>
        </div>
      </div>

      {loadState === "unavailable" ? (
        <div className="market-alert" role="alert">
          Platform status is unavailable. Treat market readiness as unknown and fail closed.
        </div>
      ) : null}
    </section>
  );
}
