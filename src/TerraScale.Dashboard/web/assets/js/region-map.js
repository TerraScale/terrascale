import { columns, rows, rowHeight, land, regions as pins } from './region-map-data.js';

const still = matchMedia('(prefers-reduced-motion: reduce)');
const scheme = matchMedia('(prefers-color-scheme: dark)');
const count = rows * columns;
const height = (rows - 1) * rowHeight;
const bits = Uint8Array.from(atob(land), char => char.charCodeAt(0));
const pointX = new Float32Array(count);
const pointY = new Float32Array(count);
const isLand = new Uint8Array(count);
for (let row = 0; row < rows; row++) {
  for (let column = 0; column < columns; column++) {
    const index = row * columns + column;
    pointX[index] = column + (row % 2 ? 0 : 0.5);
    pointY[index] = row * rowHeight;
    isLand[index] = (bits[index >> 3] >> (index & 7)) & 1;
  }
}

const sides = {
  wide: { gru: 'right', fra: 'right', sjc: 'top', sfo: 'left' },
  narrow: { gru: 'bottom', fra: 'top', sjc: 'bottom', sfo: 'top' },
};
const reach = new Map();
const distance = code => {
  if (!reach.has(code)) reach.set(code, Float32Array.from(pointX, (x, index) => Math.hypot(x - pins[code].x, pointY[index] - pins[code].y)));
  return reach.get(code);
};
const clamp = (value, low, high) => Math.min(Math.max(value, low), Math.max(low, high));
const mix = (from, to, amount) => [from[0] + (to[0] - from[0]) * amount, from[1] + (to[1] - from[1]) * amount, from[2] + (to[2] - from[2]) * amount];
const smooth = value => value * value * (3 - 2 * value);
const overlap = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
const probe = document.createElement('canvas').getContext('2d');
const tone = (element, name) => {
  probe.fillStyle = getComputedStyle(element).getPropertyValue(name).trim();
  return [1, 3, 5].map(start => Number.parseInt(probe.fillStyle.slice(start, start + 2), 16));
};

const mount = root => {
  const live = new AbortController();
  const signal = live.signal;
  const bleed = root.dataset.bleed === 'true';
  const surface = bleed ? root.closest('[data-region-surface]') ?? root : root;
  const canvas = root.querySelector('.ts-region-map-sea');
  const ctx = canvas.getContext('2d');
  const stage = root.querySelector('.ts-region-map-stage');
  const status = root.querySelector('[data-region-status]');
  const orderInput = root.querySelector('[data-region-order]');
  const form = root.closest('form');
  let link;
  let bright;
  let base;
  const recolor = () => {
    link = tone(root, '--ts-link');
    bright = tone(root, '--ts-ink');
    base = tone(root, '--ts-ink-3');
  };
  recolor();
  const entries = [...root.querySelectorAll('.ts-region-callout')].map(callout => {
    const code = callout.dataset.region;
    const input = callout.querySelector('input');
    return {
      code, callout, input,
      node: root.querySelector(`.ts-region-node[data-region="${CSS.escape(code)}"]`),
      pin: Object.hasOwn(pins, code) ? pins[code] : undefined,
      on: input ? input.checked : callout.dataset.selected === 'true',
      glow: { level: 0, target: 0, radius: 4.5, wanted: 4.5 },
      x: 0, y: 0,
    };
  });
  const byCode = new Map(entries.map(entry => [entry.code, entry]));
  const placed = entries.filter(entry => entry.pin);
  for (const entry of entries) {
    if (entry.pin) continue;
    entry.callout.dataset.docked = '';
    entry.node.hidden = true;
  }
  const center = placed.length === 0 ? [columns / 2, height / 2]
    : [placed.reduce((sum, entry) => sum + entry.pin.x, 0) / placed.length, placed.reduce((sum, entry) => sum + entry.pin.y, 0) / placed.length];

  const arc = (from, to) => {
    const [a, b] = [pins[from], pins[to]];
    const length = Math.hypot(b.x - a.x, b.y - a.y);
    const [mx, my] = [(a.x + b.x) / 2, (a.y + b.y) / 2];
    let [nx, ny] = [(a.y - b.y) / length, (b.x - a.x) / length];
    const outward = (mx - center[0]) * nx + (my - center[1]) * ny;
    if (Math.abs(outward) > 0.5 ? outward < 0 : ny > 0) [nx, ny] = [-nx, -ny];
    return [mx + nx * length * 0.24, my + ny * length * 0.24];
  };

  const trails = new Map();
  const trail = (from, to) => {
    const key = from + '|' + to;
    if (trails.has(key)) return trails.get(key);
    const [a, b, control] = [pins[from], pins[to], arc(from, to)];
    const best = new Map();
    for (let step = 0; step <= 400; step++) {
      const t = step / 400;
      const u = 1 - t;
      const x = u * u * a.x + 2 * u * t * control[0] + t * t * b.x;
      const y = u * u * a.y + 2 * u * t * control[1] + t * t * b.y;
      const near = Math.round(y / rowHeight);
      for (let row = near - 1; row <= near + 1; row++) {
        if (row < 0 || row >= rows) continue;
        const column = Math.round(x - (row % 2 ? 0 : 0.5));
        for (let at = column - 1; at <= column + 1; at++) {
          if (at < 0 || at >= columns) continue;
          const index = row * columns + at;
          const gap = Math.hypot(pointX[index] - x, pointY[index] - y);
          if (gap < 0.56 && (!best.has(index) || best.get(index).gap > gap)) best.set(index, { index, t, gap });
        }
      }
    }
    const samples = [...best.values()].filter(sample => distance(from)[sample.index] > 1.6 && distance(to)[sample.index] > 1.6).sort((p, q) => p.t - q.t);
    trails.set(key, samples);
    return samples;
  };

  const ring = codes => {
    if (codes.length < 2) return [];
    const cx = codes.reduce((sum, code) => sum + pins[code].x, 0) / codes.length;
    const cy = codes.reduce((sum, code) => sum + pins[code].y, 0) / codes.length;
    const around = [...codes].sort((p, q) => Math.atan2(pins[p].y - cy, pins[p].x - cx) - Math.atan2(pins[q].y - cy, pins[q].x - cx));
    return around.length === 2 ? [around] : around.map((code, index) => [code, around[(index + 1) % around.length]]);
  };

  const links = new Map();
  let waves = [];
  let view = { u: 5, w: 0, h: 0, dpr: 1 };
  let screenX = new Float32Array(count);
  let screenY = new Float32Array(count);
  let fade = new Float32Array(count);
  let seen = [];
  let backdrop = null;
  let frame = 0;
  let last = 0;
  let busy = false;
  let beat = 0;
  let pulse = 0;
  let turn = 0;
  let shown = false;
  let notice = 0;
  let order = (orderInput?.value ?? '').split(' ').filter(code => byCode.get(code)?.on);

  const wake = () => { if (!frame) frame = requestAnimationFrame(step); };
  const wave = (code, max, ms, amp, width = 3.6) => {
    if (still.matches || !Object.hasOwn(pins, code)) return;
    waves.push({ code, max, ms, amp, width, age: 0 });
    wake();
  };

  const dot = (index, color, alpha, grow) => {
    ctx.globalAlpha = Math.min(1, alpha);
    ctx.fillStyle = `rgb(${color[0]} ${color[1]} ${color[2]})`;
    ctx.beginPath();
    ctx.arc(screenX[index], screenY[index], Math.max(0.95, view.u * 0.2) * (1 + grow), 0, 6.2832);
    ctx.fill();
  };

  const draw = () => {
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (!backdrop) return;
    ctx.drawImage(backdrop, 0, 0);
    ctx.setTransform(view.dpr, 0, 0, view.dpr, 0, 0);
    const lit = placed.filter(entry => entry.glow.level > 0.01);
    const rings = waves.map(ringWave => {
      const t = ringWave.age / ringWave.ms;
      return { reach: distance(ringWave.code), at: ringWave.max * (1 - (1 - t) * (1 - t)), amp: ringWave.amp * (1 - t) ** 1.3, width: ringWave.width };
    });
    if (lit.length > 0 || rings.length > 0) {
      for (const index of seen) {
        let alpha = 0;
        let grow = 0;
        if (isLand[index]) {
          for (const entry of lit) {
            const gap = distance(entry.code)[index];
            if (gap >= entry.glow.radius) continue;
            alpha = Math.max(alpha, (1 - gap / entry.glow.radius) ** 1.1 * entry.glow.level);
          }
        }
        for (const ringWave of rings) {
          const offset = (ringWave.reach[index] - ringWave.at) / ringWave.width;
          if (offset > 1 || offset < -1) continue;
          const value = (1 - offset * offset) ** 2 * ringWave.amp * (isLand[index] ? 1 : 0.42);
          grow = Math.max(grow, value);
          alpha = Math.max(alpha, value);
        }
        if (alpha > 0.03) dot(index, link, alpha * fade[index], grow * 0.55);
      }
    }
    for (const line of links.values()) {
      const head = smooth(Math.min(1, line.progress));
      for (const sample of line.samples) {
        if ((line.flip ? 1 - sample.t : sample.t) > head) continue;
        let alpha = 0.8 * line.alpha;
        let grow = 0;
        if (busy) {
          const gap = Math.min(Math.abs(sample.t - beat), Math.abs(1 - sample.t - beat));
          if (gap < 0.14) {
            grow = (1 - gap / 0.14) ** 2;
            alpha = Math.min(1, alpha + grow * 0.4);
          }
        }
        dot(sample.index, grow > 0.05 ? mix(link, bright, grow * 0.8) : link, alpha * fade[sample.index], grow * 0.7);
      }
    }
    ctx.globalAlpha = 1;
  };

  const step = now => {
    const elapsed = Math.min(64, now - (last || now));
    last = now;
    const ease = 1 - Math.exp(-elapsed / 75);
    let moving = busy;
    for (const entry of placed) {
      for (const [value, target] of [['level', 'target'], ['radius', 'wanted']]) {
        if (Math.abs(entry.glow[target] - entry.glow[value]) < 0.004) {
          entry.glow[value] = entry.glow[target];
          continue;
        }
        entry.glow[value] += (entry.glow[target] - entry.glow[value]) * ease;
        moving = true;
      }
    }
    for (const [key, line] of links) {
      if (line.leaving) {
        line.alpha -= elapsed / 200;
        if (line.alpha <= 0) links.delete(key);
        moving = true;
      } else if (line.progress < 1) {
        line.progress += elapsed / 420;
        moving = true;
      }
    }
    for (const ringWave of waves) ringWave.age += elapsed;
    waves = waves.filter(ringWave => ringWave.age < ringWave.ms);
    if (waves.length > 0) moving = true;
    if (busy) beat = ((now % 1400) / 1400) * 1.3 - 0.15;
    draw();
    frame = moving ? requestAnimationFrame(step) : 0;
    if (!moving) last = 0;
  };

  const settle = () => {
    for (const entry of placed) {
      entry.glow.level = entry.glow.target;
      entry.glow.radius = entry.glow.wanted;
    }
    for (const [key, line] of links) {
      if (line.leaving) links.delete(key);
      else line.progress = 1;
    }
    waves = [];
    draw();
  };

  const layout = () => {
    const w = surface.clientWidth;
    const h = surface.clientHeight;
    const stageWidth = stage.offsetWidth;
    const tall = stage.offsetHeight;
    if (!w || !h || !stageWidth || !tall) return;
    let top = 0;
    let left = 0;
    for (let element = stage; element && element !== surface; element = element.offsetParent) {
      top += element.offsetTop;
      left += element.offsetLeft;
    }
    const frameLeft = bleed ? 0 : left;
    const frameWidth = bleed ? w : stageWidth;
    const wide = frameWidth >= 520;
    const xs = placed.map(entry => entry.pin.x);
    const ys = placed.map(entry => entry.pin.y);
    const [x0, x1] = placed.length > 0 ? [Math.min(...xs), Math.max(...xs)] : [0, columns];
    const [y0, y1] = placed.length > 0 ? [Math.min(...ys), Math.max(...ys)] : [0, height];
    const spanX = wide ? columns : Math.min(columns, Math.max(84, x1 - x0 + 34));
    const spanY = Math.min(height, Math.max(66, y1 - y0 + 27));
    const cx = clamp((x0 + x1) / 2, spanX / 2, columns - spanX / 2);
    const cy = clamp((y0 + y1) / 2, spanY / 2, height - spanY / 2);
    const u = Math.min(frameWidth / spanX, tall / spanY);
    const ox = frameLeft + frameWidth / 2 - cx * u;
    const oy = top + tall / 2 - cy * u;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    view = { u, w, h, dpr };
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    seen = [];
    for (let index = 0; index < count; index++) {
      screenX[index] = ox + pointX[index] * u;
      screenY[index] = oy + pointY[index] * u;
      const outside = Math.max(top - 6 - screenY[index], screenY[index] - (top + tall + 6), 0);
      fade[index] = Math.max(0.16, 1 - outside / 44);
      if (screenX[index] > -4 && screenX[index] < w + 4 && screenY[index] > -4 && screenY[index] < h + 4) seen.push(index);
    }
    backdrop = document.createElement('canvas');
    backdrop.width = canvas.width;
    backdrop.height = canvas.height;
    const pen = backdrop.getContext('2d');
    pen.scale(dpr, dpr);
    pen.fillStyle = `rgb(${base[0]} ${base[1]} ${base[2]})`;
    const radius = Math.max(0.85, u * 0.19);
    for (const index of seen) {
      if (!isLand[index]) continue;
      pen.globalAlpha = 0.6 * fade[index];
      pen.beginPath();
      pen.arc(screenX[index], screenY[index], radius, 0, 6.2832);
      pen.fill();
    }
    for (const entry of placed) {
      entry.x = ox + entry.pin.x * u;
      entry.y = oy + entry.pin.y * u;
    }
    for (const [index, entry] of placed.entries()) {
      for (const other of placed.slice(index + 1)) {
        if (Math.hypot(entry.x - other.x, entry.y - other.y) >= 18) continue;
        const shift = entry.x <= other.x ? -9 : 9;
        entry.x += shift;
        other.x -= shift;
      }
    }
    const taken = [];
    const preferred = sides[wide ? 'wide' : 'narrow'];
    for (const entry of placed) {
      entry.node.style.transform = `translate(${entry.x.toFixed(1)}px, ${entry.y.toFixed(1)}px)`;
      const cw = entry.callout.offsetWidth;
      const ch = entry.callout.offsetHeight;
      let best = null;
      for (const side of new Set([preferred[entry.code] ?? 'right', 'right', 'left', 'bottom', 'top'])) {
        const wantX = side === 'right' ? entry.x + 16 : side === 'left' ? entry.x - 16 - cw : entry.x - cw / 2;
        const wantY = side === 'top' ? entry.y - 16 - ch : side === 'bottom' ? entry.y + 16 : entry.y - ch / 2;
        const box = { x: clamp(wantX, frameLeft + 10, frameLeft + frameWidth - 10 - cw), y: clamp(wantY, top + 6, top + tall - 6 - ch), w: cw, h: ch };
        const upright = side === 'top' || side === 'bottom';
        let cost = Math.abs(box.x - wantX) * (upright ? 0.1 : 4) + Math.abs(box.y - wantY) * (upright ? 4 : 0.1);
        for (const other of taken) cost += overlap(box, other);
        for (const other of placed) cost += overlap(box, { x: other.x - 9, y: other.y - 9, w: 18, h: 18 }) * 4;
        if (!best || cost < best.cost) best = { cost, box };
        if (cost <= 12) break;
      }
      taken.push(best.box);
      entry.callout.style.transform = `translate(${Math.round(best.box.x)}px, ${Math.round(best.box.y)}px)`;
      entry.callout.dataset.placed = '';
    }
    root.dataset.ready = '';
    if (!shown) {
      shown = true;
      if (!still.matches) {
        for (const entry of placed) entry.glow.level = 0;
        for (const line of links.values()) line.progress = 0;
        wake();
        return;
      }
    }
    draw();
  };

  const sync = pressed => {
    const fresh = pressed !== null;
    for (const entry of entries) {
      const on = entry.input ? entry.input.checked : entry.on;
      const was = entry.on;
      entry.on = on;
      entry.callout.dataset.selected = String(on);
      entry.node.dataset.selected = String(on);
      entry.glow.target = on ? 1 : 0;
      entry.glow.wanted = on ? 7 : 4.5;
      if (!fresh || on === was) continue;
      if (on) wave(entry.code, 62, 820, 1);
      else wave(entry.code, 9, 300, 0.5);
    }
    order = order.filter(code => byCode.get(code).on);
    for (const entry of entries) if (entry.on && !order.includes(entry.code)) order.push(entry.code);
    if (orderInput) orderInput.value = order.join(' ');
    const wanted = new Map(ring(placed.filter(entry => entry.on).map(entry => entry.code)).map(pair => [[...pair].sort().join('|'), pair]));
    for (const [key, line] of links) if (!wanted.has(key)) line.leaving = true;
    for (const key of wanted.keys()) {
      const line = links.get(key);
      if (line) {
        line.leaving = false;
        line.alpha = 1;
        continue;
      }
      const [from, to] = key.split('|');
      links.set(key, { samples: trail(from, to), flip: pressed === to, progress: fresh ? 0 : 1, alpha: 1, leaving: false });
    }
    if (still.matches || !fresh) settle();
    else wake();
  };

  const announce = text => {
    clearTimeout(notice);
    status.textContent = '';
    delete status.dataset.shown;
    if (!text) return;
    requestAnimationFrame(() => {
      status.textContent = text;
      status.dataset.shown = '';
    });
    notice = setTimeout(() => { delete status.dataset.shown; }, 4000);
  };

  const refuse = entry => {
    announce(status.dataset.message);
    wave(entry.code, 9, 300, 0.5);
    entry.callout.dataset.refused = '';
    setTimeout(() => { delete entry.callout.dataset.refused; }, 400);
  };

  const work = on => {
    if ((root.dataset.busy === 'true') === on) return;
    busy = on && !still.matches;
    if (on) root.dataset.busy = 'true';
    else delete root.dataset.busy;
    clearInterval(pulse);
    if (busy) {
      const emit = () => {
        const active = placed.filter(entry => entry.on);
        if (active.length > 0) wave(active[turn++ % active.length].code, 96, 1700, 0.85, 4.4);
      };
      emit();
      pulse = setInterval(emit, 1150);
      wake();
    } else if (on) {
      settle();
    }
  };

  for (const entry of entries) {
    if (!entry.input) continue;
    entry.input.addEventListener('click', event => {
      if (!status || entry.input.type !== 'checkbox' || entry.input.checked || entries.some(other => other.input.checked)) return;
      event.preventDefault();
      refuse(entry);
    }, { signal });
    entry.input.addEventListener('change', () => {
      if (status) announce('');
      sync(entry.code);
    }, { signal });
    entry.node.addEventListener('click', () => {
      if (!root.dataset.busy) entry.input.click();
    }, { signal });
  }
  for (const type of ['pointerover', 'pointerout', 'focusin', 'focusout']) {
    root.addEventListener(type, event => {
      const part = event.target.closest?.('[data-region]');
      if (!part || (type.startsWith('pointer') && event.pointerType === 'touch')) return;
      if (event.relatedTarget && part.contains(event.relatedTarget)) return;
      const entry = byCode.get(part.dataset.region);
      const hot = type === 'pointerover' || type === 'focusin';
      entry.callout.toggleAttribute('data-hot', hot);
      entry.node.toggleAttribute('data-hot', hot);
    }, { signal });
  }
  if (form) {
    addEventListener('submit', event => { if (event.target === form && !event.defaultPrevented) work(true); }, { signal });
    addEventListener('pageshow', () => work(false), { signal });
    addEventListener('keydown', event => { if (event.key === 'Escape') work(false); }, { signal });
    form.addEventListener('reset', () => setTimeout(() => sync(null)), { signal });
  }

  sync(null);
  const resized = new ResizeObserver(layout);
  resized.observe(surface);
  resized.observe(stage);
  const repaint = () => {
    recolor();
    layout();
  };
  scheme.addEventListener('change', repaint, { signal });
  const themed = new MutationObserver(repaint);
  themed.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  document.fonts.ready.then(() => { if (!signal.aborted) layout(); });
  signal.addEventListener('abort', () => {
    resized.disconnect();
    themed.disconnect();
    cancelAnimationFrame(frame);
    clearInterval(pulse);
    clearTimeout(notice);
  });
  return live;
};

const mounted = new Map();
const scan = () => {
  for (const [root, live] of mounted) {
    if (root.isConnected) continue;
    live.abort();
    mounted.delete(root);
  }
  for (const root of document.querySelectorAll('[data-region-map]:not([data-mounted])')) {
    mounted.get(root)?.abort();
    root.dataset.mounted = '';
    mounted.set(root, mount(root));
  }
};
scan();
new MutationObserver(scan).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-mounted'] });
