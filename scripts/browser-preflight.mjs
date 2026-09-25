#!/usr/bin/env node

// Exercise real Chromium navigations in the CI container before the full suite.
// The builder's image check uses setContent, which does not test navigation.
import assert from "node:assert/strict";
import { readFileSync, statfsSync } from "node:fs";
import { createServer } from "node:http";
import { chromium } from "playwright";

const watchdog = setTimeout(() => {
  console.error("Browser preflight timed out after 45 seconds");
  process.exit(124);
}, 45_000);

function readLimit(path) {
  try {
    return readFileSync(path, "utf8").trim();
  } catch {
    return "unavailable";
  }
}

function closeServer(server) {
  return new Promise((resolve, reject) => {
    server.close((error) => (error ? reject(error) : resolve()));
  });
}

const server = createServer((_request, response) => {
  response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  response.end("<!doctype html><title>Browser preflight</title><h1>loopback-ok</h1>");
});
let browser;

try {
  const sharedMemory = statfsSync("/dev/shm");
  console.log(`Node: ${process.version}; Chromium: ${chromium.executablePath()}`);
  console.log(`/dev/shm bytes: ${sharedMemory.bsize * sharedMemory.blocks}`);
  console.log(`cgroup memory.max: ${readLimit("/sys/fs/cgroup/memory.max")}`);
  console.log(`cgroup pids.max: ${readLimit("/sys/fs/cgroup/pids.max")}`);

  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  assert(address && typeof address !== "string");

  browser = await chromium.launch({ timeout: 15_000 });
  const page = await browser.newPage();
  page.setDefaultNavigationTimeout(15_000);
  await page.goto("data:text/html,%3Ch1%3Edata-ok%3C%2Fh1%3E");
  assert.equal(await page.locator("h1").textContent(), "data-ok");
  await page.goto(`http://127.0.0.1:${address.port}/`);
  assert.equal(await page.locator("h1").textContent(), "loopback-ok");
  console.log("Browser preflight passed: data URL and loopback HTTP navigations");
} catch (error) {
  console.error("Browser preflight failed:", error);
  process.exitCode = 1;
} finally {
  if (browser) await browser.close().catch((error) => {
    console.error("Browser close failed:", error);
    process.exitCode = 1;
  });
  if (server.listening) await closeServer(server).catch((error) => {
    console.error("Server close failed:", error);
    process.exitCode = 1;
  });
  clearTimeout(watchdog);
}
