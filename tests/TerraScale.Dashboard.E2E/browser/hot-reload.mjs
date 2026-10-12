import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright-core';

const base = process.argv[2];
assert(base, 'Pass the running dotnet-watch host URL');
const browser = await chromium.launch({ headless: true });
const source = new URL('../../../src/TerraScale.Dashboard/Features/Auth/AuthPage.cs', import.meta.url);
const css = new URL('../../../src/TerraScale.Dashboard/web/assets/Features/Auth/auth.css', import.meta.url);
const originalSource = await readFile(source, 'utf8');
const originalCss = await readFile(css, 'utf8');
const originalCopy = 'Access your databases and cloud operations.';
const changedCopy = `${originalCopy} Hot reload check.`;
assert(originalSource.includes(originalCopy));
const changedSource = originalSource.replace(originalCopy, changedCopy);
const changedCss = originalCss + '\n.ts-login-logo { opacity: 0.99; }\n';
let sourceChanged = false;
let cssChanged = false;
try {
  const page = await browser.newPage();
  page.setDefaultTimeout(45000);
  await page.goto(`${base}/login`);
  await page.locator('#ts-dev-updates[data-dev-ready="true"]').waitFor({ state: 'attached' });
  await writeFile(source, changedSource);
  sourceChanged = true;
  await page.getByText(changedCopy, { exact: true }).waitFor();
  assert.equal(await readFile(source, 'utf8'), changedSource, 'No concurrent edit to the source fixture');
  await writeFile(source, originalSource);
  sourceChanged = false;
  await page.getByText(originalCopy, { exact: true }).waitFor();
  await page.locator('#ts-dev-updates[data-dev-ready="true"]').waitFor({ state: 'attached' });
  const opacity = await page.locator('.ts-login-logo').evaluate(element => getComputedStyle(element).opacity);
  await writeFile(css, changedCss);
  cssChanged = true;
  await page.waitForFunction(() => getComputedStyle(document.querySelector('.ts-login-logo')).opacity === '0.99');
  assert.equal(await readFile(css, 'utf8'), changedCss, 'No concurrent edit to the CSS fixture');
  await writeFile(css, originalCss);
  cssChanged = false;
  await page.waitForFunction(value => getComputedStyle(document.querySelector('.ts-login-logo')).opacity === value, opacity);
  console.log('PASS: C# hot reload and CSS updates reached the open browser over the Tailscale origin; source fixtures restored');
} finally {
  if (sourceChanged && await readFile(source, 'utf8') === changedSource) await writeFile(source, originalSource);
  if (cssChanged && await readFile(css, 'utf8') === changedCss) await writeFile(css, originalCss);
  await browser.close();
}
