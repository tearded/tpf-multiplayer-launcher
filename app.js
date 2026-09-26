import { renderReleaseNotes } from "./release-notes.js";
import "./release-notes.css";


if (document.getElementById("theme-toggle")) {
  const toggle = document.getElementById("theme-toggle");
  function renderTheme() {
    const dark = window.launcherTheme.current === "dark";
    toggle.setAttribute("aria-pressed", String(dark));
    const label = toggle.querySelector(".theme-label");
    if (label) label.textContent = dark ? "Night" : "Day";
    toggle.setAttribute("aria-label", label ? "Night mode" : "Dark theme");
    toggle.title = dark ? "Switch to day view" : "Switch to night view";
    toggle.querySelector("use").setAttribute("href", dark ? "#moon" : "#sun");
    document.querySelectorAll("[data-theme-choice]").forEach((button) => {
      button.setAttribute(
        "aria-pressed",
        String(button.dataset.themeChoice === window.launcherTheme.current),
      );
    });
  }
  toggle.addEventListener("click", () => {
    window.launcherTheme.set(
      window.launcherTheme.current === "dark" ? "light" : "dark",
    );
    renderTheme();
  });
  document.querySelectorAll("[data-theme-choice]").forEach((button) => {
    button.addEventListener("click", () => {
      window.launcherTheme.set(button.dataset.themeChoice);
      renderTheme();
    });
  });
  renderTheme();
}
if (window.__TAURI_INTERNALS__) {
  import("./desktop.js")
    .then((module) => module.initializeDesktop())
    .catch((error) => {
      document.documentElement.dataset.ready = "true";
      document.querySelector(".connection-status").textContent =
        "Could not load launcher: " + String(error);
      document.getElementById("main-action").disabled = true;
    });
} else {
  import("./preview.js");
}

// Older releases live in the release history; jump there and load the first page.
document.getElementById("choose-version")?.addEventListener("click", () => {
  const load = document.getElementById("load-history");
  if (!document.getElementById("history-list").children.length && !load.hidden)
    load.click();
  document.getElementById("history-title").focus({ preventScroll: true });
  document
    .querySelector(".release-history")
    .scrollIntoView({ block: "start" });
});

// Current and historical notes share the same sanitized Markdown renderer.
window.launcherHistory = {
  add(entries, install) {
    const list = document.getElementById("history-list");
    for (const entry of entries) {
      if (
        [...list.children].some(
          (item) => item.dataset.version === entry.version,
        )
      )
        continue;
      const item = document.createElement("details");
      item.dataset.version = entry.version;
      const summary = document.createElement("summary");
      const version = document.createElement("span");
      version.textContent = `Version ${entry.version}`;
      summary.append(version);
      const date = new Date(entry.date);
      if (entry.date && !Number.isNaN(date.getTime())) {
        const time = document.createElement("time");
        time.dateTime = date.toISOString();
        time.textContent = date.toLocaleDateString("en-GB", {
          day: "numeric",
          month: "short",
          year: "numeric",
        });
        summary.append(time);
      }
      const body = document.createElement("div");
      body.className = "historical-notes";
      renderReleaseNotes(body, entry.notes);
      item.append(summary, body);
      if (install) {
        const action = document.createElement("button");
        action.type = "button";
        action.className = "quiet-button history-install";
        action.textContent = entry.installable === false ? "Installation unavailable" : `Install ${entry.version}`;
        action.disabled = entry.installable === false;
        if (entry.installable === false) {
          const reason = document.createElement("p");
          reason.textContent = entry.installationIssue;
          item.append(reason);
        }
        action.addEventListener("click", () => install(entry));
        item.append(action);
      }
      list.append(item);
    }
  },
};
