export type PaperTradeLifecycleStage = {
  stage: string;
  status: string;
  entityUid: string | null;
  occurredAtUtc: string | null;
  reasonCode: string | null;
  reasonMessage: string | null;
};

export type PaperTradeLifecycleSummary = {
  correlationUid: string;
  portfolioCode: string | null;
  instrumentKey: string;
  strategyCode: string;
  direction: string;
  lifecycleStage: string;
  lifecycleStatus: string;
  isComplete: boolean;
  isStale: boolean;
  lastActivityAtUtc: string;
  observedAtUtc: string;
  signalUid: string;
  thesisUid: string | null;
  riskDecisionUid: string | null;
  tradePlanUid: string;
  executionCommandUid: string | null;
  orderUid: string | null;
  fillCount: number;
  positionUid: string | null;
  pnlSnapshotUid: string | null;
  requestedQuantity: number | null;
  filledQuantity: number | null;
  averageFillPrice: number | null;
  positionQuantity: number | null;
  netPnlAmount: number | null;
  warnings: string[];
};

export type PaperTradeLifecycleDetail = {
  contractVersion: string;
  summary: PaperTradeLifecycleSummary;
  stages: PaperTradeLifecycleStage[];
};

export type PaperTradeLifecycleList = {
  contractVersion: string;
  environment: string;
  portfolioCode: string | null;
  limit: number;
  observedAtUtc: string;
  items: PaperTradeLifecycleSummary[];
};

export type PaperTradeLifecycleLineageEvidence = {
  stage: string;
  entityUid: string;
  correlationUid: string;
  causationUid: string | null;
  occurredAtUtc: string;
  sourceTable: string;
};

export type PaperTradeLifecycleOperationalState = {
  scopeType: string;
  scopeId: string;
  effectiveOperatingMode: string;
  sourceControlUid: string | null;
  allowsNewExposure: boolean;
  allowsRiskReducingExits: boolean;
  requiresOperatorReview: boolean;
  evaluatedAtUtc: string;
  evaluationVersion: string;
};

export type PaperTradeLifecycleAcceptanceCheck = {
  code: string;
  outcome: "PASS" | "FAIL" | "INCOMPLETE" | string;
  message: string;
  evidenceReferences: string[];
};

export type PaperTradeLifecycleAcceptanceReport = {
  contractVersion: string;
  environment: string;
  correlationUid: string;
  portfolioCode: string | null;
  outcome: "PASS" | "FAIL" | "INCOMPLETE" | string;
  evaluatedAtUtc: string;
  summary: PaperTradeLifecycleSummary;
  lineage: PaperTradeLifecycleLineageEvidence[];
  operationalStates: PaperTradeLifecycleOperationalState[];
  checks: PaperTradeLifecycleAcceptanceCheck[];
};

export type LifecycleLoadState =
  | "loading"
  | "ready"
  | "empty"
  | "stale"
  | "unavailable";
