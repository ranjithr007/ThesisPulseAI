/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_SIGNAL_API_BASE_URL?: string;
  readonly VITE_TRADING_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
