export type SignalStatus =
  | "CANDIDATE"
  | "VALIDATED"
  | "REJECTED"
  | "EXPIRED"
  | "SUPERSEDED"
  | "CONSUMED";

export type SignalDirection = "LONG" | "SHORT";
export type RiskDecisionStatus =
  | "NOT_EVALUATED"
  | "APPROVED"
  | "REJECTED"
  | "RESTRICTED";
export type TradePlanStatus =
  | "NOT_AVAILABLE"
  | "PENDING"
  | "BUILDING"
  | "READY"
  | "REJECTED"
  | "RETRY_PENDING"
  | "EXPIRED"
  | "CANCELLED"
  | "FAILED";

export type SignalConnectionState =
  | "CONNECTING"
  | "LIVE"
  | "RECONNECTING"
  | "REST_FALLBACK"
  | "OFFLINE";

export interface SignalTradePlanProjection {
  status: TradePlanStatus;
  tradePlanUid: string | null;
  approvedQuantity: number | null;
  entryReferencePrice: number | null;
  stopLossPrice: number | null;
  generatedAtUtc: string | null;
  validUntilUtc: string | null;
  executionAuthorized: false;
}

export interface SignalSummary {
  signalId: number | null;
  signalUid: string;
  messageId: string;
  instrumentKey: string;
  strategyCode: string;
  strategyVersion: string;
  direction: SignalDirection;
  primaryTimeframe: string;
  strength: number;
  confidence: number;
  status: SignalStatus;
  generatedAtUtc: string;
  validUntilUtc: string;
  producer: string;
  creatorEngineCode: string;
  statusSequence?: number;
  lastUpdatedAtUtc?: string;
  riskDecisionStatus?: RiskDecisionStatus;
  riskDecisionUid?: string | null;
  riskEvaluatedAtUtc?: string | null;
  tradePlan?: SignalTradePlanProjection | null;
}

export interface SignalListResponse {
  signals: SignalSummary[];
  count: number;
}

export interface SignalScannerRow {
  signalUid: string;
  messageUid: string;
  instrumentKey: string;
  strategyCode: string;
  strategyVersion: string;
  direction: SignalDirection;
  primaryTimeframe: string;
  strength: number;
  confidence: number;
  status: SignalStatus;
  generatedAtUtc: string;
  validUntilUtc: string;
  producer: string;
  creatorEngineCode: string;
  riskDecisionStatus: RiskDecisionStatus;
  riskDecisionUid: string | null;
  riskEvaluatedAtUtc: string | null;
  tradePlan: SignalTradePlanProjection | null;
}

export interface SignalScannerResponse {
  asOfUtc: string;
  signals: SignalScannerRow[];
  count: number;
}

export interface SignalDecisionProjection {
  signalUid: string;
  riskDecisionStatus: RiskDecisionStatus;
  riskDecisionUid: string | null;
  riskEvaluatedAtUtc: string | null;
  tradePlan: SignalTradePlanProjection;
}

export interface SignalStreamEventV1 {
  eventUid: string;
  eventType: "signal.summary.changed.v1";
  contractVersion: "1.0.0";
  signalUid: string;
  signalId: number | null;
  instrumentKey: string;
  direction: SignalDirection;
  primaryTimeframe: string;
  strength: number;
  confidence: number;
  status: SignalStatus;
  statusSequence: number;
  generatedAtUtc: string;
  validUntilUtc: string;
  occurredAtUtc: string;
  correlationId: string;
}

export interface RecentSignalEventsResponse {
  events: SignalStreamEventV1[];
  count: number;
}

export interface SignalScannerFilters {
  search: string;
  direction: "ALL" | SignalDirection;
  status: "ALL" | SignalStatus;
  timeframe: "ALL" | string;
}
