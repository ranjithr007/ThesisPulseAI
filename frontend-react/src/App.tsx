import { useOperatorAuth } from "./auth/AuthenticatedApp";
import { ExecutionLifecycleWorkspace } from "./execution/ExecutionLifecycleWorkspace";
import { MarketCommandCenter } from "./market/MarketCommandCenter";
import { OperationsDashboard } from "./operations/OperationsDashboard";
import { PnlWorkspace } from "./pnl/PnlWorkspace";
import { PortfolioWorkspace } from "./portfolio/PortfolioWorkspace";
import { RiskReadinessWorkspace } from "./risk/RiskReadinessWorkspace";
import { useExpandedAppRoute } from "./routing/useExpandedAppRoute";
import { SignalDetail } from "./signals/SignalDetail";
import { SignalScanner } from "./signals/SignalScanner";
import { ThesisReadinessWorkspace } from "./theses/ThesisReadinessWorkspace";

const navigation = [
  "Market",
  "Signals",
  "Theses",
  "Risk",
  "Portfolio",
  "P&L",
  "Execution",
  "Operations",
];

export function App() {
  const { route, navigate } = useExpandedAppRoute();
  const { operator, expiresAtUtc, signOut } = useOperatorAuth();
  const activeNavigation =
    route.page === "market"
      ? "Market"
      : route.page === "theses"
        ? "Theses"
        : route.page === "risk"
          ? "Risk"
          : route.page === "portfolio"
            ? "Portfolio"
            : route.page === "pnl"
              ? "P&L"
              : route.page === "execution"
                ? "Execution"
                : route.page === "operations"
                  ? "Operations"
                  : "Signals";

  const expiresAt = new Date(expiresAtUtc);
  const permissionSummary = operator.permissions.length
    ? operator.permissions.map((permission) => permission.replace("thesispulse.", "")).join(", ")
    : "no permissions";

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">THESISPULSE AI</p>
          <h1>Intelligent signals. Validated theses. Adaptive decisions.</h1>
        </div>
        <div className="topbar-actions">
          <div className="operator-session" aria-label="Authenticated operator">
            <strong>{operator.displayName}</strong>
            <span>{permissionSummary}</span>
            <span>
              Session expires {Number.isNaN(expiresAt.getTime()) ? "soon" : expiresAt.toLocaleTimeString()}
            </span>
          </div>
          <div className="environment-badge" role="status" aria-label="Paper trading environment">
            <strong>PAPER TRADING</strong>
            <span>No real orders will be submitted</span>
          </div>
          <button className="operator-sign-out" type="button" onClick={signOut}>
            Sign out
          </button>
        </div>
      </header>

      <div className="workspace">
        <aside className="sidebar" aria-label="Primary navigation">
          <nav>
            {navigation.map((item) => (
              <button
                className={item === activeNavigation ? "nav-item active" : "nav-item"}
                key={item}
                type="button"
                onClick={() => {
                  if (item === "Market") navigate({ page: "market" });
                  else if (item === "Signals") navigate({ page: "signals" });
                  else if (item === "Theses") navigate({ page: "theses" });
                  else if (item === "Risk") navigate({ page: "risk" });
                  else if (item === "Portfolio") navigate({ page: "portfolio" });
                  else if (item === "P&L") navigate({ page: "pnl" });
                  else if (item === "Execution") navigate({ page: "execution" });
                  else if (item === "Operations") navigate({ page: "operations" });
                }}
              >
                {item}
              </button>
            ))}
          </nav>
          <div className="connection-status">
            <span className="status-dot" aria-hidden="true" />
            Phase 6 · Authenticated PAPER
          </div>
        </aside>

        <main className="content">
          {route.page === "market" ? <MarketCommandCenter /> : null}
          {route.page === "signals" ? <SignalScanner /> : null}
          {route.page === "signal-detail" ? (
            <SignalDetail signalUid={route.signalUid} onBack={() => navigate({ page: "signals" })} />
          ) : null}
          {route.page === "theses" ? <ThesisReadinessWorkspace /> : null}
          {route.page === "risk" ? <RiskReadinessWorkspace /> : null}
          {route.page === "portfolio" ? <PortfolioWorkspace /> : null}
          {route.page === "pnl" ? <PnlWorkspace /> : null}
          {route.page === "execution" ? <ExecutionLifecycleWorkspace /> : null}
          {route.page === "operations" ? <OperationsDashboard /> : null}
        </main>
      </div>
    </div>
  );
}
