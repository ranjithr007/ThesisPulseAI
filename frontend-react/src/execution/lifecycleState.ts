import type {
  LifecycleLoadState,
  PaperTradeLifecycleDetail,
  PaperTradeLifecycleSummary,
} from "./types";

export type LifecycleTone = "positive" | "warning" | "negative" | "neutral";

export function classifyLifecycleList(
  items: PaperTradeLifecycleSummary[],
): LifecycleLoadState {
  if (items.length === 0) return "empty";
  return items.every((item) => item.isStale) ? "stale" : "ready";
}

export function classifyLifecycleDetail(
  detail: PaperTradeLifecycleDetail | null,
): LifecycleLoadState {
  if (!detail) return "empty";
  return detail.summary.isStale ? "stale" : "ready";
}

export function lifecycleTone(status: string): LifecycleTone {
  const normalized = status.toUpperCase();
  if (normalized === "COMPLETE" || normalized === "VALUED" || normalized === "FILLED") {
    return "positive";
  }
  if (
    normalized === "REJECTED" ||
    normalized === "FAILED" ||
    normalized === "EXPIRED" ||
    normalized === "UNAVAILABLE"
  ) {
    return "negative";
  }
  if (
    normalized === "PARTIAL_LINEAGE" ||
    normalized === "STALE" ||
    normalized === "RETRY_PENDING" ||
    normalized === "RECONCILIATION_REQUIRED"
  ) {
    return "warning";
  }
  return "neutral";
}

export function humanizeLifecycleCode(value: string | null | undefined): string {
  if (!value) return "Not available";
  return value
    .toLowerCase()
    .split("_")
    .filter(Boolean)
    .map((part) => part[0]?.toUpperCase() + part.slice(1))
    .join(" ");
}

export function lifecycleCompletion(
  stages: PaperTradeLifecycleDetail["stages"],
): { complete: number; total: number } {
  const incompleteStatuses = new Set([
    "NOT_AVAILABLE",
    "NOT_STARTED",
    "NOT_CREATED",
    "NOT_FILLED",
    "NOT_POSTED",
    "NOT_VALUED",
  ]);
  return {
    complete: stages.filter((stage) => !incompleteStatuses.has(stage.status.toUpperCase())).length,
    total: stages.length,
  };
}
