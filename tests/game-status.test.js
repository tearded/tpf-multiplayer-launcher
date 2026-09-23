import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const source = readFileSync(new URL("../desktop.js", import.meta.url), "utf8")
  .replace(/^import .*;\r?\n/gm, "")
  .replace("export async function", "async function");
function setup(invoke, launcherCheck = async () => null) {
  const nodes = new Map();
  function node(id) {
    if (!nodes.has(id))
      nodes.set(id, {
        textContent: "",
        disabled: false,
        hidden: false,
        dataset: {},
        setAttribute() {},
        close() {},
        showModal() {
          this.open = true;
        },
        querySelector: (key) => node(`${id}/${key}`),
      });
    return nodes.get(id);
  }
  const document = {
    hidden: false,
    getElementById: node,
    querySelector: node,
    querySelectorAll: () => [],
  };
  const context = vm.createContext({
    document,
    renderReleaseNotes: (node, source) => { node.textContent = source || "No release notes available."; },
    invoke,
    check: launcherCheck,
    getVersion: async () => "1.0.2",
    setTimeout,
    clearTimeout,
  });
  vm.runInContext(
    source +
      `
    installed = {channel:'official',version:'0.6.1.1'};
    offer = {version:'0.6.2'};
    status = {running:true,gameFolder:'fixture',initialized:true,installed};
    render();
    globalThis.api = {checkLauncherUpdate, selectHistoricalVersion(value) { pendingInstallVersion=value; }, refreshGameStatus, task, checkGame, mainAction, performInstall, fetchOffer, render, setState(value, release) {status=value;installed=value.installed;offer=release;render();}};
  `,
    context,
  );
  return { ...context.api, node, document };
}
const state = (running) => ({
  running,
  gameFolder: "fixture",
  initialized: true,
  installed: { channel: "official", version: "0.6.1.1" },
});

test("unsupported latest release stays visible and cannot trigger installation", async () => {
  const calls = [];
  const release = {version: "autumn-release", notes: "Latest notes", installable: false, installationIssue: "No compatible Windows package."};
  const ui = setup(async (_command, args) => { calls.push(args.action); return release; });
  ui.setState(state(false), null);
  await ui.fetchOffer();
  assert.equal(ui.node("offered-version").textContent, "autumn-release");
  assert.equal(ui.node("release-notes").textContent, "Latest notes");
  assert.equal(ui.node("release-status").textContent, "Installation unavailable");
  assert.equal(ui.node("play-current").hidden, false);
  await ui.mainAction();
  assert.deepEqual(calls, ["fetch"]);
  assert.match(ui.document.querySelector(".connection-status").textContent, /No compatible Windows package/);
});

test("two-part latest version offers an update from a four-part version", () => {
  const ui = setup(async () => null);
  ui.setState(state(false), {version: "0.7", installable: true});
  assert.equal(ui.node("offered-version").textContent, "0.7");
  assert.equal(ui.node("main-action").querySelector("span").textContent, "Update & play");
});

test("closing the game unlocks updates without restarting the launcher", async () => {
  const ui = setup(async () => state(false));
  assert.equal(ui.node("main-action").disabled, true);
  await ui.refreshGameStatus();
  assert.equal(ui.node("main-action").disabled, false);
  assert.equal(ui.node("main-action/span").textContent, "Update & play");
  assert.match(ui.node(".connection-status").textContent, /Game closed/);
});

test("game start is observed and blocks updates with an explicit label", async () => {
  let running = false;
  const ui = setup(async () => state(running));
  await ui.refreshGameStatus();
  running = true;
  await ui.refreshGameStatus();
  assert.equal(ui.node("main-action").disabled, true);
  assert.match(ui.node("main-action/span").textContent, /Game is running/);
});

test("focus/timer coalesce and user actions wait rather than colliding with the native helper", async () => {
  let finish,
    reads = 0,
    actionStarted = false;
  const ui = setup(() => {
    reads++;
    return new Promise((resolve) => {
      finish = resolve;
    });
  });
  const first = ui.refreshGameStatus(),
    second = ui.refreshGameStatus();
  const action = ui.task(async () => {
    actionStarted = true;
  });
  await ui.refreshGameStatus(); // Busy user action suppresses another poll.
  assert.equal(reads, 1);
  assert.equal(actionStarted, false);
  finish(state(false));
  await Promise.all([first, second, action]);
  assert.equal(actionStarted, true);
  assert.equal(ui.node("main-action").disabled, false);
});

test("a failed status check can recover on the next poll; hidden windows do not poll", async () => {
  let reads = 0;
  const ui = setup(async () => {
    if (++reads === 1) throw Error("helper failure");
    return state(false);
  });
  ui.document.hidden = true;
  await ui.refreshGameStatus();
  assert.equal(reads, 0);
  ui.document.hidden = false;
  await ui.refreshGameStatus();
  assert.equal(ui.node("main-action").disabled, true);
  await ui.refreshGameStatus();
  assert.equal(ui.node("main-action").disabled, false);
});

test("refresh checks game state before fetching the Silver release", async () => {
  const calls = [];
  const ui = setup(async (_, { action }) => {
    calls.push(action);
    return action === "status" ? state(false) : { version: "0.6.2", notes: "" };
  });
  await ui.checkGame();
  assert.deepEqual(calls, ["status", "fetch"]);
  assert.equal(ui.node("main-action").disabled, false);
});

test("legacy builds cannot be launched, including when GitHub is unavailable", () => {
  const ui = setup(async () => state(false));
  ui.setState(
    { ...state(false), installed: { channel: "community", version: "0.6.2" } },
    null,
  );
  assert.equal(ui.node("play-current").disabled, true);
  assert.equal(ui.node("play-current").hidden, true);
  assert.equal(ui.node("main-action/span").textContent, "Retry release check");
});

test("an installed multiplayer version can be played offline", async () => {
  const calls = [];
  const ui = setup(async (_, args) => {
    calls.push(args);
  });
  ui.setState(state(false), null);
  assert.equal(ui.node("main-action/span").textContent, "Play");
  await ui.mainAction();
  assert.equal(calls[0].action, "play");
  assert.equal(calls[0].channel, "official");
});

test("downgrades and migration require an explicit install confirmation", async () => {
  const ui = setup(async () => {
    throw Error("Must not install before confirmation");
  });
  ui.setState(state(false), { version: "0.5.0" });
  await ui.mainAction();
  assert.equal(ui.node("install-dialog").open, true);
  assert.match(ui.node("install-title").textContent, /older release/);
  ui.setState(
    { ...state(false), installed: { channel: "community", version: "0.6.2" } },
    { version: "0.6.2" },
  );
  await ui.mainAction();
  assert.equal(ui.node("install-title").textContent, "Install multiplayer?");
});

test("a failed release refresh clears the stale installation offer", async () => {
  const ui = setup(async () => {
    throw Error("offline");
  });
  ui.setState(state(false), { version: "0.6.2" });
  await assert.rejects(ui.fetchOffer(), /offline/);
  assert.equal(ui.node("offered-version").textContent, "\u2014");
  assert.equal(ui.node("main-action/span").textContent, "Play");
});

test("a successful installation starts Silver and only passes the official source", async () => {
  const calls = [];
  const ui = setup(async (_, args) => {
    calls.push(args);
    return args.action === "install"
      ? {
          ...state(false),
          installed: { channel: "official", version: "0.6.2" },
        }
      : {};
  });
  ui.setState({ ...state(false), installed: null }, { version: "0.6.2" });
  await ui.performInstall();
  assert.deepEqual(
    calls.map((c) => c.action),
    ["install", "play"],
  );
  assert.ok(calls.every((c) => c.channel === "official"));
  assert.equal(calls[0].version, "0.6.2");
});

test("recovery takes priority and does not start a restored legacy build", async () => {
  const calls = [];
  const ui = setup(async (_, args) => {
    calls.push(args.action);
    return state(false);
  });
  ui.setState({ ...state(false), recovery: true }, { version: "0.6.2" });
  await ui.mainAction();
  assert.deepEqual(calls, ["recover"]);
});

test("historical installation passes the selected version instead of the latest offer", async () => {
  const calls = [];
  const ui = setup(async (command, args) => {
    calls.push(args);
    if (args.action === "install")
      return {
        ...state(false),
        installed: { channel: "official", version: "0.6.1.14" },
      };
    return { started: true };
  });
  ui.setState(state(false), { version: "0.6.1.18" });
  ui.selectHistoricalVersion("0.6.1.14");
  await ui.performInstall();
  assert.equal(calls[0].action, "install");
  assert.equal(calls[0].version, "0.6.1.14");
  assert.equal(calls[1].action, "play");
  assert.equal(ui.node("offered-version").textContent, "0.6.1.18");
});

test("experimental upgrade requires confirmation", async () => {
  const ui = setup(async () => {
    throw new Error("No installation before confirmation");
  });
  ui.setState(state(false), { version: "0.6.1.18", experimental: true });
  await ui.mainAction();
  assert.equal(ui.node("install-dialog").open, true);
  assert.equal(
    ui.node("install-title").textContent,
    "Install experimental release?",
  );
});
test("launcher update check remains callable after mod feed failure", async () => {
  const ui = setup(async (command, args) => {
    if (args.action === "status") return state(false);
    throw new Error("Offline mod feed");
  });
  await ui.checkGame();
  await ui.checkLauncherUpdate();
  assert.match(ui.node("launcher-update-copy").textContent, /up to date/);
});

test("available launcher update shows a badge without installing", async () => {
  let installed = false;
  const ui = setup(
    async () => state(false),
    async () => ({
      version: "1.0.3",
      downloadAndInstall() {
        installed = true;
      },
    }),
  );
  await ui.checkLauncherUpdate();
  assert.equal(ui.node("launcher-update-badge").hidden, false);
  assert.match(ui.node("launcher-update-badge").textContent, /Launcher update · v1\.0\.3/);
  assert.match(ui.node("launcher-update-copy").textContent, /1.0.3/);
  assert.equal(installed, false);
});

test("launcher update notice clears when a recheck finds no update or fails", async () => {
  for (const fails of [false, true]) {
    let checks = 0;
    const ui = setup(async () => state(false), async () => {
      if (++checks === 1) return { version: "1.0.3", async close() {} };
      if (fails) throw new Error("Offline");
      return null;
    });
    await ui.checkLauncherUpdate();
    assert.equal(ui.node("launcher-update-badge").hidden, false);
    await ui.checkLauncherUpdate();
    assert.equal(ui.node("launcher-update-badge").hidden, true);
  }
});
