const REFRESH_SECONDS = 30;

(() => {
  const root = document.documentElement;
  root.dataset.js = "";

  const storageKey = "ts-status-time-zone";
  const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  const isZone = (zone) => {
    try {
      new Intl.DateTimeFormat("en", { timeZone: zone });
      return true;
    } catch {
      return false;
    }
  };
  const stored = () => {
    try {
      const zone = localStorage.getItem(storageKey);
      return zone && isZone(zone) ? zone : "";
    } catch {
      return "";
    }
  };
  let zone = stored() || browserZone;

  const styles = {
    datetime: { dateStyle: "medium", timeStyle: "short", hourCycle: "h23" },
    clock: { hour: "2-digit", minute: "2-digit", hourCycle: "h23" },
    seconds: { timeStyle: "medium", hourCycle: "h23" },
    day: { month: "short", day: "numeric" },
  };

  const zoneLabel = () => {
    if (zone === "UTC") return "UTC";
    const part = new Intl.DateTimeFormat("en", { timeZone: zone, timeZoneName: "short" })
      .formatToParts(new Date())
      .find((item) => item.type === "timeZoneName");
    return part ? part.value : zone;
  };

  const localize = async (root) => {
    const payload = root.querySelector("[data-bar-data]");
    let records = [];
    if (payload) {
      payload.remove();
      try {
        records = await new Response(
          new Blob([Uint8Array.from(atob(payload.textContent), (char) => char.charCodeAt(0))])
            .stream()
            .pipeThrough(new DecompressionStream("gzip")),
        ).json();
      } catch {
        // Keep the labeled SVG fallback; times and live refresh still work.
      }
    }
    // Expand compact SSR bars before localization; the server's SVG remains
    // readable when scripts are unavailable.
    for (const [index, svg] of [...root.querySelectorAll("svg[data-bars]")].entries()) {
      const data = records[index];
      if (!data) continue;
      const strip = document.createElement("ol");
      strip.className = svg.classList.contains("st-bars-lg") ? "st-bars st-bars-lg" : "st-bars";
      strip.setAttribute("role", "img");
      strip.tabIndex = 0;
      strip.dataset.strip = "";
      strip.setAttribute("aria-label", svg.getAttribute("aria-label"));
      data.forEach(({ state, from, to, card }) => {
        const cell = document.createElement("li");
        const daily = to - from >= 86400000;
        const day = new Intl.DateTimeFormat("en", { month: "short", day: "numeric", timeZone: "UTC" });
        const when = daily ? day.format(from) : "";
        Object.assign(cell.dataset, {
          state: state[0],
          label: state[1],
          from,
          to,
          span: daily ? "day" : "hour",
          when,
          tip: when + " · " + state[1],
          card: JSON.stringify(card),
        });
        strip.append(cell);
      });
      svg.replaceWith(strip);
    }
    const label = zoneLabel();
    const format = {};
    for (const name in styles) format[name] = new Intl.DateTimeFormat("en", { ...styles[name], timeZone: zone });
    for (const element of root.querySelectorAll("time[data-format]")) {
      element.textContent =
        format[element.dataset.format].format(new Date(element.dateTime)) +
        (element.dataset.format === "clock" ? "" : " " + label);
    }
    // Spans shorter than a day follow the zone; UTC days keep their dates.
    for (const element of root.querySelectorAll("[data-from][data-span=hour]")) {
      const from = Number(element.dataset.from);
      const when =
        format.day.format(from) +
        ", " +
        format.clock.format(from) +
        "–" +
        format.clock.format(Number(element.dataset.to)) +
        " " +
        label;
      element.dataset.when = when;
      element.dataset.tip = when + " · " + element.dataset.label;
    }
    for (const element of root.querySelectorAll("[data-zone]")) element.textContent = label;
  };

  const select = document.getElementById("st-zone");
  if (select && Intl.supportedValuesOf) {
    const zones = Intl.supportedValuesOf("timeZone");
    if (!zones.includes("UTC")) zones.unshift("UTC");
    // The automatic choice names only the local city, so the control stays small.
    const city = browserZone.split("/").pop().replaceAll("_", " ");
    select.append(new Option(city, ""), ...zones.map((name) => new Option(name.replaceAll("_", " "), name)));
    select.value = stored();
    select.addEventListener("change", () => {
      zone = select.value || browserZone;
      try {
        if (select.value) localStorage.setItem(storageKey, select.value);
        else localStorage.removeItem(storageKey);
      } catch {
        // Private mode: the choice lasts for this page only.
      }
      void localize(document);
    });
    select.closest("[hidden]")?.removeAttribute("hidden");
  }
  void localize(document);

  // "Last round 12s ago" counters, and "starts in 2h 5m" countdowns.
  const span = (seconds) => {
    const minutes = Math.floor(seconds / 60);
    if (minutes < 1) return seconds + "s";
    if (minutes < 60) return minutes + "m";
    const hours = Math.floor(minutes / 60);
    if (hours < 48) return hours + "h " + (minutes % 60) + "m";
    return Math.floor(hours / 24) + "d " + (hours % 24) + "h";
  };
  const ago = () => {
    for (const element of document.querySelectorAll("[data-ago]")) {
      const seconds = Math.max(0, Math.round((Date.now() - Number(element.dataset.ago)) / 1000));
      element.textContent = seconds < 90 ? seconds + "s ago" : Math.round(seconds / 60) + "m ago";
    }
    for (const element of document.querySelectorAll("[data-until]")) {
      const seconds = Math.round((Number(element.dataset.until) - Date.now()) / 1000);
      element.textContent = seconds > 0 ? element.dataset.prefix + " in " + span(seconds) : "";
    }
  };
  ago();
  setInterval(() => document.hidden || ago(), 1000);

  // One hover card, filled from the data-card JSON of the element it
  // describes and placed beside it through CSSOM.
  const card = document.createElement("div");
  card.className = "st-card";
  card.setAttribute("role", "tooltip");
  card.hidden = true;
  document.body.append(card);
  const live = document.getElementById("st-live");
  let current = null;

  const element = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };

  // Above the element, or below it near the top of the viewport.
  const place = () => {
    if (!current) return;
    const box = current.getBoundingClientRect();
    const left = Math.min(
      Math.max(8, box.left + box.width / 2 - card.offsetWidth / 2),
      innerWidth - card.offsetWidth - 8,
    );
    const above = box.top - card.offsetHeight - 10;
    card.style.left = left + "px";
    card.style.top = (above >= 8 ? above : box.bottom + 10) + "px";
  };

  const show = (target, announce) => {
    let data = {};
    try {
      data = JSON.parse(target.dataset.card || "{}");
    } catch {
      // Malformed content shows the header alone.
    }
    const title = target.dataset.when || target.dataset.title || "";
    const head = element("div", "st-card-head");
    head.append(element("span", "st-card-title", title));
    if (target.dataset.label) {
      const badge = element("span", "ts-badge");
      badge.dataset.state = target.dataset.state || "none";
      badge.append(element("span", "ts-dot"), target.dataset.label);
      head.append(badge);
    }
    const parts = [head];
    const rows = Array.isArray(data.rows) ? data.rows : [];
    if (rows.length > 0) {
      const list = element("dl", "st-card-rows");
      for (const [name, value] of rows) list.append(element("dt", "", name), element("dd", "", value));
      parts.push(list);
    }
    const notes = Array.isArray(data.notes) ? data.notes : [];
    if (notes.length > 0) {
      const list = element("ul", "st-card-notes");
      for (const note of notes) list.append(element("li", "", note));
      parts.push(list);
    }
    card.replaceChildren(...parts);

    current?.classList.remove("is-active");
    current = target;
    target.classList.add("is-active");
    card.hidden = false;
    place();
    if (announce && live) {
      live.textContent = [title, target.dataset.label, ...rows.map((row) => row.join(" ")), ...notes]
        .filter(Boolean)
        .join(". ");
    }
  };

  const hide = () => {
    card.hidden = true;
    current?.classList.remove("is-active");
    current = null;
  };

  const cardOf = (node) => (node instanceof Element ? node.closest("[data-card]") : null);

  document.addEventListener("pointerover", (event) => {
    if (event.pointerType === "touch") return;
    const target = cardOf(event.target);
    if (target) show(target, false);
    else if (current) hide();
  });
  document.addEventListener("pointerout", (event) => event.relatedTarget || hide());
  // Taps open a card and a second tap, or a tap elsewhere, closes it.
  document.addEventListener("click", (event) => {
    const target = cardOf(event.target);
    if (target && target !== current) show(target, false);
    else hide();
  });
  document.addEventListener("focusin", (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    // A tap focuses the strip too; only keyboard focus opens its newest cell,
    // or the tap's own click would close the card it just opened.
    if (target.matches("[data-strip]")) {
      if (!target.matches(":focus-visible")) return;
      const cells = target.children;
      const active = target.querySelector(".is-active");
      show(active || cells[cells.length - 1], true);
    } else if (target.matches("[data-card]")) show(target, true);
  });
  document.addEventListener("focusout", (event) => {
    const next = event.relatedTarget;
    if (!(next instanceof Element) || !(next.matches("[data-strip]") || next.matches("[data-card]"))) hide();
  });
  // Arrow keys move between the cells of a focused strip.
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") return hide();
    const strip = event.target instanceof Element && event.target.matches("[data-strip]") ? event.target : null;
    if (!strip || !current || current.parentElement !== strip) return;
    const cells = [...strip.children];
    const index = cells.indexOf(current);
    const next = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: cells.length - 1 }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    show(cells[Math.max(0, Math.min(cells.length - 1, next))], true);
  });
  addEventListener("scroll", place, { passive: true });
  addEventListener("resize", place);

  // The ping page's browser test: a warm-up request to open the connection,
  // then timed ones; opaque (no-cors) responses still time the trip. A
  // request that never answers fails after a timeout.
  const samples = 5;
  const start = document.querySelector("[data-ping-start]");
  if (start) {
    start.disabled = false;
    const once = async (url) => {
      const same = url.startsWith("/");
      const began = performance.now();
      const response = await fetch(
        url + (url.includes("?") ? "&" : "?") + "ts-ping=" + Math.random().toString(36).slice(2),
        {
          mode: same ? "same-origin" : "no-cors",
          cache: "no-store",
          credentials: "omit",
          signal: AbortSignal.timeout(10000),
        },
      );
      const elapsed = performance.now() - began;
      return { elapsed, response: same ? response : null };
    };
    start.addEventListener("click", async () => {
      start.disabled = true;
      start.textContent = "Testing…";
      try {
        for (const row of document.querySelectorAll("[data-ping-url]")) {
          const result = row.querySelector("[data-ping-result]");
          const fastest = row.querySelector("[data-ping-min]");
          result.textContent = "…";
          try {
            await once(row.dataset.pingUrl);
            const times = [];
            let last = null;
            for (let index = 0; index < samples; index++) {
              const { elapsed, response } = await once(row.dataset.pingUrl);
              times.push(elapsed);
              last = response;
            }
            times.sort((a, b) => a - b);
            result.textContent = Math.round(times[Math.floor(times.length / 2)]) + " ms";
            fastest.textContent = Math.round(times[0]) + " ms";
            if (last) {
              const data = await last.json().catch(() => null);
              const colo = row.querySelector("[data-ping-colo]");
              if (data && data.colo && colo) colo.textContent = " · " + data.colo;
            }
          } catch {
            result.textContent = "Unreachable";
            fastest.textContent = "—";
          }
        }
      } finally {
        start.disabled = false;
        start.textContent = "Test again";
      }
    });
  }

  const main = document.querySelector("main[data-live]");
  if (!main) return;
  const interval = REFRESH_SECONDS * 1000;
  let last = Date.now();
  let busy = false;
  const refresh = async () => {
    if (busy || document.hidden) return;
    busy = true;
    try {
      const url = new URL(location.href);
      url.searchParams.set("fragment", "main");
      const response = await fetch(url, { headers: { accept: "text/html" } });
      if (!response.ok) return;
      const template = document.createElement("template");
      template.innerHTML = await response.text();
      const next = template.content.querySelector("main");
      if (!next) return;
      await localize(next);
      // Keep keyboard focus on the same element across the swap, found by
      // its position among the focusable elements: labels carry live values.
      const focusable = "a[href], button, input, select, [tabindex]";
      const page = document.querySelector("main");
      const position = [...page.querySelectorAll(focusable)].indexOf(document.activeElement);
      // Sections the visitor opened stay open.
      for (const open of page.querySelectorAll("details[open][id]")) {
        next.querySelector("#" + CSS.escape(open.id))?.setAttribute("open", "");
      }
      hide();
      page.replaceWith(next);
      ago();
      document.title = next.dataset.title || document.title;
      if (position >= 0) next.querySelectorAll(focusable)[position]?.focus();
      last = Date.now();
    } catch {
      // Offline or failing: keep the last good page and try again later.
    } finally {
      busy = false;
    }
  };
  setInterval(refresh, interval);
  document.addEventListener("visibilitychange", () => Date.now() - last >= interval && refresh());
})();
