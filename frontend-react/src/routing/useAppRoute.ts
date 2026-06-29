import { useCallback, useEffect, useState } from "react";

export type AppRoute =
  | { page: "signals" }
  | { page: "signal-detail"; signalUid: string }
  | { page: "operations" };

function parseHash(hash: string): AppRoute {
  const normalized = hash.replace(/^#/, "");
  const segments = normalized.split("/").filter(Boolean);

  if (segments[0] === "operations") {
    return { page: "operations" };
  }

  if (segments[0] === "signals" && segments[1]) {
    return {
      page: "signal-detail",
      signalUid: decodeURIComponent(segments[1]),
    };
  }

  return { page: "signals" };
}

function routeToHash(route: AppRoute): string {
  switch (route.page) {
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
      window.history.replaceState(null, "", routeToHash({ page: "signals" }));
    }

    return () => window.removeEventListener("hashchange", handleHashChange);
  }, []);

  const navigate = useCallback((nextRoute: AppRoute) => {
    const nextHash = routeToHash(nextRoute);

    if (window.location.hash === nextHash.slice(1)) {
      setRoute(nextRoute);
      return;
    }

    window.location.hash = nextHash.slice(1);
  }, []);

  return { route, navigate };
}
