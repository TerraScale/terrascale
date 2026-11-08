/* A sparkline: a small trend line without axes, stretched to its box.
   It is decorative; the row it sits in states the numbers. */

import { html, type Html } from "../../html";
import { coordinate, niceCeiling, runs, smoothPath } from "./geometry";

const WIDTH = 120;
const HEIGHT = 28;
const PAD = 3;

export function sparkline(values: readonly (number | null)[]): Html {
  const known = values.filter((value): value is number => value !== null);
  if (known.length < 2) return html``;
  const scale = niceCeiling(Math.max(...known));
  const xAt = (index: number) => (WIDTH * index) / Math.max(1, values.length - 1);
  const yAt = (value: number) => PAD + (HEIGHT - 2 * PAD) * (1 - value / scale);
  const segments = runs(values);
  // Relative cubic coordinates preserve the curve while avoiding repeated
  // long absolute coordinates in every miniature chart.
  const compact = (path: string) => {
    let x = 0;
    let y = 0;
    const number = (value: number) => String(Number(value.toFixed(1)));
    return [...path.matchAll(/([MLC]) ([^MLC]+)/g)]
      .map(([, command, coordinates]) => {
        const points = coordinates!
          .trim()
          .split(" ")
          .map((point) => point.split(",").map(Number));
        const next = points
          .map(([px = 0, py = 0]) =>
            command === "M" ? `${number(px)},${number(py)}` : `${number(px - x)},${number(py - y)}`,
          )
          .join(" ");
        [x = 0, y = 0] = points.at(-1)!;
        return `${command === "M" ? "M" : command!.toLowerCase()}${next}`;
      })
      .join("");
  };
  const paths = segments.map(
    (run) => html`<path d="${compact(smoothPath(run.map(({ index, value }) => [xAt(index), yAt(value)])))}"></path>`,
  );
  const last = segments.at(-1)?.at(-1);
  return html`<svg class="st-spark" viewBox="0 0 ${WIDTH} ${HEIGHT}" preserveAspectRatio="none" aria-hidden="true">${paths}${
    last ? html`<circle cx="${coordinate(xAt(last.index))}" cy="${coordinate(yAt(last.value))}" r="2"></circle>` : ""
  }</svg>`;
}
