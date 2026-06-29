import * as signalR from "@microsoft/signalr";

export type SignalStatus =
  | "CANDIDATE"
  | "VALIDATED"
  | "REJECTED"
  | "EXPIRED"
  | "SUPERSEDED"
  | "CONSUMED";

export type SignalStreamEvent = {
  eventUid: string;
  eventType: "signal.summary.changed.v1";
  contractVersion: "1.0.0";
  signalUid: string;
  signalId: number | null;
  instrumentKey: string;
  direction: "LONG" | "SHORT";
  primaryTimeframe: "1m" | "5m" | "15m" | "1h" | "1d";
  strength: number;
  confidence: number;
  status: SignalStatus;
  statusSequence: number;
  generatedAtUtc: string;
  validUntilUtc: string;
  occurredAtUtc: string;
  correlationId: string;
};

export type StreamConnectionState =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected";

export function createSignalConnection(
  baseUrl: string,
  onSignal: (event: SignalStreamEvent) => void,
  onStateChange: (state: StreamConnectionState) => void,
): signalR.HubConnection {
  const normalizedBaseUrl = baseUrl.replace(/\/$/, "");
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${normalizedBaseUrl}/hubs/signals`)
    .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on("signalUpdated", onSignal);
  connection.onreconnecting(() => onStateChange("reconnecting"));
  connection.onreconnected(() => onStateChange("connected"));
  connection.onclose(() => onStateChange("disconnected"));
  return connection;
}

export async function fetchRecentSignals(
  baseUrl: string,
  limit = 100,
): Promise<SignalStreamEvent[]> {
  const normalizedBaseUrl = baseUrl.replace(/\/$/, "");
  const response = await fetch(
    `${normalizedBaseUrl}/api/v1/stream/signals/recent?limit=${limit}`,
    { headers: { Accept: "application/json" } },
  );

  if (!response.ok) {
    throw new Error(`Signal history request failed with HTTP ${response.status}`);
  }

  const payload = (await response.json()) as {
    events?: SignalStreamEvent[];
  };
  return payload.events ?? [];
}

export function mergeSignalEvent(
  current: SignalStreamEvent[],
  incoming: SignalStreamEvent,
): SignalStreamEvent[] {
  const existing = current.find((item) => item.signalUid === incoming.signalUid);

  if (existing && existing.statusSequence > incoming.statusSequence) {
    return current;
  }

  return [incoming, ...current.filter((item) => item.signalUid !== incoming.signalUid)]
    .sort(
      (left, right) =>
        new Date(right.occurredAtUtc).getTime() -
        new Date(left.occurredAtUtc).getTime(),
    )
    .slice(0, 500);
}
