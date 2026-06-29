import { useCallback, useEffect, useMemo, useState } from "react";

import type {
  PlatformHealthSnapshot,
  SignalExpiryJobStatus,
} from "./types";

const operationsApiBaseUrl =
  import.meta.env.VITE_OPERATIONS_API_BASE_URL?.replace(/\/$/, "") ??
  "http://localhost:5107";

function formatTime(value: string | null): string {
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

async function readJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    headers: { Accept: "application/json" },
    signal,
  });
  const payload = (await response.json()) as T;

  if (!response.ok && response.status !== 503) {
    throw new Error(`Operations API returned HTTP ${response.status}.`);
  }

  return payload;
}

export function OperationsDashboard() {
  const [job, setJob] = useState<SignalExpiryJobStatus | null>(null);
  const [health, setHealth] = useState<PlatformHealthSnapshot | null>(null);
  const [lastRefreshAtUtc, setLastRefreshAtUtc] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    setRefreshing(true);

    try {
      const [jobStatus, platformHealth] = await Promise.all([
        readJson<SignalExpiryJobStatus>(
          `${operationsApiBaseUrl}/api/v1/jobs/signal-expiry`,
          signal,
        ),
        readJson<PlatformHealthSnapshot>(
          `${operationsApiBaseUrl}/api/v1/platform/health`,
          signal,
        ),
      ]);

      setJob(jobStatus);
      setHealth(platformHealth);
      setLastRefreshAtUtc(new Date().toISOString());
      setError(null);
    } catch (requestError) {
      if (!signal?.aborted) {
        setError(
          requestError instanceof Error
            ? requestError.message
            : "Operations data could not be loaded.",
        );
      }
    } finally {
      if (!signal?.aborted) {
        setRefreshing(false);
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);

    const timer = window.setInterval(() => {
      void refresh();
    }, 10_000);

    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [refresh]);

  const healthyDependencies = useMemo(
    () => health?.dependencies.filter((item) => item.status === "HEALTHY").length ?? 0,
    [health],
  );

  return (
    <section className="operations-page" aria-labelledby="operations-title">
      <div className="operations-heading">
        <div>
          <p className="eyebrow">PLATFORM OPERATIONS</p>
          <h2 id="operations-title">Operations Dashboard</h2>
          <p>
            Scheduler state, expiry activity, downstream readiness, and correlation
            diagnostics for the PAPER environment.
          </p>
        </div>
        <button
          className="refresh-button"
          type="button"
          disabled={refreshing}
          onClick={() => void refresh()}
        >
          {refreshing ? "Refreshing..." : "Refresh now"}
        </button>
      </div>

      {error ? (
        <div className="scanner-alert" role="alert">
          <strong>Operations warning</strong>
          <span>{error}</span>
        </div>
      ) : null}

      <div className="operations-metrics">
        <article>
          <span>Platform health</span>
          <strong>{health?.status ?? "UNKNOWN"}</strong>
          <small>{healthyDependencies}/{health?.dependencies.length ?? 0} dependencies ready</small>
        </article>
        <article>
          <span>Expiry scheduler</span>
          <strong>{job?.enabled ? "ENABLED" : "DISABLED"}</strong>
          <small>{job ? `Every ${job.intervalSeconds}s` : "Awaiting status"}</small>
        </article>
        <article>
          <span>Last run</span>
          <strong>{job?.lastRun.status ?? "UNKNOWN"}</strong>
          <small>{formatTime(job?.lastRun.completedAtUtc ?? null)}</small>
        </article>
        <article>
          <span>Last refresh</span>
          <strong>{lastRefreshAtUtc ? "CURRENT" : "PENDING"}</strong>
          <small>{formatTime(lastRefreshAtUtc)}</small>
        </article>
      </div>

      <div className="operations-grid">
        <article className="operations-card">
          <div className="card-heading-row">
            <div>
              <p className="eyebrow">SCHEDULED JOB</p>
              <h3>Signal expiry</h3>
            </div>
            <span className={`job-state ${(job?.lastRun.status ?? "not_run").toLowerCase()}`}>
              {job?.lastRun.status ?? "NOT_RUN"}
            </span>
          </div>

          <dl className="operations-definition-list">
            <div><dt>Enabled</dt><dd>{job?.enabled ? "Yes" : "No"}</dd></div>
            <div><dt>Interval</dt><dd>{job?.intervalSeconds ?? 0} seconds</dd></div>
            <div><dt>Batch size</dt><dd>{job?.batchSize ?? 0}</dd></div>
            <div><dt>Started</dt><dd>{formatTime(job?.lastRun.startedAtUtc ?? null)}</dd></div>
            <div><dt>Completed</dt><dd>{formatTime(job?.lastRun.completedAtUtc ?? null)}</dd></div>
            <div><dt>Selected</dt><dd>{job?.lastRun.selected ?? 0}</dd></div>
            <div><dt>Expired</dt><dd>{job?.lastRun.expired ?? 0}</dd></div>
            <div><dt>Published</dt><dd>{job?.lastRun.published ?? 0}</dd></div>
            <div><dt>Publication failures</dt><dd>{job?.lastRun.publicationFailures ?? 0}</dd></div>
            <div className="full-row"><dt>Correlation ID</dt><dd className="monospace">{job?.lastRun.correlationId ?? "Not available"}</dd></div>
            <div className="full-row"><dt>Error</dt><dd>{job?.lastRun.error ?? "None"}</dd></div>
          </dl>
        </article>

        <article className="operations-card">
          <div className="card-heading-row">
            <div>
              <p className="eyebrow">READINESS</p>
              <h3>Service dependencies</h3>
            </div>
            <span className={`platform-state ${(health?.status ?? "unknown").toLowerCase()}`}>
              {health?.status ?? "UNKNOWN"}
            </span>
          </div>

          <div className="dependency-list">
            {(health?.dependencies ?? []).map((dependency) => (
              <div className="dependency-row" key={dependency.name}>
                <span className={`dependency-dot ${dependency.status.toLowerCase()}`} />
                <div>
                  <strong>{dependency.name}</strong>
                  <small>{dependency.baseUrl}{dependency.readinessPath}</small>
                </div>
                <div className="dependency-result">
                  <strong>{dependency.status}</strong>
                  <small>{dependency.durationMilliseconds} ms · HTTP {dependency.httpStatus ?? "N/A"}</small>
                </div>
                {dependency.error ? <p>{dependency.error}</p> : null}
              </div>
            ))}

            {health?.dependencies.length === 0 || !health ? (
              <div className="page-state-card">No dependency checks are available yet.</div>
            ) : null}
          </div>
        </article>
      </div>

      <aside className="safety-note">
        <strong>Read-only operational view</strong>
        <span>
          This dashboard does not start jobs, modify schedules, or change trading
          state. Operational mutations remain protected service actions.
        </span>
      </aside>
    </section>
  );
}
