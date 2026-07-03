import { useCallback, useEffect, useState } from "react";

export type AppRoute =
  | { page: "market" }
  | { page: "signals" }
  | { page: "signal-detail"; signalUid: string }
  | { page: "theses" }
  | { page: "risk" }
  | { page: "portfolio" }
  | { page: "pnl" }
  | { page: "operations" };

function parseHash(hash: string): AppRoute {
  const segments = hash.replace(/^#/, "").split("/").filter(Boolean);
  if (segments[0] === "market") return { page: "market" };
  if (segments[0] === "theses") return { page: "theses" };
  if (segments[0] === "risk") return { page: "risk" };
  if (segments[0] === "portfolio") return { page: "portfolio" };
  if (segments[0] === "pnl") return { page: "pnl" };
  if (segments[0] === "operations") return { page: "operations" };
  if (segments[0] === "signals" && segments[1]) {
    return { page: "signal-detail", signalUid: decodeURIComponent(segments[1]) };
  }
  if (segments[0] === "signals") return { page: "signals" };
  return { page: "market" };
}

function routeToHash(route: AppRoute) {
  return route.page === "signal-detail"
    ? `#/signals/${encodeURIComponent(route.signalUid)}`
    : `#/${route.page}`;
}

export function useExpandedAppRoute() {
  const [route, setRoute] = useState<AppRoute>(() => parseHash(window.location.hash));
  useEffect(() => {
    const handleHashChange = () => setRoute(parseHash(window.location.hash));
    window.addEventListener("hashchange", handleHashChange);
    if (!window.location.hash) {
      window.history.replaceState(null, "", "#/market");
      setRoute({ page: "market" });
    }
    return () => window.removeEventListener("hashchange", handleHashChange);
  }, []);
  const navigate = useCallback((nextRoute: AppRoute) => {
    const nextHash = routeToHash(nextRoute);
    if (window.location.hash === nextHash) setRoute(nextRoute);
    else window.location.hash = nextHash.slice(1);
  }, []);
  return { route, navigate };
}
