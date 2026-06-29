const navigation = [
  "Market",
  "Signals",
  "Theses",
  "Risk",
  "Portfolio",
  "P&L",
  "Operations",
];

const foundationCards = [
  {
    title: "Signal Scanner",
    description: "Versioned signals, freshness, thesis status, and risk decisions.",
  },
  {
    title: "Instrument Intelligence",
    description: "Market context, engine contributions, evidence, and conflicts.",
  },
  {
    title: "Risk & P&L",
    description: "Paper exposure, drawdown, limits, and immutable snapshots.",
  },
];

export function App() {
  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">THESISPULSE AI</p>
          <h1>Intelligent signals. Validated theses. Adaptive decisions.</h1>
        </div>
        <div className="environment-badge" role="status" aria-label="Paper trading environment">
          <strong>PAPER TRADING</strong>
          <span>No real orders will be submitted</span>
        </div>
      </header>

      <div className="workspace">
        <aside className="sidebar" aria-label="Primary navigation">
          <nav>
            {navigation.map((item) => (
              <button className={item === "Market" ? "nav-item active" : "nav-item"} key={item}>
                {item}
              </button>
            ))}
          </nav>
          <div className="connection-status">
            <span className="status-dot" aria-hidden="true" />
            Platform foundation
          </div>
        </aside>

        <main className="content">
          <section className="hero-panel">
            <p className="eyebrow">PHASE 1</p>
            <h2>Platform foundation is active</h2>
            <p>
              The application shell is ready for authentication, market navigation, signal scanning,
              instrument intelligence, risk controls, and real-time updates.
            </p>
          </section>

          <section className="card-grid" aria-label="Foundation modules">
            {foundationCards.map((card) => (
              <article className="feature-card" key={card.title}>
                <span className="card-state">FOUNDATION</span>
                <h3>{card.title}</h3>
                <p>{card.description}</p>
              </article>
            ))}
          </section>
        </main>
      </div>
    </div>
  );
}
