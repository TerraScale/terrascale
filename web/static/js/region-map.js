import { rocket } from "./datastar-rocket.js";

const regionMapSource = "/brand/region-map.svg";
const selectionAttribute = "data-region-map-selection";
const mapWidth = 136;
const mapHeight = 80;
const loginHighlights = Object.freeze(["SFO", "GRU", "FRA"]);
const decorativePins = Object.freeze({
  SFO: Object.freeze({ x: 21, y: 28.58 }),
  GRU: Object.freeze({ x: 50, y: 59.76 }),
  FRA: Object.freeze({ x: 71.5, y: 20.78 }),
});
const decorativeArcs = Object.freeze([
  Object.freeze(["SFO", "GRU"]),
  Object.freeze(["GRU", "FRA"]),
  Object.freeze(["FRA", "SFO"]),
]);

function selectedCodes(select) {
  if (!(select instanceof HTMLSelectElement)) return [];
  return select.multiple
    ? [...select.selectedOptions].map(option => option.value).filter(Boolean)
    : select.value ? [select.value] : [];
}

function selectionFor(host) {
  const selectID = host.getAttribute("data-select-id");
  return selectID ? document.getElementById(selectID) : null;
}

function updateSelection(host, select) {
  if (selectionFor(host) !== select) return;
  const badge = host.querySelector(`[${selectionAttribute}]`);
  if (!badge) return;
  const codes = selectedCodes(select);
  const label = codes.join(", ");
  if (badge.textContent !== label) badge.textContent = label;
  if (badge.hidden !== (codes.length === 0)) badge.hidden = codes.length === 0;
}

function highlightedCodes(host) {
  const codes = (host.getAttribute("data-highlights") ?? "")
    .split(",")
    .map(code => code.trim().toUpperCase())
    .filter(Boolean);
  return codes.join(",") === loginHighlights.join(",") ? loginHighlights : [];
}

function arcPath(from, to) {
  const midX = (from.x + to.x) / 2;
  const length = Math.hypot(to.x - from.x, to.y - from.y);
  const lift = Math.max(3, length * 0.18);
  const controlY = Math.min(from.y, to.y) - lift - Math.abs(to.x - from.x) * 0.04;
  return `M ${from.x} ${from.y} Q ${midX} ${controlY} ${to.x} ${to.y}`;
}

function appendHighlights(host, fragment) {
  const codes = highlightedCodes(host);
  if (codes.length === 0) return;

  const overlay = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  overlay.classList.add("ts-region-overlay");
  overlay.setAttribute("viewBox", `0 0 ${mapWidth} ${mapHeight}`);
  overlay.setAttribute("preserveAspectRatio", "xMidYMid meet");
  overlay.setAttribute("aria-hidden", "true");
  for (let index = 0; index < decorativeArcs.length; index += 1) {
    const [fromCode, toCode] = decorativeArcs[index];
    const arc = document.createElementNS("http://www.w3.org/2000/svg", "path");
    arc.classList.add("ts-region-arc");
    arc.setAttribute("d", arcPath(decorativePins[fromCode], decorativePins[toCode]));
    arc.style.animationDelay = `${index * 0.45}s`;
    overlay.append(arc);
  }
  fragment.append(overlay);

  for (let index = 0; index < codes.length; index += 1) {
    const code = codes[index];
    const pin = decorativePins[code];
    const node = document.createElement("span");
    node.classList.add("ts-region-node");
    node.dataset.regionCode = code;
    node.setAttribute("aria-hidden", "true");
    node.style.left = `${pin.x / mapWidth * 100}%`;
    node.style.top = `${pin.y / mapHeight * 100}%`;

    const ring = document.createElement("span");
    ring.classList.add("ts-region-node-ring");
    ring.style.animationDelay = `${index * 0.3}s`;
    const core = document.createElement("span");
    core.classList.add("ts-region-node-core");
    node.append(ring, core);
    fragment.append(node);
  }
}

if (!customElements.get("ts-region-map")) {
  rocket("ts-region-map", {
    mode: "light",
    setup({ host, cleanup }) {
      const onChange = event => {
        const target = event.target;
        if (target instanceof HTMLSelectElement) updateSelection(host, target);
      };
      const observer = new MutationObserver(() => updateSelection(host, selectionFor(host)));
      observer.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["selected"],
        childList: true,
        subtree: true,
      });
      document.addEventListener("change", onChange);
      cleanup(() => document.removeEventListener("change", onChange));
      cleanup(() => observer.disconnect());
    },
    render({ host }) {
      const fragment = document.createDocumentFragment();
      if (host.getAttribute("data-src") !== regionMapSource) return fragment;

      const image = document.createElement("img");
      image.className = "ts-region-map-image";
      image.src = regionMapSource;
      image.alt = "";
      image.loading = "lazy";
      image.decoding = "async";
      image.setAttribute("aria-hidden", "true");
      fragment.append(image);
      appendHighlights(host, fragment);

      const select = selectionFor(host);
      if (select instanceof HTMLSelectElement) {
        const badge = document.createElement("span");
        badge.className = "ts-region-map-selection";
        badge.setAttribute(selectionAttribute, "");
        badge.setAttribute("aria-hidden", "true");
        const codes = selectedCodes(select);
        badge.textContent = codes.join(", ");
        badge.hidden = codes.length === 0;
        fragment.append(badge);
      }
      return fragment;
    },
  });
}
