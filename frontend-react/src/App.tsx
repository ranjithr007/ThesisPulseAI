import { SignalScanner } from "./signals/SignalScanner";

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
            {navigation.map((item) => (
              <button
                className={item === "Signals" ? "nav-item active" : "nav-item"}
                key={item}
                type="button"
              >
                {item}
              </button>
            ))}
          </nav>
          <div className="connection-status">
            <span className="status-dot" aria-hidden="true" />
            Phase 1 · PAPER only
          </div>
        </aside>

        <main className="content">
          <SignalScanner />
        </main>
      </div>
    </div>
  );
}
