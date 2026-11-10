const svg = 'http://www.w3.org/2000/svg';
const still = matchMedia('(prefers-reduced-motion: reduce)');
const drawMs = 900;
const staggerMs = 140;
const lightCycleMs = 2800;

for (const field of document.querySelectorAll('[data-region-map]')) {
  const home = field.querySelector('input[name="homeRegion"]');
  const homeLabel = field.querySelector('[data-region-home-label]').id;
  const links = field.querySelector('[data-region-links]');
  const summary = field.querySelector('[data-region-summary]');
  const error = field.querySelector('[data-region-error]');
  const nodes = new Map([...field.querySelectorAll('button[data-region]')].map(node => [node.dataset.region, node]));
  const order = [...nodes].filter(([, node]) => node.getAttribute('aria-pressed') === 'true').map(([code]) => code);
  const drawn = new Map();
  let sequence = 0;

  const point = code => {
    const style = getComputedStyle(nodes.get(code));
    return [Number.parseFloat(style.getPropertyValue('--x')), Number.parseFloat(style.getPropertyValue('--y'))];
  };

  const arc = (from, to) => {
    const [[x1, y1], [x2, y2]] = [point(from), point(to)];
    const [cx, cy] = [...nodes.keys()].map(point).reduce(([x, y], [px, py]) => [x + px / nodes.size, y + py / nodes.size], [0, 0]);
    const length = Math.hypot(x2 - x1, y2 - y1);
    const [mx, my] = [(x1 + x2) / 2, (y1 + y2) / 2];
    let [nx, ny] = [(y1 - y2) / length, (x2 - x1) / length];
    const outward = (mx - cx) * nx + (my - cy) * ny;
    if (Math.abs(outward) > 0.5 ? outward < 0 : ny > 0) [nx, ny] = [-nx, -ny];
    const lift = length * 0.24;
    return `M${x1} ${y1}Q${(mx + nx * lift).toFixed(2)} ${(my + ny * lift).toFixed(2)} ${x2} ${y2}`;
  };

  const link = (from, to, delay) => {
    const group = document.createElementNS(svg, 'g');
    const d = arc(from, to);
    group.setAttribute('class', 'ts-db-region-link');
    group.style.setProperty('--draw-delay', `${delay}ms`);
    group.style.setProperty('--light-delay', `${delay + drawMs + (sequence++ * 1100) % lightCycleMs}ms`);
    for (const part of ['glow', 'line', 'light']) {
      const path = document.createElementNS(svg, 'path');
      for (const [name, value] of Object.entries({class: `ts-db-region-link-${part}`, d, pathLength: '1'})) path.setAttribute(name, value);
      group.append(path);
    }
    links.append(group);
    return group;
  };

  const leave = group => {
    if (still.matches || group.getClientRects().length === 0) return group.remove();
    group.querySelector('.ts-db-region-link-line').addEventListener('animationend', () => group.remove(), {once: true});
    group.setAttribute('data-leaving', '');
  };

  const render = () => {
    for (const [code, node] of nodes) {
      node.setAttribute('aria-pressed', String(order.includes(code)));
      node.toggleAttribute('data-home', order[0] === code);
      if (order[0] === code) node.setAttribute('aria-describedby', homeLabel);
      else node.removeAttribute('aria-describedby');
    }
    home.value = order[0] ?? '';
    for (const replica of field.querySelectorAll('input[name="replicaRegions"]')) replica.remove();
    home.after(...order.slice(1).map(code => Object.assign(document.createElement('input'), {type: 'hidden', name: 'replicaRegions', value: code})));
    summary.replaceChildren(...order.flatMap((code, index) => {
      const name = document.createElement(index === 0 ? 'strong' : 'span');
      name.textContent = nodes.get(code).getAttribute('aria-label');
      if (index === 0) return [name];
      const separator = document.createElement('span');
      separator.setAttribute('aria-hidden', 'true');
      separator.textContent = ' · ';
      return [separator, name];
    }));
    if (order.length > 0) error.hidden = true;
    const pairs = new Map(order.flatMap((from, index) => order.slice(index + 1).map(to => [[from, to].sort().join(' '), [from, to]])));
    for (const [key, group] of drawn) {
      if (pairs.has(key)) continue;
      drawn.delete(key);
      leave(group);
    }
    let delay = 0;
    for (const [key, [from, to]] of pairs) {
      if (drawn.has(key)) continue;
      drawn.set(key, link(from, to, delay));
      delay += staggerMs;
    }
  };

  field.addEventListener('click', event => {
    const code = event.target.closest('button[data-region]')?.dataset.region;
    if (!code) return;
    const index = order.indexOf(code);
    if (index === -1) order.push(code);
    else order.splice(index, 1);
    render();
  });

  field.closest('form').addEventListener('submit', event => {
    if (order.length > 0) return;
    event.preventDefault();
    error.hidden = false;
    nodes.values().next().value?.focus();
  });

  render();
}
