import { useCallback, useEffect, useState } from "react";

export type AppRoute =
  | { page: "market" }
  | { page: "signals" }
  | { page: "signal-detail"; signalUid: string }
  | { page: "theses" }
  | { page: "operations" };

function parseHash(hash: string): AppRoute {
  const normalized = hash.replace(/^#/, "");
  const segments = normalized.split("/").filter(Boolean);

  if (segments[0] === "market") {
    return { page: "market" };
  }

  if (segments[0] === "theses") {
    return { page: "theses" };
  }

  if (segments[0] === "operations") {
    return { page: "operations" };
  }

  if (segments[0] === "signals" && segments[1]) {
    return {
      page: "signal-detail",
      signalUid: decodeURIComponent(segments[1]),
    };
  }

  if (segments[0] === "signals") {
    return { page: "signals" };
  }

  return { page: "market" };
}

function routeToHash(route: AppRoute): string {
  switch (route.page) {
    case "market":
      return "#/market";
    case "theses":
      return "#/theses";
    case "operations":
      return "#/operations";
    case "signal-detail":
      return `#/signals/${encodeURIComponent(route.signalUid)}`;
    default:
      return "#/signals";
  }
}

export function useAppRoute() {
  const [route, setRoute] = useState<AppRoute>(() =>
    parseHash(window.location.hash),
  );

  useEffect(() => {
    const handleHashChange = () => setRoute(parseHash(window.location.hash));
    window.addEventListener("hashchange", handleHashChange);

    if (!window.location.hash) {
      const defaultHash = routeToHash({ page: "market" });
      window.history.replaceState(null, "", defaultHash);
      setRoute({ page: "market" });
    }

    return () => window.removeEventListener("hashchange", handleHashChange);
  }, []);

  const navigate = useCallback((nextRoute: AppRoute) => {
    const nextHash = routeToHash(nextRoute);

    if (window.location.hash === nextHash) {
      setRoute(nextRoute);
      return;
    }

    window.location.hash = nextHash.slice(1);
  }, []);

  return { route, navigate };
}
