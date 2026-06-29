export interface SignalExpiryRunSnapshot {
  status: "NOT_RUN" | "RUNNING" | "COMPLETED" | "FAILED";
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  selected: number;
  expired: number;
  published: number;
  publicationFailures: number;
  correlationId: string | null;
  error: string | null;
}

export interface SignalExpiryJobStatus {
  enabled: boolean;
  intervalSeconds: number;
  batchSize: number;
  lastRun: SignalExpiryRunSnapshot;
}

export interface DependencyHealthSnapshot {
  name: string;
  status: "HEALTHY" | "UNHEALTHY";
  baseUrl: string;
  readinessPath: string;
  httpStatus: number | null;
  durationMilliseconds: number;
  checkedAtUtc: string;
  error: string | null;
}

export interface PlatformHealthSnapshot {
  status: "HEALTHY" | "DEGRADED" | "UNHEALTHY";
  checkedAtUtc: string;
  dependencies: DependencyHealthSnapshot[];
}
