import assert from "node:assert/strict";

import {
  classifyLifecycleDetail,
  classifyLifecycleList,
  humanizeLifecycleCode,
  lifecycleCompletion,
  lifecycleTone,
} from "../src/execution/lifecycleState.ts";

const baseSummary = {
  correlationUid: "10000000-0000-0000-0000-000000000001",
  portfolioCode: "PAPER-DEFAULT",
  instrumentKey: "NSE_EQ|RELIANCE",
  strategyCode: "INTRADAY-MOMENTUM",
  direction: "LONG",
  lifecycleStage: "TRADE_PLAN_READY",
  lifecycleStatus: "IN_PROGRESS",
  isComplete: false,
  isStale: false,
  lastActivityAtUtc: "2026-07-03T10:00:00Z",
  observedAtUtc: "2026-07-03T10:01:00Z",
  signalUid: "20000000-0000-0000-0000-000000000001",
  thesisUid: null,
  riskDecisionUid: null,
  tradePlanUid: "30000000-0000-0000-0000-000000000001",
  executionCommandUid: null,
  orderUid: null,
  fillCount: 0,
  positionUid: null,
  pnlSnapshotUid: null,
  requestedQuantity: 10,
  filledQuantity: null,
  averageFillPrice: null,
  positionQuantity: null,
  netPnlAmount: null,
  warnings: ["THESIS_LINEAGE_NOT_AVAILABLE"],
};

assert.equal(classifyLifecycleList([]), "empty");
assert.equal(classifyLifecycleList([baseSummary]), "ready");
assert.equal(classifyLifecycleList([{ ...baseSummary, isStale: true }]), "stale");

const detail = {
  contractVersion: "1.0.0",
  summary: baseSummary,
  stages: [
    { stage: "SIGNAL", status: "VALIDATED", entityUid: baseSummary.signalUid, occurredAtUtc: baseSummary.lastActivityAtUtc, reasonCode: null, reasonMessage: null },
    { stage: "THESIS", status: "NOT_AVAILABLE", entityUid: null, occurredAtUtc: null, reasonCode: null, reasonMessage: null },
    { stage: "RISK", status: "RISK_APPROVED", entityUid: null, occurredAtUtc: null, reasonCode: null, reasonMessage: null },
  ],
};

assert.equal(classifyLifecycleDetail(detail), "ready");
assert.deepEqual(lifecycleCompletion(detail.stages), { complete: 2, total: 3 });
assert.equal(lifecycleTone("COMPLETE"), "positive");
assert.equal(lifecycleTone("FAILED"), "negative");
assert.equal(lifecycleTone("PARTIAL_LINEAGE"), "warning");
assert.equal(lifecycleTone("IN_PROGRESS"), "neutral");
assert.equal(humanizeLifecycleCode("POSITION_HAS_NO_PNL_VALUATION"), "Position Has No Pnl Valuation");

console.log("Phase 5.5 execution lifecycle frontend acceptance passed.");
