import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { AuthenticatedApp } from "./auth/AuthenticatedApp";
import "./auth/auth.css";
import { installAuthenticatedFetch } from "./auth/authSession";
import "./styles.css";
import "./signals/signal-scanner.css";
import "./pages.css";

const rootElement = document.getElementById("root");

if (!rootElement) {
  throw new Error("Root element was not found");
}

installAuthenticatedFetch();

createRoot(rootElement).render(
  <StrictMode>
    <AuthenticatedApp />
  </StrictMode>,
);
