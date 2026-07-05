export type OperatorIdentity = {
  subject: string;
  displayName: string;
  permissions: string[];
};

export type OperatorSession = {
  accessToken: string;
  tokenType: "Bearer";
  expiresAtUtc: string;
  operator: OperatorIdentity;
};

export const authExpiredEvent = "thesispulse:auth-expired";

const storageKey = "thesispulse.operator.session.v1";
let authenticatedFetchInstalled = false;

const configuredApiBases = [
  import.meta.env.VITE_TRADING_API_BASE_URL,
  import.meta.env.VITE_SIGNAL_API_BASE_URL,
  import.meta.env.VITE_THESIS_API_BASE_URL,
  import.meta.env.VITE_RISK_API_BASE_URL,
  import.meta.env.VITE_EXECUTION_API_BASE_URL,
  import.meta.env.VITE_PORTFOLIO_API_BASE_URL,
  import.meta.env.VITE_OPERATIONS_API_BASE_URL,
]
  .filter((value): value is string => Boolean(value))
  .map((value) => new URL(value, window.location.origin));

export function readOperatorSession(): OperatorSession | null {
  const raw = window.sessionStorage.getItem(storageKey);
  if (!raw) return null;

  try {
    const session = JSON.parse(raw) as OperatorSession;
    if (
      !session.accessToken ||
      session.tokenType !== "Bearer" ||
      !session.expiresAtUtc ||
      !session.operator?.subject ||
      !session.operator?.displayName ||
      !Array.isArray(session.operator.permissions)
    ) {
      clearOperatorSession();
      return null;
    }

    const expiresAt = Date.parse(session.expiresAtUtc);
    if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) {
      clearOperatorSession();
      return null;
    }

    return session;
  } catch {
    clearOperatorSession();
    return null;
  }
}

export function saveOperatorSession(session: OperatorSession): void {
  window.sessionStorage.setItem(storageKey, JSON.stringify(session));
}

export function clearOperatorSession(): void {
  window.sessionStorage.removeItem(storageKey);
}

export function installAuthenticatedFetch(): void {
  if (authenticatedFetchInstalled) return;
  authenticatedFetchInstalled = true;

  const nativeFetch = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init);
    const url = new URL(request.url, window.location.origin);
    const session = readOperatorSession();
    let authenticatedRequest = request;

    if (session && shouldAttachToken(url) && !request.headers.has("Authorization")) {
      const headers = new Headers(request.headers);
      headers.set("Authorization", `${session.tokenType} ${session.accessToken}`);
      authenticatedRequest = new Request(request, { headers });
    }

    const response = await nativeFetch(authenticatedRequest);
    if (
      response.status === 401 &&
      shouldAttachToken(url) &&
      !url.pathname.endsWith("/api/v1/auth/token")
    ) {
      clearOperatorSession();
      window.dispatchEvent(new Event(authExpiredEvent));
    }

    return response;
  };
}

function shouldAttachToken(url: URL): boolean {
  if (url.origin === window.location.origin && url.pathname.startsWith("/local/")) {
    return true;
  }

  return configuredApiBases.some((base) =>
    url.origin === base.origin &&
    (url.pathname === base.pathname || url.pathname.startsWith(`${trimSlash(base.pathname)}/`)),
  );
}

function trimSlash(value: string): string {
  return value.endsWith("/") ? value.slice(0, -1) : value;
}
