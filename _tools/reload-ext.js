// Reload the Anaglyphohol unpacked extension from the chrome://extensions page.
// Run against the "chrome://extensions" target. Returns the extension id + name it reloaded.
(() => {
  const mgr = document.querySelector("extensions-manager");
  if (!mgr) return "no extensions-manager (are you on chrome://extensions?)";
  const list = mgr.shadowRoot.querySelector("extensions-item-list");
  const items = [...list.shadowRoot.querySelectorAll("extensions-item")];
  const match = items.find(i => (i.shadowRoot.querySelector("#name")?.textContent || "").toLowerCase().includes("anaglyphohol"))
;
  if (!match) return "no extension items found";
  const name = match.shadowRoot.querySelector("#name")?.textContent?.trim();
  const btn = match.shadowRoot.querySelector("#dev-reload-button");
  if (!btn) return `found '${name}' (${match.id}) but no #dev-reload-button (is Developer mode on?)`;
  btn.click();
  return `reloaded '${name}' id=${match.id}`;
})()
