import { MarketCommandCenter } from "./market/MarketCommandCenter";
import { OperationsDashboard } from "./operations/OperationsDashboard";
import { RiskReadinessWorkspace } from "./risk/RiskReadinessWorkspace";
import { useAppRoute } from "./routing/useAppRoute";
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
  "Operations",
];

export function App() {
  const { route, navigate } = useAppRoute();
  const activeNavigation =
    route.page === "market"
      ? "Market"
      : route.page === "theses"
        ? "Theses"
        : route.page === "risk"
          ? "Risk"
          : route.page === "operations"
            ? "Operations"
            : "Signals";

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">THESISPULSE AI</p>
          <h1>Intelligent signals. Validated theses. Adaptive decisions.</h1>
        </div>
        <div
          className="environment-badge"
          role="status"
          aria-label="Paper trading environment"
        >
          <strong>PAPER TRADING</strong>
          <span>No real orders will be submitted</span>
        </div>
      </header>

      <div className="workspace">
        <aside className="sidebar" aria-label="Primary navigation">
          <nav>
            {navigation.map((item) => {
              const supported =
                item === "Market" ||
                item === "Signals" ||
                item === "Theses" ||
                item === "Risk" ||
                item === "Operations";

              return (
                <button
                  className={item === activeNavigation ? "nav-item active" : "nav-item"}
                  key={item}
                  type="button"
                  disabled={!supported}
                  aria-disabled={!supported}
                  title={supported ? undefined : "Planned for a later Phase 5 slice"}
                  onClick={() => {
                    if (item === "Market") navigate({ page: "market" });
                    else if (item === "Signals") navigate({ page: "signals" });
                    else if (item === "Theses") navigate({ page: "theses" });
                    else if (item === "Risk") navigate({ page: "risk" });
                    else if (item === "Operations") navigate({ page: "operations" });
                  }}
                >
                  {item}
                </button>
              );
            })}
          </nav>
          <div className="connection-status">
            <span className="status-dot" aria-hidden="true" />
            Phase 5 · PAPER only
          </div>
        </aside>

        <main className="content">
          {route.page === "market" ? <MarketCommandCenter /> : null}
          {route.page === "signals" ? <SignalScanner /> : null}
          {route.page === "signal-detail" ? (
            <SignalDetail
              signalUid={route.signalUid}
              onBack={() => navigate({ page: "signals" })}
            />
          ) : null}
          {route.page === "theses" ? <ThesisReadinessWorkspace /> : null}
          {route.page === "risk" ? <RiskReadinessWorkspace /> : null}
          {route.page === "operations" ? <OperationsDashboard /> : null}
        </main>
      </div>
    </div>
  );
}
