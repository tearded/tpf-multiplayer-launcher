import { defineConfig } from "vite";

export default defineConfig({
  server: {
    // Cargo locks its build outputs while compiling; watching them crashes the dev server.
    watch: {
      ignored: ["**/src-tauri/**", "**/native/out/**", "**/release/**", "**/output/**"],
    },
  },
  build: {
    rollupOptions: {
      input: { main: "index.html", fallback: "fallback.html" },
    },
  },
});
