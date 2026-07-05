import { createContext, FormEvent, ReactNode, useContext, useEffect, useMemo, useState } from "react";

import { App } from "../App";
import {
  authExpiredEvent,
  clearOperatorSession,
  OperatorIdentity,
  OperatorSession,
  readOperatorSession,
  saveOperatorSession,
} from "./authSession";

const tradingApiBase = (import.meta.env.VITE_TRADING_API_BASE_URL ?? "").replace(/\/$/, "");

type AuthContextValue = {
  operator: OperatorIdentity;
  expiresAtUtc: string;
  signOut: () => void;
};

type ProblemDetails = {
  title?: string;
  detail?: string;
};

const AuthContext = createContext<AuthContextValue | null>(null);

export function useOperatorAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) {
    throw new Error("useOperatorAuth must be used inside AuthenticatedApp");
  }
  return value;
}

export function AuthenticatedApp() {
  const [session, setSession] = useState<OperatorSession | null>(() => readOperatorSession());

  useEffect(() => {
    const handleExpired = () => setSession(null);
    window.addEventListener(authExpiredEvent, handleExpired);
    return () => window.removeEventListener(authExpiredEvent, handleExpired);
  }, []);

  useEffect(() => {
    if (!session) return;

    const controller = new AbortController();
    void fetch(`${tradingApiBase}/api/v1/auth/session`, {
      headers: { Accept: "application/json" },
      signal: controller.signal,
    })
      .then(async (response) => {
        if (!response.ok) return;
        const operator = (await response.json()) as OperatorIdentity;
        const refreshed = { ...session, operator };
        saveOperatorSession(refreshed);
        setSession(refreshed);
      })
      .catch((error: Error) => {
        if (error.name !== "AbortError") {
          clearOperatorSession();
          setSession(null);
        }
      });

    return () => controller.abort();
  }, [session?.accessToken]);

  const contextValue = useMemo<AuthContextValue | null>(() => {
    if (!session) return null;
    return {
      operator: session.operator,
      expiresAtUtc: session.expiresAtUtc,
      signOut: () => {
        clearOperatorSession();
        setSession(null);
      },
    };
  }, [session]);

  if (!session || !contextValue) {
    return <OperatorSignIn onAuthenticated={setSession} />;
  }

  return (
    <AuthContext.Provider value={contextValue}>
      <App />
    </AuthContext.Provider>
  );
}

type OperatorSignInProps = {
  onAuthenticated: (session: OperatorSession) => void;
};

function OperatorSignIn({ onAuthenticated }: OperatorSignInProps) {
  const [username, setUsername] = useState("operator");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);

    try {
      const response = await fetch(`${tradingApiBase}/api/v1/auth/token`, {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ username, password }),
      });

      if (!response.ok) {
        const problem = (await response.json().catch(() => ({}))) as ProblemDetails;
        setError(problem.detail ?? problem.title ?? "Sign-in failed.");
        return;
      }

      const nextSession = (await response.json()) as OperatorSession;
      saveOperatorSession(nextSession);
      onAuthenticated(nextSession);
      setPassword("");
    } catch {
      setError("Authentication service is unavailable. Keep the platform in fail-closed mode.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="auth-page">
      <section className="auth-card" aria-labelledby="operator-sign-in-heading">
        <p className="eyebrow">THESISPULSE AI</p>
        <h1 id="operator-sign-in-heading">Operator sign-in</h1>
        <p className="auth-intro">
          Access is restricted to authenticated PAPER operators. Authentication does not grant
          broker, LIVE execution, or risk-override authority.
        </p>

        <form onSubmit={submit} className="auth-form">
          <label>
            Username
            <input
              autoComplete="username"
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              disabled={submitting}
              required
            />
          </label>
          <label>
            Password
            <input
              autoComplete="current-password"
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              disabled={submitting}
              required
            />
          </label>
          {error ? <div className="auth-error" role="alert">{error}</div> : null}
          <button type="submit" disabled={submitting}>
            {submitting ? "Signing in…" : "Sign in to PAPER workspace"}
          </button>
        </form>

        <p className="auth-help">
          When using the Windows launcher, use the one-time password printed after startup.
        </p>
      </section>
    </main>
  );
}

export function AuthBoundary({ children }: { children: ReactNode }) {
  return <>{children}</>;
}
