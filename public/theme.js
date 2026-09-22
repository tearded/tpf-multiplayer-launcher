"use strict";
// Runs before the stylesheet to avoid flashing a light surface on dark startup.
(() => {
  const key = "tpf2-launcher.theme";
  let current = "dark";
  try {
    if (
      !document.documentElement.dataset.fixedTheme &&
      localStorage.getItem(key) === "light"
    )
      current = "light";
  } catch {
    /* Storage can be unavailable. */
  }
  document.documentElement.dataset.theme = current;
  document.documentElement.dataset.runtime = window.__TAURI_INTERNALS__
    ? "desktop"
    : "preview";
  window.launcherTheme = {
    get current() {
      return current;
    },
    set(value) {
      if (document.documentElement.dataset.fixedTheme) return;
      if (value !== "light" && value !== "dark") return;
      current = value;
      document.documentElement.dataset.theme = current;
      try {
        localStorage.setItem(key, current);
      } catch {
        /* The toggle still works in memory. */
      }
    },
  };
})();
