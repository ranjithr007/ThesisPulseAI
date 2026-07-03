/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_TRADING_API_BASE_URL?: string;
  readonly VITE_SIGNAL_API_BASE_URL?: string;
  readonly VITE_THESIS_API_BASE_URL?: string;
  readonly VITE_RISK_API_BASE_URL?: string;
  readonly VITE_EXECUTION_API_BASE_URL?: string;
  readonly VITE_PORTFOLIO_API_BASE_URL?: string;
  readonly VITE_OPERATIONS_API_BASE_URL?: string;
  readonly VITE_PORTFOLIO_CODE?: string;
  readonly VITE_PORTFOLIO_CURRENCY?: string;
  readonly VITE_EXECUTION_LIFECYCLE_LIMIT?: string;
  readonly VITE_PNL_MAXIMUM_AGE_MINUTES?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
