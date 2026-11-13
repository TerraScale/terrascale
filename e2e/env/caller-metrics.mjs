import { expect } from '@playwright/test';

const readings = new Map();
export async function readMetrics(url) {
  const previous = readings.get(url);
  let body;
  await expect.poll(async () => {
    body = await (await fetch(url)).text();
    return body !== previous;
  }, { message: 'TerraBase metrics newer than the last reading', intervals: [50] }).toBe(true);
  readings.set(url, body);
  return body;
}
