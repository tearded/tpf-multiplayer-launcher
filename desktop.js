import { renderReleaseNotes, releaseLink } from "./release-notes.js";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getVersion } from "@tauri-apps/api/app";
import { check } from "@tauri-apps/plugin-updater";

const $ = (id) => document.getElementById(id);
let installed = null,
  offer = null,
  status = null,
  busy = false;
let pendingUpdate = null,
  updateChecking = false,
  launcherVersion = "";
let pendingInstallVersion = null;
let historyPage = 1,
  historyHasMore = true;
let toastTimer,
  statusRefresh = null;
const releaseUrl = "https://github.com/silver2127/tpf2-multiplayer/releases";
function toast(text) {
  clearTimeout(toastTimer);
  $("toast").textContent = String(text);
  $("toast").hidden = false;
  toastTimer = setTimeout(() => ($("toast").hidden = true), 6500);
}
function message(text) {
  document.querySelector(".connection-status").textContent = String(text);
}
function setMainLabel(text) {
  $("main-action").querySelector("span").textContent = text;
}
function isSilver() {
  return installed?.channel === "official";
}
function isDowngrade() {
  if (!isSilver() || !offer) return false;
  const current = installed.version.split(".").map(Number),
    next = offer.version.split(".").map(Number);
  for (let i = 0; i < 4; i++) {
    if ((current[i] || 0) !== (next[i] || 0))
      return (current[i] || 0) > (next[i] || 0);
  }
  return false;
}
function render() {
  const same = isSilver() && installed.version === offer?.version;
  const supported = isSilver() && status?.initialized;
  $("installed-version").textContent = installed
    ? `${installed.version}${isSilver() ? "" : " · Other build"}`
    : status
      ? "Not installed"
      : "—";
  $("offered-version").textContent = offer?.version || "—";
  $("game-path").textContent = status?.gameFolder || "Not selected";
  $("game-path").title = status?.gameFolder || "";
  $("release-status").textContent = !status
    ? "Not checked"
    : status.recovery
      ? "Recovery needed"
      : status.running
        ? "Running"
        : installed && !isSilver()
          ? "Setup required"
          : !offer
            ? "Release unavailable"
            : same
              ? "Up to date"
              : isDowngrade()
                ? "Newer build installed"
                : installed
                  ? "Update available"
                  : "Not installed";
  $("release-status").dataset.state = same
    ? "ready"
    : offer
      ? "update"
      : "unknown";
  setMainLabel(
    busy
      ? "Working…"
      : !status
        ? "Retry connection"
        : status.running
          ? "Game is running"
          : status.recovery
            ? "Restore previous installation"
            : !status.gameFolder
              ? "Select game folder"
              : same || (!offer && supported)
                ? "Play"
                : !offer
                  ? "Retry release check"
                  : !supported
                    ? "Install multiplayer"
                    : isDowngrade()
                      ? "Install latest release"
                      : "Update & play",
  );
  $("main-action")
    .querySelector("use")
    .setAttribute(
      "href",
      same || (!offer && supported) ? "#play" : "#download",
    );
  $("main-action").disabled = busy || Boolean(status?.running);
  $("main-action").setAttribute("aria-busy", String(busy));
  $("play-current").hidden = same || !supported;
  $("play-current").disabled =
    busy ||
    !supported ||
    !status?.gameFolder ||
    status?.running ||
    status?.recovery;
  ["check-game", "open-folder", "open-backups"].forEach(
    (id) => ($(id).disabled = busy),
  );
  if ($("load-history")) $("load-history").disabled = busy || !historyHasMore;
  $("choose-folder").disabled = busy || Boolean(status?.running);
  $("launcher-install").disabled =
    busy || !pendingUpdate || Boolean(status?.running) || updateChecking;
  $("launcher-check").disabled = busy || updateChecking;
  if ($("release-track")) {
    $("release-track").disabled = busy || !status;
    $("release-track").value = status?.experimental ? "1" : "0";
  }
  if ($("release-track-label"))
    $("release-track-label").textContent = status?.experimental
      ? "Experimental"
      : "Stable";
  if ($("backup-limit")) {
    $("backup-limit").disabled = busy || !status;
    $("backup-limit").value = String(status?.backupLimit ?? 5);
  }
  $("confirm-install").disabled =
    busy || (!offer && !pendingInstallVersion) || Boolean(status?.running);
}
async function native(action, options = {}) {
  return invoke("native_action", { action, channel: "official", ...options });
}
function acceptStatus(value) {
  status = value;
  installed = value.installed;
  render();
}
function refreshStatus() {
  if (!statusRefresh)
    statusRefresh = native("status")
      .then(acceptStatus)
      .finally(() => {
        statusRefresh = null;
      });
  return statusRefresh;
}
async function refreshGameStatus() {
  if (busy || document.hidden) return;
  const wasRunning = status?.running;
  try {
    await refreshStatus();
    if (status.running !== wasRunning)
      message(
        status.running
          ? "Game is running. Close it before installing updates."
          : "Game closed. Ready.",
      );
  } catch {
    message("Could not check the game status. Try again.");
  }
}
async function fetchOffer() {
  // Never leave a stale installation offer active after a failed refresh.
  offer = null;
  render();
  try {
    offer = await native("fetch");
    $("news-headline").textContent = offer
      ? `Version ${offer.version}`
      : "No release available";
    renderReleaseNotes($("release-notes"), offer?.notes);
  } catch (error) {
    $("news-headline").textContent = "Release unavailable";
    $("release-notes").textContent =
      "Could not load the release from GitHub. Check your connection and try again. You can still play an installed multiplayer version.";
    throw error;
  } finally {
    render();
  }
}
async function task(work) {
  if (busy) return;
  busy = true;
  render();
  try {
    if (statusRefresh) await statusRefresh;
    await work();
  } catch (error) {
    message(String(error));
    toast(error);
  } finally {
    busy = false;
    $("operation-progress").hidden = true;
    render();
  }
}
async function checkGame() {
  await task(async () => {
    message("Checking installation and latest release…");
    await refreshStatus();
    await fetchOffer();
    message(
      status.running
        ? "Game is running. Close it before installing updates."
        : status.recovery
          ? "An interrupted installation needs to be restored."
          : !status.gameFolder
            ? "Select your Transport Fever 2 game folder."
            : installed && !isSilver()
              ? "Install multiplayer to use this launcher. Your current mod files will be backed up."
              : "Ready.",
    );
  });
}
async function play() {
  await native("play");
  message("Game launch requested through Steam.");
}
async function performInstall() {
  if ((!offer && !pendingInstallVersion) || busy) return;
  const version = pendingInstallVersion || offer.version;
  pendingInstallVersion = null;
  $("install-dialog").close();
  await task(async () => {
    message("Preparing installation…");
    acceptStatus(await native("install", { version }));
    message(`Multiplayer ${installed.version} installed.`);
    await play();
  });
}
async function mainAction() {
  pendingInstallVersion = null;
  if (busy || status?.running) return;
  if (!status) {
    await checkGame();
    return;
  }
  if (status.recovery) {
    await task(async () => {
      acceptStatus(await native("recover"));
      message("Previous installation restored.");
    });
    return;
  }
  if (!status.gameFolder) {
    await task(async () => {
      acceptStatus(await native("choose-folder"));
      message(
        status.gameFolder
          ? "Game folder selected."
          : "No game folder selected.",
      );
    });
    return;
  }
  if (isSilver() && (installed.version === offer?.version || !offer)) {
    await task(play);
    return;
  }
  if (!offer) {
    await checkGame();
    return;
  }
  if (!isSilver() || isDowngrade() || offer.experimental) {
    $("install-title").textContent = isDowngrade()
      ? "Install an older release?"
      : offer.experimental
        ? "Install experimental release?"
        : "Install multiplayer?";
    $("install-message").textContent =
      `Multiplayer ${offer.version} will be installed${installed ? ` over version ${installed.version}` : ""}, then the game will start. Everyone in your session needs the same version.`;
    $("install-dialog").showModal();
    return;
  }
  await performInstall();
}
async function checkLauncherUpdate() {
  if (busy || updateChecking) return;
  updateChecking = true;
  render();
  $("launcher-update-copy").textContent = "Checking for launcher updates…";
  try {
    if (pendingUpdate) await pendingUpdate.close();
    pendingUpdate = null;
    pendingUpdate = await check({ timeout: 20000 });
    $("launcher-update-copy").textContent = pendingUpdate
      ? `Version ${pendingUpdate.version} is available. Close the game before installing.`
      : `Launcher ${launcherVersion} is up to date.`;
    if ($("launcher-update-badge")) {
      $("launcher-update-badge").hidden = !pendingUpdate;
      $("launcher-update-badge").textContent = pendingUpdate
        ? `Launcher update · v${pendingUpdate.version}`
        : "Launcher update available";
      $("open-settings").title = pendingUpdate
        ? `Launcher ${pendingUpdate.version} available`
        : "Settings";
    }
    if ($("launcher-info"))
      $("launcher-info").textContent = pendingUpdate
        ? `Launcher ${pendingUpdate.version} available`
        : `Launcher ${launcherVersion} · Up to date`;
  } catch {
    $("launcher-update-copy").textContent =
      "Could not check for updates. Try again later.";
    if ($("launcher-update-badge")) $("launcher-update-badge").hidden = true;
    if ($("launcher-info"))
      $("launcher-info").textContent = "Check launcher updates";
  } finally {
    updateChecking = false;
    render();
  }
}
async function installLauncherUpdate() {
  if (!pendingUpdate || busy || updateChecking || status?.running) return;
  busy = true;
  render();
  let downloaded = 0,
    total = 0;
  try {
    if (statusRefresh) await statusRefresh;
    await pendingUpdate.downloadAndInstall((event) => {
      if (event.event === "Started") total = event.data.contentLength || 0;
      if (event.event === "Progress") downloaded += event.data.chunkLength;
      $("launcher-update-copy").textContent =
        event.event === "Finished"
          ? "Signature verified. Installing launcher update…"
          : `Downloading launcher… ${total ? Math.floor((downloaded / total) * 100) + "%" : ""}`;
    });
  } catch (error) {
    $("launcher-update-copy").textContent = `Launcher update failed: ${error}`;
  } finally {
    busy = false;
    render();
  }
}
export async function initializeDesktop() {
  document.addEventListener("click", (event) => {
    const link = event.target.closest(".markdown-notes a");
    if (!link) return;
    event.preventDefault();
    const url = releaseLink(link.href);
    if (url) task(() => native("release-link", { version: url }));
  });
  document.querySelector(".preview-label").hidden = true;
  ["open-settings", "launcher-info", "launcher-update-badge"].forEach((id) =>
    $(id)?.addEventListener("click", () => $("settings-dialog").showModal()),
  );
  document
    .querySelectorAll(".close-dialog")
    .forEach((button) =>
      button.addEventListener("click", () => button.closest("dialog").close()),
    );
  $("main-action").addEventListener("click", mainAction);
  $("confirm-install").addEventListener("click", performInstall);
  $("play-current").addEventListener("click", () => task(play));
  $("check-game").addEventListener("click", checkGame);
  $("open-folder").addEventListener("click", () =>
    task(async () => {
      if (status?.gameFolder) await native("game-folder");
      else acceptStatus(await native("choose-folder"));
    }),
  );
  $("choose-folder").addEventListener("click", () =>
    task(async () => acceptStatus(await native("choose-folder"))),
  );
  $("open-backups").addEventListener("click", () =>
    task(() => native("backups")),
  );
  $("release-link").href = releaseUrl;
  $("release-link").addEventListener("click", (event) => {
    event.preventDefault();
    task(() => native("release-page"));
  });
  $("backup-limit")?.addEventListener("change", async (event) => {
    const selected = event.target.value;
    await task(async () => {
      try {
        const saved = await native("backup-limit", { version: selected });
        status.backupLimit = saved.backupLimit;
        $("backup-feedback").textContent =
          "Saved. Applies after the next successful mod installation.";
      } catch (error) {
        $("backup-feedback").textContent =
          "Could not save the backup limit. Try again.";
        throw error;
      }
    });
  });
  $("load-history")?.addEventListener("click", () =>
    task(async () => {
      $("history-status").textContent = "Loading releases…";
      try {
        const page = await native("history", { version: String(historyPage) });
        window.launcherHistory.add(page.entries, (entry) =>
          task(async () => {
            await refreshStatus();
            if (status.running)
              throw new Error("Close the game before installing a version.");
            if (status.recovery)
              throw new Error("Restore the interrupted installation first.");
            if (!status.gameFolder)
              throw new Error("Select a game folder first.");
            pendingInstallVersion = entry.version;
            $("install-title").textContent =
              `Install version ${entry.version}?`;
            $("install-message").textContent =
              `This replaces the current mod with multiplayer ${entry.version}, then starts the game. Current mod files will be backed up. Everyone in your session must use this version. Older packages must pass the same download and compatibility checks as current releases.`;
            $("install-dialog").showModal();
          }),
        );
        historyPage++;
        historyHasMore = page.hasMore;
        $("load-history").hidden = !historyHasMore;
        $("load-history").querySelector("span").textContent =
          "Load more releases";
        $("history-status").textContent = page.hasMore
          ? ""
          : "All releases for this selection loaded.";
      } catch (error) {
        $("history-status").textContent =
          "Could not load release history. Try again.";
        throw error;
      }
    }),
  );
  $("release-track")?.addEventListener("change", (event) => {
    const selected = event.target.value;
    task(async () => {
      try {
        const saved = await native("release-track", { version: selected });
        status.experimental = saved.experimental;
        offer = null;
        pendingInstallVersion = null;
        historyPage = 1;
        historyHasMore = true;
        $("history-list").replaceChildren();
        $("history-status").textContent = "";
        $("load-history").hidden = false;
        $("load-history").querySelector("span").textContent =
          "Browse previous releases";
        $("track-feedback").textContent =
          "Preference saved. Checking releases…";
        await fetchOffer();
        $("track-feedback").textContent = offer
          ? "Release selection updated. Nothing was installed."
          : "No releases found for this selection.";
      } catch (error) {
        $("track-feedback").textContent =
          "Could not refresh the release selection. Try checking for updates again.";
        throw error;
      }
    });
  });
  $("launcher-check").addEventListener("click", checkLauncherUpdate);
  $("launcher-install").addEventListener("click", installLauncherUpdate);
  await listen("native-progress", (event) => {
    message(event.payload.text);
    const progress = $("operation-progress");
    progress.hidden = false;
    if (event.payload.percent > 0) {
      progress.max = 100;
      progress.value = event.payload.percent;
    } else progress.removeAttribute("value");
  });
  await listen("operation-busy", () =>
    toast("An operation is in progress. Please wait before closing."),
  );
  launcherVersion = await getVersion();
  document.querySelector(".footer-version").textContent = `v${launcherVersion}`;
  render();
  document.documentElement.dataset.ready = "true";
  const launcherUpdateCheck = checkLauncherUpdate();
  await checkGame();
  window.addEventListener("focus", refreshGameStatus);
  document.addEventListener("visibilitychange", refreshGameStatus);
  window.setInterval(refreshGameStatus, 2500);
  await launcherUpdateCheck;
}
