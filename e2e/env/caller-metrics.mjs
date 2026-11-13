import { expect } from '@playwright/test';

export async function readMetrics(url) {
  const previous = await (await fetch(url)).text();
  let body;
  await expect.poll(async () => {
    body = await (await fetch(url)).text();
    return body !== previous;
  }, { message: 'TerraBase metrics newer than the last reading', intervals: [50] }).toBe(true);
  return body;
}
