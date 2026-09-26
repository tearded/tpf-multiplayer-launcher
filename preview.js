import { renderReleaseNotes } from "./release-notes.js";
// Browser-only demonstration. Never performs native actions or downloads.
const $ = (id) => document.getElementById(id);
let toastTimer;
function toast(text) {
  clearTimeout(toastTimer);
  $("toast").textContent = text;
  $("toast").hidden = false;
  toastTimer = setTimeout(() => ($("toast").hidden = true), 5000);
}
$("installed-version").textContent = "0.6.1.16";
$("offered-version").textContent = "0.6.1.18";
$("release-status").textContent = "Update available";
$("release-status").dataset.state = "update";
$("main-action").disabled = false;
$("main-action").querySelector("span").textContent = "Update & play";
$("play-current").disabled = false;
$("game-path").textContent = "C:\\Steam\\steamapps\\common\\Transport Fever 2";
$("game-path").title = $("game-path").textContent;
$("news-headline").textContent = "Version 0.6.1.18";
// Preview uses the same Markdown renderer as the desktop and release history.
renderReleaseNotes($("release-notes"), `_Preview · sample release notes_

## Download

| Platform | File |
| --- | --- |
| **Windows** | Multiplayer installer |
| **Linux / Steam Deck** | Proton setup script |

## Changes

- Clearer connection messages.
- Improved save transfers between players.

### Before you play

Everyone in your session needs **the same mod version**.
`);

document.querySelector(".connection-status").textContent =
  "Preview · sample installation, no files will be changed";
document.querySelector(".footer-version").textContent = "v1.1.0";
$("launcher-update-copy").textContent =
  "Preview only. The desktop app checks for signed launcher updates on startup.";
if (new URLSearchParams(window.location.search).has("launcher-update")) {
  const badge = $("launcher-update-badge");
  if (badge) {
    badge.textContent = "Launcher update · v1.1.1";
    badge.hidden = false;
  }
  $("launcher-update-copy").textContent =
    "Preview only · Version 1.1.1 is available. Close the game before installing.";
}
for (const id of ["open-settings", "launcher-info", "launcher-update-badge"])
  $(id)?.addEventListener("click", () => $("settings-dialog").showModal());
document
  .querySelectorAll(".close-dialog")
  .forEach((button) =>
    button.addEventListener("click", () => button.closest("dialog").close()),
  );
$("main-action").addEventListener("click", () => {
  $("install-title").textContent = "Update multiplayer";
  $("install-message").textContent =
    "The desktop app backs up the current mod files, installs multiplayer, and starts the game through Steam. This preview does not change any files.";
  $("confirm-install").textContent = "Simulate installation";
  $("install-dialog").showModal();
});
// Simulated progress with the same button states as the desktop app.
function simulateInstall() {
  const button = $("main-action"),
    label = button.querySelector("span"),
    status = document.querySelector(".connection-status");
  const steps = [];
  for (let percent = 4; percent <= 100; percent += 4)
    steps.push([`Downloading release… ${percent}%`, "known", percent]);
  steps.push(["Backing up current files and installing release…", "unknown", 0]);
  button.disabled = true;
  status.textContent = "Keep the launcher open until this finishes.";
  steps.forEach(([text, state, percent], index) =>
    setTimeout(() => {
      label.textContent = text;
      button.dataset.progress = state;
      button.style.setProperty("--progress", `${percent}%`);
    }, index * 120),
  );
  setTimeout(() => {
    delete button.dataset.progress;
    button.disabled = false;
    label.textContent = "Update & play";
    status.textContent = "Preview · sample installation, no files will be changed";
    toast("Preview: nothing was installed.");
  }, steps.length * 120 + 2500);
}
$("confirm-install").addEventListener("click", () => {
  $("install-dialog").close();
  if ($("confirm-install").textContent === "Simulate installation") simulateInstall();
});
$("play-current").addEventListener("click", () =>
  toast(
    "Preview: the desktop app starts the installed multiplayer version through Steam.",
  ),
);
$("check-game").addEventListener("click", () =>
  toast("Preview: sample release data. Open GitHub for current releases."),
);
$("launcher-check").addEventListener("click", () =>
  toast("Launcher updates are available in the desktop app."),
);
$("choose-folder").addEventListener("click", () =>
  toast("The desktop app lets you select your Transport Fever 2 folder."),
);
$("open-folder").addEventListener("click", () =>
  toast("The desktop app opens your game folder in File Explorer."),
);
$("open-backups").addEventListener("click", () =>
  toast("The desktop app opens your saved mod backups in File Explorer."),
);
document.documentElement.dataset.ready = "true";

$("uninstall-mod")?.addEventListener("click", () => {
  $("uninstall-dialog").showModal();
});
$("confirm-uninstall")?.addEventListener("click", () => {
  $("uninstall-dialog").close();
  toast("Preview: the desktop app removes multiplayer and restores the original game files.");
});

$("backup-limit")?.addEventListener("change", () => {
  $("backup-feedback").textContent = "Preview only. No backups are changed.";
});

$("load-history")?.addEventListener("click", () => {
  window.launcherHistory.add(
    [
      {
        version: "0.6.1.16",
        notes:
          "Sample notes for an earlier release. The desktop app loads the original release notes from GitHub.",
      },
      {
        version: "0.6.1.15",
        notes: "Sample history entry. Expand a version to read its notes.",
      },
      {
        version: "0.6.1.14",
        notes:
          "Preview only. These are placeholder notes, not the actual release changelog.",
      },
    ],
    (entry) => {
      $("install-title").textContent = `Install version ${entry.version}?`;
      $("install-message").textContent =
        `The desktop app backs up the current mod and installs multiplayer ${entry.version}. Everyone in your session must use this version. Preview only: no files are changed.`;
      $("confirm-install").textContent = "Close preview";
      $("install-dialog").showModal();
    },
  );
  $("load-history").hidden = true;
  $("history-status").textContent =
    "Sample history · the desktop app loads real releases.";
});

$("release-track")?.addEventListener("change", (event) => {
  const version = event.target.value === "1" ? "0.6.1.17" : "0.6.1.18";
  $("offered-version").textContent = version;
  $("news-headline").textContent = `Version ${version}`;
  $("release-track-label").textContent =
    event.target.value === "1" ? "Experimental" : "Stable";
  $("track-feedback").textContent =
    "Preview only. The desktop app fetches releases for this selection.";
});
