import { relative } from 'node:path';

export default class QuarantineDiscovery {
  onBegin(config, suite) {
    const inventory = suite.allTests().map(test => {
      let serial;
      for (let parent = test.parent; parent; parent = parent.parent) {
        if (typeof parent._parallelMode !== 'string') throw new Error('Playwright discovery did not expose suite execution mode');
        if (parent._parallelMode === 'serial') serial = JSON.stringify(parent.titlePath());
      }
      return { spec: relative(config.rootDir, test.location.file), titles: test.titlePath().slice(3), tags: test.tags, project: test.parent.project().name, serial };
    });
    process.stdout.write(JSON.stringify(inventory));
  }

  onError(error) {
    process.stderr.write(error.message + '\n');
    process.exitCode = 1;
  }
}
