const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
import { readFile } from 'node:fs/promises';
import './draw.mjs';
const here=path.dirname(fileURLToPath(import.meta.url));
const browser=await chromium.launch({headless:true,...(process.env.CHROME_BIN?{executablePath:process.env.CHROME_BIN}:{})});
try {
  for (const count of [3,10,20]) {
    const source=path.join(here,`architecture-${count}-regions.svg`);
    const svg=await readFile(source,'utf8');
    const height=Number(svg.match(/height="(\d+)"/)?.[1]);
    const page=await browser.newPage({viewport:{width:1600,height},deviceScaleFactor:1});
    await page.setContent('<!doctype html><html><body style="margin:0">'+svg+'</body></html>');
    await page.screenshot({path:path.join(here,`architecture-${count}-regions.png`),fullPage:true,timeout:60000});
    await page.close();
  }
} finally {await browser.close();}
