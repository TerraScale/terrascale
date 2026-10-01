export const npmReleasePolicy = Object.freeze({
  packageName: "terrascale",
  activeProducerBlockers: Object.freeze([
    "C06/C07 management/catalog/credential admission",
    "production single-writer Better Auth security persistence",
    "actual installed producer allocation and deployed account/browser journey",
  ]),
  buildTarget: "npm",
  outputRoot: "artifacts/candidate",
  qualificationSchemaVersion: 1,
  qualificationMediaType:
    "application/vnd.terrascale.npm-qualification.v1+json",
  buildReceiptSchemaVersion: 1,
  buildReceiptMediaType:
    "application/vnd.terrascale.candidate-build-receipt.v1+json",
  buildOutputDigestDomain: "terrascale.deploy/candidate-build-output/v1",
  npmTreeDigestDomain: "terrascale/npm-cli-tree/v1",
  npm: Object.freeze({
    version: "11.18.0",
    archiveUrl: "https://registry.npmjs.org/npm/-/npm-11.18.0.tgz",
    archiveIntegrity:
      "sha512-T67M4L5wNm0cZ7EBLErcEkY1SmzEW/WJ+SADBzsFUY1UdAPfFHXFQtZ6SEXiK0+vzXysCvAsepbMaBTwnrAD+w==",
    archiveSha512:
      "4faecce0be70366d1c67b1012c4adc1246354a6cc45bf589f92003073b05518d547403df1475c542d67a4845e22b4fafcd7cac0af02c7a96cc6814f09eb003fb",
  }),
  nodeVersions: Object.freeze({ 22: "v22.23.2", 24: "v24.21.0" }),
});

/**
 * @param {string} version
 * @returns {string}
 */
export function qualificationFilename(version) {
  return `terrascale-${version}.qualification.json`;
}

/**
 * @param {string} version
 * @returns {string}
 */
export function tarballFilename(version) {
  return `terrascale-${version}.tgz`;
}
