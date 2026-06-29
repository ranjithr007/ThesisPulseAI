import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import type {
  RecentSignalEventsResponse,
  SignalConnectionState,
  SignalListResponse,
  SignalStreamEventV1,
  SignalSummary,
} from "./types";

const signalApiBaseUrl =
  import.meta.env.VITE_SIGNAL_API_BASE_URL?.replace(/\/$/, "") ??
  "http://localhost:5102";
const tradingApiBaseUrl =
  import.meta.env.VITE_TRADING_API_BASE_URL?.replace(/\/$/, "") ??
  "http://localhost:5100";

function normalizeSignal(signal: SignalSummary): SignalSummary {
  return {
    ...signal,
    strength: Number(signal.strength),
    confidence: Number(signal.confidence),
    statusSequence: signal.statusSequence ?? 0,
    lastUpdatedAtUtc: signal.lastUpdatedAtUtc ?? signal.generatedAtUtc,
  };
}

function fromStreamEvent(event: SignalStreamEventV1): SignalSummary {
  return {
    signalId: event.signalId,
    signalUid: event.signalUid,
    messageId: "",
    instrumentKey: event.instrumentKey,
    strategyCode: "REALTIME",
    strategyVersion: event.contractVersion,
    direction: event.direction,
    primaryTimeframe: event.primaryTimeframe,
    strength: Number(event.strength),
    confidence: Number(event.confidence),
    status: event.status,
    generatedAtUtc: event.generatedAtUtc,
    validUntilUtc: event.validUntilUtc,
    producer: "ThesisPulse.Trading.Api",
    creatorEngineCode: "STREAM",
    statusSequence: event.statusSequence,
    lastUpdatedAtUtc: event.occurredAtUtc,
  };
}

function mergeSignal(
  current: SignalSummary | undefined,
  incoming: SignalSummary,
): SignalSummary {
  if (!current) {
    return incoming;
  }

  const currentSequence = current.statusSequence ?? 0;
  const incomingSequence = incoming.statusSequence ?? 0;

  if (incomingSequence < currentSequence) {
    return current;
  }

  return {
    ...current,
    ...incoming,
    messageId: incoming.messageId || current.messageId,
    strategyCode:
      incoming.strategyCode === "REALTIME"
        ? current.strategyCode
        : incoming.strategyCode,
    strategyVersion:
      incoming.strategyCode === "REALTIME"
        ? current.strategyVersion
        : incoming.strategyVersion,
    producer:
      incoming.producer === "ThesisPulse.Trading.Api"
        ? current.producer
        : incoming.producer,
    creatorEngineCode:
      incoming.creatorEngineCode === "STREAM"
        ? current.creatorEngineCode
        : incoming.creatorEngineCode,
  };
}

async function fetchJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    headers: { Accept: "application/json" },
    signal,
  });

  if (!response.ok) {
    throw new Error(`Request failed with HTTP ${response.status}`);
  }

  return (await response.json()) as T;
}

export interface SignalScannerState {
  signals: SignalSummary[];
  connectionState: SignalConnectionState;
  lastEventAtUtc: string | null;
  lastSnapshotAtUtc: string | null;
  error: string | null;
  refresh: () => Promise<void>;
}

export function useSignalScanner(): SignalScannerState {
  const [signalsById, setSignalsById] = useState<Map<string, SignalSummary>>(
    () => new Map(),
  );
  const [connectionState, setConnectionState] =
    useState<SignalConnectionState>("CONNECTING");
  const [lastEventAtUtc, setLastEventAtUtc] = useState<string | null>(null);
  const [lastSnapshotAtUtc, setLastSnapshotAtUtc] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const connectionRef = useRef<HubConnection | null>(null);

  const applySignals = useCallback((incoming: SignalSummary[]) => {
    setSignalsById((current) => {
      const next = new Map(current);

      for (const signal of incoming) {
        const normalized = normalizeSignal(signal);
        next.set(
          normalized.signalUid,
          mergeSignal(next.get(normalized.signalUid), normalized),
        );
      }

      return next;
    });
  }, []);

  const applyEvent = useCallback((event: SignalStreamEventV1) => {
    const incoming = fromStreamEvent(event);
    setSignalsById((current) => {
      const next = new Map(current);
      next.set(
        incoming.signalUid,
        mergeSignal(next.get(incoming.signalUid), incoming),
      );
      return next;
    });
    setLastEventAtUtc(event.occurredAtUtc);
    setError(null);
  }, []);

  const refresh = useCallback(async () => {
    try {
      const snapshot = await fetchJson<SignalListResponse>(
        `${signalApiBaseUrl}/api/v1/signals?limit=200`,
      );
      applySignals(snapshot.signals);
      setLastSnapshotAtUtc(new Date().toISOString());
      setError(null);
    } catch (requestError) {
      setError(
        requestError instanceof Error
          ? requestError.message
          : "Signal snapshot could not be loaded.",
      );
      setConnectionState((current) =>
        current === "LIVE" ? current : "OFFLINE",
      );
    }
  }, [applySignals]);

  useEffect(() => {
    const abortController = new AbortController();
    let disposed = false;

    const loadInitialData = async () => {
      try {
        const [snapshot, recentEvents] = await Promise.all([
          fetchJson<SignalListResponse>(
            `${signalApiBaseUrl}/api/v1/signals?limit=200`,
            abortController.signal,
          ),
          fetchJson<RecentSignalEventsResponse>(
            `${tradingApiBaseUrl}/api/v1/stream/signals/recent?limit=100`,
            abortController.signal,
          ).catch(() => ({ events: [], count: 0 })),
        ]);

        if (disposed) {
          return;
        }

        applySignals(snapshot.signals);
        for (const event of recentEvents.events) {
          applyEvent(event);
        }
        setLastSnapshotAtUtc(new Date().toISOString());
      } catch (requestError) {
        if (!disposed && !abortController.signal.aborted) {
          setError(
            requestError instanceof Error
              ? requestError.message
              : "Initial signal data could not be loaded.",
          );
        }
      }
    };

    const connection = new HubConnectionBuilder()
      .withUrl(`${tradingApiBaseUrl}/hubs/signals`, {
        withCredentials: true,
      })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    connectionRef.current = connection;
    connection.on("signalUpdated", applyEvent);
    connection.onreconnecting(() => {
      if (!disposed) {
        setConnectionState("RECONNECTING");
      }
    });
    connection.onreconnected(() => {
      if (!disposed) {
        setConnectionState("LIVE");
        void refresh();
      }
    });
    connection.onclose(() => {
      if (!disposed) {
        setConnectionState("REST_FALLBACK");
      }
    });

    const start = async () => {
      await loadInitialData();

      try {
        await connection.start();
        if (!disposed) {
          setConnectionState("LIVE");
          setError(null);
        }
      } catch (connectionError) {
        if (!disposed) {
          setConnectionState("REST_FALLBACK");
          setError(
            connectionError instanceof Error
              ? connectionError.message
              : "SignalR connection could not be established.",
          );
        }
      }
    };

    void start();
    const fallbackTimer = window.setInterval(() => {
      if (connection.state !== HubConnectionState.Connected) {
        setConnectionState("REST_FALLBACK");
        void refresh();
      }
    }, 15_000);

    return () => {
      disposed = true;
      abortController.abort();
      window.clearInterval(fallbackTimer);
      connection.off("signalUpdated", applyEvent);
      connectionRef.current = null;
      void connection.stop();
    };
  }, [applyEvent, applySignals, refresh]);

  const signals = useMemo(
    () =>
      Array.from(signalsById.values()).sort(
        (left, right) =>
          Date.parse(right.generatedAtUtc) - Date.parse(left.generatedAtUtc),
      ),
    [signalsById],
  );

  return {
    signals,
    connectionState,
    lastEventAtUtc,
    lastSnapshotAtUtc,
    error,
    refresh,
  };
}
