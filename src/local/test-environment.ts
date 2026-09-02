import { afterAll } from "vitest";

import {
  localSqlitePresenceIndicators,
  localSqliteStageIndicators,
} from "./environment.js";

const indicatorNames = [
  ...localSqlitePresenceIndicators,
  ...localSqliteStageIndicators,
] as const;

export function installLocalSqliteTestEnvironment(): void {
  const previous = new Map<string, string | undefined>(
    indicatorNames.map((name) => [name, process.env[name]]),
  );
  for (const name of indicatorNames) {
    delete process.env[name];
  }
  process.env.NODE_ENV = "test";
  afterAll(() => {
    for (const [name, value] of previous) {
      if (value === undefined) {
        delete process.env[name];
      } else {
        process.env[name] = value;
      }
    }
  });
}

export function localSqliteTestChildEnvironment(): NodeJS.ProcessEnv {
  return {
    ...(process.env.PATH === undefined ? {} : { PATH: process.env.PATH }),
    ...(process.env.SystemRoot === undefined ? {} : { SystemRoot: process.env.SystemRoot }),
    ...(process.env.TMPDIR === undefined ? {} : { TMPDIR: process.env.TMPDIR }),
    ...(process.env.TMP === undefined ? {} : { TMP: process.env.TMP }),
    ...(process.env.TEMP === undefined ? {} : { TEMP: process.env.TEMP }),
    NODE_ENV: "test",
  };
}
