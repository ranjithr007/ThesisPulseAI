# ADR-0004: Cash, Futures, and Options Rollout Scope

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI product, architecture, risk, and execution
- **Supersedes:** None

## Context

Cash equities, futures, and options have materially different execution, margin, liquidity, expiry, pricing, and risk characteristics. Supporting all instruments for live execution from the first release would increase operational risk and make it difficult to isolate failures.

## Decision

ThesisPulse AI will use a staged rollout:

1. cash-equity market data, intelligence, paper, and shadow workflows;
2. cash-equity restricted-live execution;
3. index-futures market data, intelligence, paper, and shadow workflows;
4. index-futures restricted-live execution;
5. options market data, chain intelligence, and paper workflows;
6. options shadow workflows;
7. restricted-live single-leg options only after separate acceptance criteria are met;
8. multi-leg options remain out of scope until a dedicated architecture and risk decision is accepted.

The platform may collect and analyze a broader instrument set than it permits for live execution.

## Cash equities

### Initial scope

- intraday long trades;
- broker-supported intraday short trades subject to exchange and broker rules;
- market, limit, stop, and stop-limit behavior only when confirmed by the broker capability matrix;
- no overnight positions by default;
- no averaging down or martingale behavior;
- no automatic conversion between intraday and delivery products.

### Required controls

- price-band and circuit awareness;
- corporate-action handling;
- auction and suspended-security handling;
- short-sale session cutoff awareness;
- quantity, tick-size, and exchange-rule validation;
- product-type validation through the broker adapter;
- position reconciliation before and after the trading session.

## Futures

### Initial scope

- index futures for NIFTY 50, BANK NIFTY, and FINNIFTY when listed, liquid, and broker-supported;
- nearest liquid contract selected through a versioned contract-selection policy;
- intraday positions only initially;
- single-contract-direction exposure per underlying unless an approved hedging policy states otherwise.

### Required controls

- lot-size and contract-multiplier validation;
- expiry and rollover awareness;
- margin and available-funds checks;
- contract liquidity and spread checks;
- futures-to-underlying basis monitoring;
- expiry-week and physical/settlement risk rules;
- no stale contract mapping;
- maximum notional and correlated-index exposure.

## Options

### Data and intelligence scope

Options are initially approved for data ingestion, research, intelligence, backtesting, and paper trading. Required capabilities include:

- option-chain normalization;
- effective-dated contract identity;
- expiry calendar and expiry-type classification;
- strike selection and moneyness;
- bid, ask, last price, volume, and open interest;
- implied volatility and Greeks with calculation-source versioning;
- spread, depth, and liquidity checks;
- underlying price and timestamp alignment;
- expiry-day and event-risk classification.

### Restricted-live single-leg options prerequisites

Single-leg options may enter restricted live only after all of the following are accepted and demonstrated:

- stable option-chain ingestion and contract mapping;
- point-in-time backtests without look-ahead or survivorship bias;
- paper and shadow validation across multiple volatility regimes;
- strike and expiry selection policy;
- maximum spread and minimum liquidity policy;
- premium-at-risk, Greeks, and gap-risk controls;
- order, partial-fill, rejection, and cancellation behavior;
- expiry-day controls;
- brokerage, fees, taxes, and slippage modeling;
- broker reconciliation for contract-level positions;
- explicit live capital and loss limits lower than the parent portfolio limits.

### Multi-leg options

Multi-leg strategies are excluded from the initial live scope. They require a separate ADR covering:

- strategy-level atomicity expectations;
- leg sequencing;
- partial-fill and orphan-leg risk;
- combined margin estimation;
- net Greeks and scenario risk;
- adjustment and unwind behavior;
- broker basket or multi-order capabilities;
- reconciliation of each leg and the combined strategy.

## Product and order type policy

The domain uses canonical values rather than broker-specific strings. Supported canonical capabilities must be mapped and verified by the Upstox adapter.

Examples of canonical concepts include:

- instrument class: `CASH_EQUITY`, `INDEX_FUTURE`, `STOCK_FUTURE`, `INDEX_OPTION`, `STOCK_OPTION`;
- position intent: `INTRADAY`, `DELIVERY`, `CARRY_FORWARD`;
- order type: `MARKET`, `LIMIT`, `STOP_MARKET`, `STOP_LIMIT`;
- time in force: `DAY`, `IOC` where supported;
- side: `BUY`, `SELL`.

A canonical capability is enabled only when the active adapter capability matrix confirms the mapping for the specific segment and environment. Unknown or unsupported combinations are rejected before order submission.

## Environment matrix

| Instrument scope | Paper | Shadow | Restricted live | Scaled live |
|---|---:|---:|---:|---:|
| Cash equities | Yes | Yes | Initial | After evidence |
| Index futures | Yes | Yes | After cash validation | After evidence |
| Stock futures | Research/paper | Later | Deferred | Deferred |
| Index options, single leg | Yes | After prerequisites | Deferred until approved | Deferred |
| Stock options, single leg | Research/paper | Later | Deferred | Deferred |
| Multi-leg options | Research only | No | No | No |

## Contract-selection policy

Derivatives selection must be deterministic, versioned, and point-in-time correct. It must record:

- eligible expiries;
- selected expiry;
- selected contract or strike;
- liquidity and spread observations;
- underlying price used;
- policy version;
- selection timestamp;
- rejection reasons for alternatives.

A model must not directly provide a raw broker contract token for execution.

## Exposure aggregation

Risk must aggregate economically related exposure across:

- cash equity and its futures;
- underlying index and index futures;
- options delta and other approved Greek measures;
- multiple contracts on the same underlying;
- correlated indices and constituents.

Instrument-specific risk cannot bypass portfolio-level limits.

## Alternatives considered

### Enable all instruments for live execution immediately

Rejected because options and derivatives introduce materially different failure modes and risk calculations.

### Cash equities only for the entire product

Rejected because index futures and options intelligence are important to the planned market-thesis and derivatives-analysis capabilities.

### Let strategies choose any broker contract directly

Rejected because contract identity, expiry, liquidity, and broker mapping must be centrally controlled and auditable.

## Consequences

- The first live milestone is smaller and easier to reconcile.
- Options intelligence can be developed without prematurely enabling options execution.
- Derivative contract selection becomes a versioned platform service.
- Multi-leg execution requires a future dedicated design rather than hidden strategy logic.
