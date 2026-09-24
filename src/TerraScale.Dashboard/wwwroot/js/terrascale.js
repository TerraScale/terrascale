// terrascale.js — the only custom script. Everything interactive is Datastar
// (data-* attributes in the slices); this file covers platform interactions
// such as native <details> menus, filter forms, and clipboard copy buttons.
(() => {
  "use strict";

  // The mobile sidebar is a Datastar signal, so keep its browser-level
  // accessibility state in sync with the class Datastar applies. The drawer
  // owns focus while open; the page behind it is inert and cannot scroll.
  const navShell = document.querySelector(".ts-shell");
  if (navShell) {
    const navDrawer = navShell.querySelector("#ts-navigation");
    const navInset = navShell.querySelector(".ts-inset");
    const navTrigger = navShell.querySelector(".ts-fab-nav");
    const navClose = navShell.querySelector(".ts-sidebar-close");
    const focusableSelector = [
      "a[href]",
      "area[href]",
      "summary",
      "button:not([disabled])",
      "input:not([disabled]):not([type=hidden])",
      "select:not([disabled])",
      "textarea:not([disabled])",
      "[contenteditable=\"true\"]",
      "[tabindex]:not([tabindex=\"-1\"])",
    ].join(",");
    let navOpen = false;
    let navScrollY = 0;
    let lockedBodyStyle;
    let lockedHtmlOverflow;
    let insetAriaHidden;
    let insetWasInert = false;
    const mobileNavMedia = window.matchMedia("(max-width: 47.99rem)");

    const isVisible = element => {
      const style = window.getComputedStyle(element);
      return style.display !== "none" && style.visibility !== "hidden"
        && element.getClientRects().length > 0;
    };
    const drawerFocusable = () => navDrawer
      ? [...navDrawer.querySelectorAll(focusableSelector)].filter(element => {
        const closedMenu = element.closest("details:not([open])");
        return (!closedMenu || element.tagName === "SUMMARY") && isVisible(element);
      })
      : [];

    const lockPage = () => {
      navScrollY = window.scrollY;
      lockedBodyStyle = {
        position: document.body.style.position,
        top: document.body.style.top,
        width: document.body.style.width,
        overflow: document.body.style.overflow,
        paddingRight: document.body.style.paddingRight,
      };
      lockedHtmlOverflow = document.documentElement.style.overflow;
      const scrollbarWidth = window.innerWidth - document.documentElement.clientWidth;
      document.body.style.position = "fixed";
      document.body.style.top = `-${navScrollY}px`;
      document.body.style.width = "100%";
      document.body.style.overflow = "hidden";
      if (scrollbarWidth > 0) document.body.style.paddingRight = `${scrollbarWidth}px`;
      document.documentElement.style.overflow = "hidden";
    };

    const unlockPage = () => {
      const scrollY = navScrollY;
      if (lockedBodyStyle) {
        document.body.style.position = lockedBodyStyle.position;
        document.body.style.top = lockedBodyStyle.top;
        document.body.style.width = lockedBodyStyle.width;
        document.body.style.overflow = lockedBodyStyle.overflow;
        document.body.style.paddingRight = lockedBodyStyle.paddingRight;
      }
      if (lockedHtmlOverflow !== undefined) {
        document.documentElement.style.overflow = lockedHtmlOverflow;
      }
      lockedBodyStyle = undefined;
      lockedHtmlOverflow = undefined;
      window.scrollTo(0, scrollY);
    };

    const closeNavWithSignal = () => {
      if (navClose instanceof HTMLElement) {
        navClose.click();
      } else if (navTrigger instanceof HTMLElement) {
        navTrigger.click();
      }
    };

    const syncNavState = () => {
      const open = navShell.classList.contains("nav-open");
      if (open === navOpen) return;
      navOpen = open;

      if (open) {
        if (navInset) {
          insetAriaHidden = navInset.getAttribute("aria-hidden");
          insetWasInert = navInset.hasAttribute("inert");
          navInset.inert = true;
          navInset.setAttribute("aria-hidden", "true");
        }
        lockPage();
        const [first] = drawerFocusable();
        (navClose instanceof HTMLElement && isVisible(navClose) ? navClose : first)
          ?.focus({ preventScroll: true });
      } else {
        if (navInset) {
          if (insetWasInert) navInset.inert = true;
          else navInset.inert = false;
          if (insetAriaHidden === null) navInset.removeAttribute("aria-hidden");
          else if (insetAriaHidden !== undefined) navInset.setAttribute("aria-hidden", insetAriaHidden);
        }
        unlockPage();
        (navTrigger instanceof HTMLElement) && navTrigger.focus({ preventScroll: true });
      }
    };

    new MutationObserver(syncNavState).observe(navShell, {
      attributes: true,
      attributeFilter: ["class"],
    });

    const closeNavOutsideMobile = () => {
      if (!mobileNavMedia.matches && navShell.classList.contains("nav-open")) {
        closeNavWithSignal();
      }
    };
    mobileNavMedia.addEventListener("change", closeNavOutsideMobile);

    document.addEventListener("keydown", event => {
      if (!navOpen) return;
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        closeNavWithSignal();
        return;
      }
      if (event.key !== "Tab" || !navDrawer) return;
      const focusable = drawerFocusable();
      if (focusable.length === 0) {
        event.preventDefault();
        navDrawer.focus({ preventScroll: true });
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey ? document.activeElement === first : document.activeElement === last) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus({ preventScroll: true });
      }
    }, true);

    document.addEventListener("focusin", event => {
      if (!navOpen || !navDrawer || navDrawer.contains(event.target)) return;
      const [first] = drawerFocusable();
      first?.focus({ preventScroll: true });
    }, true);

    syncNavState();
    closeNavOutsideMobile();
  }

  // Clear-Site-Data's speculation directives landed in Chromium 138. Older
  // engines can prefetch but cannot safely invalidate private HTML on mutation.
  // UA-CH unavailable, unsupported or insecure contexts use native navigation.
  const prefetchRules = document.getElementById("ts-prefetch-rules");
  const canInvalidatePrefetch = navigator.userAgentData?.brands.some(
    brand => brand.brand === "Chromium" && Number(brand.version) >= 138);
  if (prefetchRules instanceof HTMLTemplateElement) {
    if (window.isSecureContext && canInvalidatePrefetch
      && HTMLScriptElement.supports?.("speculationrules") && !navigator.connection?.saveData) {
      const script = document.createElement("script");
      script.type = "speculationrules";
      script.textContent = prefetchRules.content.querySelector("script").textContent;
      document.head.append(script);
    }
    prefetchRules.remove();
  }

  // In-flight loading feedback: a thin accent bar (html[data-nav-pending]) while
  // the browser fetches the next document. It is not a view transition, so the
  // page itself never cross-fades.
  let navigationTimeout;
  const clearNavigationState = () => {
    clearTimeout(navigationTimeout);
    document.documentElement.removeAttribute("data-nav-pending");
  };
  const indicateNavigation = () => {
    document.documentElement.setAttribute("data-nav-pending", "");
    clearTimeout(navigationTimeout);
    // Native navigation may be canceled by the browser (Escape, download,
    // external handler). Never leave a permanent busy state behind.
    navigationTimeout = setTimeout(clearNavigationState, 8000);
  };
  const navigatesDocument = (url, target) => {
    if (target && target !== "_self") return false;
    const destination = new URL(url, document.baseURI);
    return destination.origin === location.origin
      && ["http:", "https:"].includes(destination.protocol)
      && !(destination.pathname === location.pathname && destination.search === location.search
        && (destination.hash || destination.href === location.href));
  };

  // Observe after bubbling handlers (including confirmation and Datastar)
  // have decided whether the browser should perform a native navigation.
  window.addEventListener("click", (event) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey
      || event.shiftKey || event.altKey) return;
    const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
    if (!link || link.hasAttribute("download") || link.hasAttribute("data-no-progress")) return;
    const target = link.getAttribute("target") ?? document.querySelector("base[target]")?.target;
    if (navigatesDocument(link.href, target)) indicateNavigation();
  });
  window.addEventListener("submit", (event) => {
    const form = event.target;
    if (event.defaultPrevented || !(form instanceof HTMLFormElement)
      || form.hasAttribute("data-no-progress")) return;
    const button = event.submitter;
    const method = button?.getAttribute("formmethod") ?? form.method;
    const target = button?.getAttribute("formtarget") ?? form.getAttribute("target")
      ?? document.querySelector("base[target]")?.target;
    const action = button?.getAttribute("formaction") ?? form.action;
    if (method !== "dialog" && (!target || target === "_self")
      && new URL(action, document.baseURI).origin === location.origin) indicateNavigation();
  });
  window.addEventListener("pageshow", clearNavigationState);
  window.addEventListener("pagereveal", clearNavigationState);
  window.addEventListener("pageswap", clearNavigationState);
  window.addEventListener("pagehide", clearNavigationState);
  window.addEventListener("error", clearNavigationState);
  window.addEventListener("keydown", event => {
    if (event.key === "Escape") clearNavigationState();
  });
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") clearNavigationState();
  });

  document.addEventListener("click", (event) => {
    for (const details of document.querySelectorAll("details.ts-menu[open]")) {
      if (!details.contains(event.target)) details.removeAttribute("open");
    }
  });

  document.addEventListener("click", (event) => {
    const option = event.target.closest("[data-filter-value]");
    if (!option) return;
    const form = option.closest("form");
    const input = option.closest(".ts-filter-field")?.querySelector('input[type="hidden"]');
    if (!form || !input) return;
    input.value = option.dataset.filterValue;
    form.requestSubmit();
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
