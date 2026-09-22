import test from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { renderReleaseNotes, releaseLink } from "../release-notes.js";

function render(markdown) {
  const dom = new JSDOM('<div id="notes"></div>');
  const node = dom.window.document.getElementById("notes");
  renderReleaseNotes(node, markdown);
  return node;
}

test("GitHub release Markdown renders headings, download tables, emphasis, lists and code", () => {
  const node = render(`## Download

| Platform | Download |
| --- | --- |
| **Windows** | [Installer](https://github.com/silver2127/tpf2-multiplayer/releases/download/v0.6.1.18/TpF2Multiplayer.msi) |

### How to install
- Close the game.
- Run the installer.

Use \`install_proton.sh\` on Linux.

> Everyone needs the same version.`);
  assert.equal(node.querySelector("h2").textContent, "Download");
  assert.equal(node.querySelectorAll("table tr").length, 2);
  assert.equal(node.querySelector("strong").textContent, "Windows");
  assert.equal(node.querySelectorAll("li").length, 2);
  assert.equal(node.querySelector("code").textContent, "install_proton.sh");
  assert.equal(node.querySelector("a").target, "_blank");
  assert.equal(node.querySelector(".notes-table").tabIndex, 0);
  assert.ok(!node.textContent.includes("##"));
  assert.ok(!node.textContent.includes("[Installer]"));
});

test("upstream HTML cannot inject scripts, styles, images, forms or native commands", () => {
  const node = render(`<script>alert(1)</script><style>body{display:none}</style>
<img src="https://example.com/tracker" onerror="alert(1)">
<svg onload="alert(1)"><a href="javascript:alert(1)">bad</a></svg>
<form action="https://example.com"><input name="secret"></form>
<p id="main-action" class="primary-button" onclick="alert(1)" style="position:fixed">Text</p>

[bad](javascript:alert%281%29)
[file](file:///C:/Windows/calc.exe)
[other](https://example.com/)
[spoof](https://github.com@evil.example/silver2127/tpf2-multiplayer/)
[valid](https://github.com/silver2127/tpf2-multiplayer/releases)`);
  assert.equal(node.querySelector("script,style,img,svg,form,input,[onclick],[onerror],[style],[id],[class=primary-button]"), null);
  assert.equal(node.querySelectorAll("a").length, 1);
  assert.equal(node.querySelector("a").textContent, "valid");
});

test("relative upstream links work and unrelated or escaped destinations stay inert", () => {
  assert.equal(releaseLink("releases"), "https://github.com/silver2127/tpf2-multiplayer/releases");
  for (const url of ["https://github.com/other/repo/", "https://github.com/silver2127/tpf2-multiplayer-evil/", "https://github.com:444/silver2127/tpf2-multiplayer/", "../../../other", "data:text/html,bad"])
    assert.equal(releaseLink(url), null);
});

test("refresh replaces old content and missing notes get readable fallback", () => {
  const node = render("## Old");
  renderReleaseNotes(node, "**New**");
  assert.equal(node.querySelector("h2"), null);
  assert.equal(node.querySelector("strong").textContent, "New");
  renderReleaseNotes(node, null);
  assert.equal(node.textContent.trim(), "No release notes available.");
});
