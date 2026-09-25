import { rocket } from "./datastar-rocket.js";
import "./dashboard.js";

const focusableSelector = [
  "a[href]",
  "area[href]",
  "summary",
  "button:not([disabled])",
  'input:not([disabled]):not([type="hidden"])',
  "select:not([disabled])",
  "textarea:not([disabled])",
  '[contenteditable="true"]',
  '[tabindex]:not([tabindex="-1"])',
].join(",");

const elementTarget = target => target instanceof Element ? target : target?.parentElement;

function listen(target, type, handler, cleanup, options) {
  target.addEventListener(type, handler, options);
  cleanup(() => target.removeEventListener(type, handler, options));
}

function define(tag, definition) {
  if (!customElements.get(tag)) rocket(tag, definition);
}

define("ts-menu", {
  mode: "light",
  setup({ host, cleanup }) {
    const closeOpenDetails = () => {
      for (const details of host.querySelectorAll("details.ts-menu[open]")) {
        details.open = false;
      }
    };

    const onDocumentClick = event => {
      if (!host.contains(elementTarget(event.target))) closeOpenDetails();
    };
    const onDocumentKeydown = event => {
      if (event.key !== "Escape") return;
      const target = elementTarget(event.target);
      const details = [...host.querySelectorAll("details.ts-menu[open]")]
        .find(menu => menu.contains(target));
      if (!details) return;
      event.preventDefault();
      event.stopPropagation();
      details.open = false;
      details.querySelector("summary")?.focus();
    };

    listen(document, "click", onDocumentClick, cleanup, true);
    listen(document, "keydown", onDocumentKeydown, cleanup, true);
  },
});

define("ts-tabs", {
  mode: "light",
  setup({ host, cleanup }) {
    const onKeydown = event => {
      const current = elementTarget(event.target)?.closest("a[href]");
      if (!current || !host.contains(current)) return;

      const navigation = current.closest("nav.ts-tabs");
      const links = [...(navigation ?? host).querySelectorAll("a[href]")]
        .filter(link => !link.hasAttribute("aria-disabled") && !link.closest("[hidden]"));
      const currentIndex = links.indexOf(current);
      if (currentIndex < 0 || links.length < 2) return;

      let nextIndex = currentIndex;
      if (event.key === "Home") {
        nextIndex = 0;
      } else if (event.key === "End") {
        nextIndex = links.length - 1;
      } else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
        const rtl = navigation && getComputedStyle(navigation).direction === "rtl";
        const forward = event.key === "ArrowRight" ? 1 : -1;
        const direction = rtl ? -forward : forward;
        nextIndex = (currentIndex + direction + links.length) % links.length;
      } else {
        return;
      }

      event.preventDefault();
      links[nextIndex].focus();
    };

    listen(host, "keydown", onKeydown, cleanup);
  },
});

define("ts-password-control", {
  mode: "light",
  setup({ host, cleanup }) {
    let resetTimeout;
    const control = () => {
      const button = host.querySelector("[data-password-toggle]");
      const inputID = button?.getAttribute("aria-controls");
      const input = inputID ? document.getElementById(inputID) : null;
      if (!(button instanceof HTMLButtonElement) || !(input instanceof HTMLInputElement)
        || !host.contains(input)) return null;
      return { button, input };
    };

    const enhance = () => {
      const current = control();
      if (!current) return;
      if (current.input.type !== "password" && current.input.type !== "text") {
        current.button.hidden = true;
        current.button.setAttribute("aria-pressed", "false");
        current.button.setAttribute("aria-label", "Show password");
        return;
      }
      const empty = current.input.value.length === 0;
      if (empty && current.input.type === "text") current.input.type = "password";
      current.button.hidden = empty;
      current.button.setAttribute("aria-pressed", String(current.input.type === "text"));
      current.button.setAttribute("aria-label", current.input.type === "text" ? "Hide password" : "Show password");
    };

    const onValueChange = event => {
      if (event.target === control()?.input) enhance();
    };

    const onReset = event => {
      const current = control();
      if (!(event.target instanceof HTMLFormElement) || current?.input.form !== event.target) return;
      clearTimeout(resetTimeout);
      resetTimeout = window.setTimeout(() => {
        resetTimeout = undefined;
        if (event.defaultPrevented) return;
        const current = control();
        if (!current) return;
        current.input.type = "password";
        enhance();
      }, 0);
    };

    const onClick = event => {
      const target = elementTarget(event.target);
      const button = target?.closest("[data-password-toggle]");
      if (!button || !host.contains(button)) return;
      const current = control();
      if (!current || current.button !== button || current.input.disabled) return;
      if (current.input.value.length === 0) {
        enhance();
        return;
      }
      event.preventDefault();
      current.input.type = current.input.type === "password" ? "text" : "password";
      current.button.setAttribute("aria-pressed", String(current.input.type === "text"));
      current.button.setAttribute("aria-label", current.input.type === "text" ? "Hide password" : "Show password");
    };

    const observer = new MutationObserver(enhance);
    observer.observe(host, { childList: true, subtree: true });
    cleanup(() => observer.disconnect());
    cleanup(() => clearTimeout(resetTimeout));
    listen(host, "click", onClick, cleanup);
    listen(host, "input", onValueChange, cleanup);
    listen(host, "change", onValueChange, cleanup);
    listen(document, "reset", onReset, cleanup, true);
    enhance();
  },
});

define("ts-mobile-nav", {
  mode: "light",
  setup({ host, cleanup }) {
    const media = window.matchMedia("(max-width: 47.99rem)");
    const insetStates = new Map();
    let open = false;
    let scrollY = 0;
    let bodyStyles;
    let htmlOverflow;

    const drawer = () => host.querySelector("#ts-navigation");
    const inset = () => host.querySelector(".ts-inset");
    const trigger = () => host.querySelector(".ts-fab-nav");
    const closeButton = () => host.querySelector(".ts-sidebar-close");
    const visible = element => {
      const style = getComputedStyle(element);
      return style.display !== "none" && style.visibility !== "hidden"
        && element.getClientRects().length > 0;
    };
    const drawerFocusable = () => {
      const currentDrawer = drawer();
      return currentDrawer
        ? [...currentDrawer.querySelectorAll(focusableSelector)].filter(element => {
          const closedMenu = element.closest("details:not([open])");
          return (!closedMenu || element.tagName === "SUMMARY") && visible(element);
        })
        : [];
    };
    const blockInset = () => {
      const currentInset = inset();
      if (!currentInset) return;
      if (!insetStates.has(currentInset)) {
        insetStates.set(currentInset, {
          ariaHidden: currentInset.getAttribute("aria-hidden"),
          inert: currentInset.hasAttribute("inert"),
        });
      }
      currentInset.inert = true;
      currentInset.setAttribute("aria-hidden", "true");
    };
    const restoreInsets = () => {
      for (const [element, previous] of insetStates) {
        element.inert = previous.inert;
        if (previous.ariaHidden === null) element.removeAttribute("aria-hidden");
        else element.setAttribute("aria-hidden", previous.ariaHidden);
      }
      insetStates.clear();
    };
    const lockPage = () => {
      if (bodyStyles || !document.body) return;
      scrollY = window.scrollY;
      bodyStyles = {
        position: document.body.style.position,
        top: document.body.style.top,
        width: document.body.style.width,
        overflow: document.body.style.overflow,
        paddingRight: document.body.style.paddingRight,
      };
      htmlOverflow = document.documentElement.style.overflow;
      const scrollbarWidth = window.innerWidth - document.documentElement.clientWidth;
      document.body.style.position = "fixed";
      document.body.style.top = `-${scrollY}px`;
      document.body.style.width = "100%";
      document.body.style.overflow = "hidden";
      if (scrollbarWidth > 0) document.body.style.paddingRight = `${scrollbarWidth}px`;
      document.documentElement.style.overflow = "hidden";
    };
    const unlockPage = () => {
      if (bodyStyles && document.body) {
        document.body.style.position = bodyStyles.position;
        document.body.style.top = bodyStyles.top;
        document.body.style.width = bodyStyles.width;
        document.body.style.overflow = bodyStyles.overflow;
        document.body.style.paddingRight = bodyStyles.paddingRight;
        document.documentElement.style.overflow = htmlOverflow;
        bodyStyles = undefined;
        htmlOverflow = undefined;
        window.scrollTo(0, scrollY);
      }
    };
    const closeWithSignal = () => {
      const button = closeButton() ?? trigger();
      if (button instanceof HTMLElement) button.click();
    };
    const sync = () => {
      const nextOpen = host.classList.contains("nav-open");
      if (nextOpen === open) {
        if (nextOpen) blockInset();
        return;
      }
      open = nextOpen;

      if (open) {
        blockInset();
        lockPage();
        const close = closeButton();
        const [first] = drawerFocusable();
        (close instanceof HTMLElement && visible(close) ? close : first)?.focus({ preventScroll: true });
      } else {
        restoreInsets();
        unlockPage();
        trigger()?.focus({ preventScroll: true });
      }
    };
    const onKeydown = event => {
      if (!open) return;
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        closeWithSignal();
        return;
      }
      if (event.key !== "Tab") return;
      const currentDrawer = drawer();
      const focusable = drawerFocusable();
      if (!currentDrawer || focusable.length === 0) {
        event.preventDefault();
        currentDrawer?.focus({ preventScroll: true });
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus({ preventScroll: true });
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus({ preventScroll: true });
      }
    };
    const onFocusIn = event => {
      const currentDrawer = drawer();
      if (!open || !currentDrawer || currentDrawer.contains(elementTarget(event.target))) return;
      drawerFocusable()[0]?.focus({ preventScroll: true });
    };
    const onMediaChange = () => {
      if (!media.matches && host.classList.contains("nav-open")) closeWithSignal();
    };

    const observer = new MutationObserver(sync);
    observer.observe(host, { attributes: true, attributeFilter: ["class"], childList: true, subtree: true });
    cleanup(() => observer.disconnect());
    listen(host, "keydown", onKeydown, cleanup, true);
    listen(host, "focusin", onFocusIn, cleanup, true);
    if (media.addEventListener) listen(media, "change", onMediaChange, cleanup);
    else {
      media.addListener(onMediaChange);
      cleanup(() => media.removeListener(onMediaChange));
    }
    cleanup(() => {
      restoreInsets();
      unlockPage();
    });
    sync();
    onMediaChange();
  },
});

function loadRegionMapWhenPresent() {
  const load = () => import("./region-map.js");
  if (document.querySelector("ts-region-map")) {
    void load();
    return;
  }

  const observer = new MutationObserver(() => {
    if (!document.querySelector("ts-region-map")) return;
    observer.disconnect();
    window.removeEventListener("pagehide", stopObserving);
    void load();
  });
  const stopObserving = () => observer.disconnect();
  observer.observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener("pagehide", stopObserving, { once: true });
}

loadRegionMapWhenPresent();
