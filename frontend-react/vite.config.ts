import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

function localProxy(target: string, prefix: string) {
  return {
    target,
    changeOrigin: true,
    ws: true,
    rewrite: (path: string) => path.replace(new RegExp(`^${prefix}`), ""),
  };
}

export default defineConfig({
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    proxy: {
      "/local/trading": localProxy("http://localhost:60515", "/local/trading"),
      "/local/signal": localProxy("http://localhost:59479", "/local/signal"),
      "/local/thesis": localProxy("http://localhost:59475", "/local/thesis"),
      "/local/risk": localProxy("http://localhost:59477", "/local/risk"),
      "/local/execution": localProxy("http://localhost:59482", "/local/execution"),
      "/local/portfolio": localProxy("http://localhost:59483", "/local/portfolio"),
      "/local/operations": localProxy("http://localhost:59485", "/local/operations"),
    },
  },
});
