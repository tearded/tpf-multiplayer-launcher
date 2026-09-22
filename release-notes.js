import { marked } from "marked";
import createDOMPurify from "dompurify";

export function releaseLink(value) {
  try {
    const url = new URL(value, "https://github.com/silver2127/tpf2-multiplayer/");
    return url.protocol === "https:" && url.hostname === "github.com" &&
      !url.username && !url.password && !url.port &&
      url.pathname.startsWith("/silver2127/tpf2-multiplayer/") ? url.href : null;
  } catch { return null; }
}

export function renderReleaseNotes(node, source) {
  const markdown = String(source || "No release notes available.");
  node.classList.add("markdown-notes");
  // Release bodies are untrusted. Sanitize into an inert fragment before attaching.
  // No remote images, styles, scripts, forms, SVGs or arbitrary link destinations.
  if (markdown.length > 100_000) { node.textContent = markdown; return; }
  const purifier = createDOMPurify(node.ownerDocument.defaultView);
  const fragment = purifier.sanitize(marked.parse(markdown, { gfm: true, breaks: false, async: false }), {
    ALLOWED_TAGS: ["p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "del", "ul", "ol", "li", "blockquote", "pre", "code", "table", "thead", "tbody", "tr", "th", "td", "a"],
    ALLOWED_ATTR: ["href", "title", "start"],
    ALLOW_DATA_ATTR: false,
    ALLOW_ARIA_ATTR: false,
    RETURN_DOM_FRAGMENT: true,
  });
  for (const link of fragment.querySelectorAll("a")) {
    const href = releaseLink(link.getAttribute("href") || "");
    if (!link.hasAttribute("href") || !href) {
      link.replaceWith(...link.childNodes);
      continue;
    }
    link.href = href;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
  }
  for (const table of fragment.querySelectorAll("table")) {
    const wrapper = node.ownerDocument.createElement("div");
    wrapper.className = "notes-table";
    wrapper.tabIndex = 0;
    wrapper.setAttribute("role", "region");
    wrapper.setAttribute("aria-label", "Release details");
    table.replaceWith(wrapper);
    wrapper.append(table);
  }
  node.replaceChildren(fragment);
}
