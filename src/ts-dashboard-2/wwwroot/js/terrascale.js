// terrascale.js — the only custom script. Everything interactive is Datastar
// (data-* attributes in the slices); this file covers the two things Datastar
// leaves to the platform: click-away close for native <details> menus and
// clipboard copy buttons.
(() => {
  "use strict";

  document.addEventListener("click", (event) => {
    for (const details of document.querySelectorAll("details.ts-menu[open]")) {
      if (!details.contains(event.target)) details.removeAttribute("open");
    }
  });

  // Destructive forms opt into a native confirm() with the exact v1 copy.
  document.addEventListener("submit", (event) => {
    const form = event.target.closest("form[data-confirm]");
    if (form && !window.confirm(form.dataset.confirm ?? "")) {
      event.preventDefault();
    }
  });

  // Native <dialog> open/close: declarative attributes used by the slices
  // (data-open-dialog="#id", data-close-dialog, dialog[data-autoopen]).
  document.addEventListener("click", (event) => {
    const opener = event.target.closest("[data-open-dialog]");
    if (opener) document.getElementById(opener.dataset.openDialog)?.showModal();
    const closer = event.target.closest("[data-close-dialog]");
    if (closer) closer.closest("dialog")?.close();
  });

  // Give every dialog an accessible name from its title (v1 parity: Radix
  // dialogs are labeled by their heading).
  for (const dialog of document.querySelectorAll("dialog.ts-dialog")) {
    const title = dialog.querySelector(".ts-dialog-title");
    if (title && !title.id) title.id = `${dialog.id || "ts"}-dialog-title`;
    if (title && !dialog.hasAttribute("aria-labelledby")) {
      dialog.setAttribute("aria-labelledby", title.id);
    }
  }

  for (const dialog of document.querySelectorAll("dialog[data-autoopen]")) {
    dialog.showModal();
  }

  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    for (const details of document.querySelectorAll("details.ts-menu[open]")) {
      details.removeAttribute("open");
    }
    for (const dialog of document.querySelectorAll("dialog.ts-dialog[open]")) {
      dialog.close();
    }
  });

  document.addEventListener("click", async (event) => {
    const button = event.target.closest("[data-copy]");
    if (!button) return;
    try {
      await navigator.clipboard.writeText(button.dataset.copy ?? "");
      const label = button.querySelector("[data-copy-label]");
      if (label) {
        const original = label.textContent;
        label.textContent = button.dataset.copyDone ?? "Copied";
        setTimeout(() => (label.textContent = original), 1600);
      }
    } catch {
      // Clipboard unavailable (insecure context); ignore.
    }
  });

  document.addEventListener("click", (event) => {
    const toggle = event.target.closest("[data-toggle-type]");
    if (!toggle) return;
    const input = toggle.parentElement?.querySelector("input");
    if (!input) return;
    const show = input.type === "password";
    input.type = show ? "text" : "password";
    toggle.setAttribute("aria-label", show ? "Hide password" : "Show password");
    const svg = toggle.querySelector("svg");
    if (svg) {
      svg.style.opacity = show ? "0.45" : "";
    }
  });
})();
