const iso = (date) => date.toISOString().slice(0, 19);

const pointFor = (track, event) => {
  const bounds = track.getBoundingClientRect();
  const ratio = Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width));
  const start = Date.parse(track.dataset.start);
  const end = Date.parse(track.dataset.end);
  return new Date(start + (end - start) * ratio);
};

const moveCursor = (track, date) => {
  const start = Date.parse(track.dataset.start);
  const end = Date.parse(track.dataset.end);
  const ratio = Math.min(1, Math.max(0, (date.getTime() - start) / (end - start)));
  const cursor = track.querySelector('.ts-backups-cursor');
  if (cursor) cursor.style.setProperty('--at', `${(ratio * 100).toFixed(2)}%`);
};

for (const track of document.querySelectorAll('.ts-backups-track')) {
  track.addEventListener('click', (event) => {
    const card = track.closest('.ts-card');
    const input = card?.querySelector('input[name="point"]');
    if (!input) return;
    const date = pointFor(track, event);
    input.value = iso(date);
    moveCursor(track, date);
    input.dispatchEvent(new Event('change', { bubbles: true }));
  });
}
