# Phase 0 Closure Decisions

## Purpose

This document closes the remaining architecture-level product and risk decisions without enabling live execution.

Phase 0 approves boundaries, ceilings, evidence requirements and promotion gates. Runtime services, live symbols, broker capabilities and execution activation remain Phase 1 or later work.

## 1. Initial liquid-equity allow-list policy

The first tradable universe will be a separately versioned NSE cash-equity allow-list.

### Entry requirements

An equity may enter the first allow-list only when all of the following are independently verified for the proposed effective period:

- listed and active on NSE cash market;
- current broker instrument mapping is available;
- normal market lot and tick size are known;
- no current trading suspension or surveillance restriction blocks the intended strategy;
- median daily traded value and trade count exceed the approved liquidity floor;
- median quoted spread is within the approved spread ceiling;
- five-minute candles meet completeness and freshness requirements;
- the instrument is supported by the selected market-data and broker adapters;
- sector and correlation metadata are available;
- short-selling eligibility is explicitly recorded rather than inferred.

### Initial size

The first allow-list should contain 10–20 highly liquid large-cap equities. Membership must be approved through a versioned universe record and must not be inferred from an index membership alone.

### Fail-closed rule

Until the reviewed symbol list is committed and activated, no cash equity is trade allowed. The existing index context instruments remain non-tradable benchmarks.

## 2. Initial PAPER exposure ceilings

The following ceilings are approved as conservative PAPER defaults:

| Limit | Starting ceiling |
|---|---:|
| Standard risk per trade | 0.25% of approved capital |
| Maximum risk per trade | 0.50% |
| Maximum total open risk | 1.00% |
| Maximum single-instrument notional | 20% |
| Maximum sector exposure | 30% |
| Maximum correlated-bucket exposure | 35% |
| Maximum margin utilisation | 40% |
| Maximum gross exposure | 100% |
| Maximum net directional exposure | 50% |
| Daily soft loss | 1.00% |
| Daily hard loss | 1.50% |
| Weekly loss | 3.00% |
| Strategy drawdown | 6.00% |
| Portfolio drawdown | 8.00% |
| Consecutive-loss pause | 3 losses |
| Trades per symbol per session | 2 |

These are ceilings, not targets. A child policy, strategy or instrument policy may only reduce them.

## 3. Soft and hard response policy

### Soft breach

A soft breach moves the affected scope to `RESTRICTED`:

- risk multiplier: 0.50;
- maximum one concurrent new position;
- no widening of existing risk;
- approved risk-reducing exits remain available.

### Hard breach

A hard breach moves the affected scope to `CLOSE_ONLY`:

- no new exposure;
- no increase to existing exposure;
- approved risk-reducing and emergency exits remain available;
- reconciliation and operator approval are required before reset.

## 4. Environment promotion gates

### Offline to PAPER

Required evidence:

- point-in-time data with no look-ahead leakage;
- walk-forward evaluation;
- at least 100 completed historical trades or an approved equivalent evidence set;
- positive expectancy after fees and conservative slippage;
- strategy drawdown within the approved ceiling;
- deterministic replay with versioned data, features, model and configuration;
- no direct intelligence-to-execution authority.

### PAPER to SHADOW

Required evidence:

- at least 20 trading sessions;
- zero duplicate execution-intent side effects;
- zero stale-data approvals for new exposure;
- successful order/fill/position reconciliation above 99%;
- no unresolved critical incident;
- kill-switch, close-only and restart recovery drills pass;
- complete correlation and causation lineage.

### SHADOW to RESTRICTED_LIVE

Required evidence:

- at least 30 trading sessions;
- zero unresolved idempotency violations;
- observed slippage and fill behaviour within approved tolerance;
- broker capability matrix re-verified for the exact instruments, products and order types;
- account, margin, settlement and reconciliation checks pass;
- independent operator approval;
- approved restricted-live capital allocation;
- active calendar and symbol allow-list versions.

### RESTRICTED_LIVE to SCALED_LIVE

Required evidence:

- no critical control breach during the restricted-live observation period;
- realised loss, drawdown, slippage and reconciliation remain within policy;
- incident and learning-candidate reviews are complete;
- explicit risk and governance approval;
- a new deployment manifest and active policy assignment.

## 5. Restricted-live capital boundary

The initial restricted-live allocation is the lower of:

- INR 50,000; or
- 5% of separately approved trading capital.

Additional restrictions:

- cash equities only;
- one concurrent position;
- standard risk no greater than 0.25% of the restricted allocation;
- no overnight positions;
- no futures or options;
- no automatic scale-up;
- explicit operator approval before entering restricted live.

This decision does not activate live trading.

## 6. Cross-service portfolio contract decision

### Canonical cross-service contracts

The following must become versioned canonical contracts because Python analytics, learning or risk processes may consume them:

- `position-event`;
- `portfolio-snapshot`;
- `pnl-snapshot`.

They must carry:

- contract version;
- environment;
- exact portfolio, position and instrument identities;
- event or snapshot time in UTC;
- correlation and causation IDs;
- source service and source version;
- fixed-precision quantities and monetary values;
- exact fill, valuation and policy lineage where applicable;
- canonical JSON and deterministic hash when persisted as immutable evidence.

### Internal contract

The mutable portfolio aggregate remains internal to ASP.NET Core. External consumers receive immutable events or point-in-time snapshots, never the mutable database projection.

## 7. Deferred activation decisions

The following remain intentionally pending:

- exact first liquid-equity symbol membership;
- active 2026 NSE holiday and special-session calendar;
- current Upstox instrument mappings and capability activation;
- active PAPER order-transition rules;
- broker account and portfolio provisioning;
- live and shadow capital assignments.

These require current external verification or runtime implementation and must not be inferred from Phase 0 architecture documents.

## Phase 0 closure interpretation

Phase 0 is complete when:

- migrations and seeds pass repeat execution;
- .NET and Python contract validation agree;
- the Phase 0 tracker reflects completed local acceptance;
- the three cross-service portfolio schemas are added or explicitly scheduled as the first contract task of Phase 1;
- all remaining live-sensitive items are documented as fail-closed promotion prerequisites.
