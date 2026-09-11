export const localSqlitePresenceIndicators = [
  "CI",
  "CONTINUOUS_INTEGRATION",
  "GITHUB_ACTIONS",
  "GITLAB_CI",
  "FORGEJO_ACTIONS",
  "GITEA_ACTIONS",
  "TF_BUILD",
  "BUILDKITE",
  "CIRCLECI",
  "JENKINS_URL",
  "TEAMCITY_VERSION",
  "VERCEL",
  "NETLIFY",
  "CF_PAGES",
  "RENDER",
  "FLY_APP_NAME",
  "K_SERVICE",
  "CLOUD_RUN_JOB",
  "KUBERNETES_SERVICE_HOST",
  "AWS_LAMBDA_FUNCTION_NAME",
  "AWS_EXECUTION_ENV",
  "ECS_CONTAINER_METADATA_URI",
  "ECS_CONTAINER_METADATA_URI_V4",
  "WEBSITE_INSTANCE_ID",
  "WEBSITE_SITE_NAME",
  "FUNCTIONS_WORKER_RUNTIME",
  "DYNO",
  "HEROKU_APP_NAME",
  "RAILWAY_ENVIRONMENT",
  "RAILWAY_SERVICE_ID",
  "NOMAD_ALLOC_ID",
  "TERRASCALE_DEPLOYMENT",
  "TERRASCALE_QUALIFICATION",
] as const;

/**
 * Explicit opt-in for a hosted development environment.
 *
 * Hosting platforms set a presence indicator on every workload, including
 * development environments, so the SDK cannot tell a hosted dev environment
 * from production by presence alone. Setting this variable to a true value
 * relaxes *platform* indicators only: CI, qualification, and every stage
 * indicator (`NODE_ENV=production`, `STAGE=staging`, …) still refuse.
 */
export const localSqliteHostedOptIn = "TERRASCALE_LOCAL_SQLITE_ALLOW_HOSTED";

/** Presence indicators the opt-in never relaxes. */
export const localSqliteUnrelaxableIndicators: ReadonlySet<string> = new Set([
  "CI",
  "CONTINUOUS_INTEGRATION",
  "GITHUB_ACTIONS",
  "GITLAB_CI",
  "FORGEJO_ACTIONS",
  "GITEA_ACTIONS",
  "TF_BUILD",
  "BUILDKITE",
  "CIRCLECI",
  "JENKINS_URL",
  "TEAMCITY_VERSION",
  "TERRASCALE_DEPLOYMENT",
  "TERRASCALE_QUALIFICATION",
]);

export const localSqliteStageIndicators = [
  "NODE_ENV",
  "APP_ENV",
  "ENVIRONMENT",
  "STAGE",
  "DEPLOY_ENV",
  "DEPLOYMENT_ENVIRONMENT",
  "TERRASCALE_ENV",
  "TERRASCALE_ENVIRONMENT",
  "VERCEL_ENV",
] as const;

const FORBIDDEN_STAGE_PATTERN =
  /(?:^|[-_.])(?:prod(?:uction)?|stag(?:e|ing)|preview|deploy(?:ed|ment)?|qualif(?:ication|ying|y)|ci)(?:$|[-_.])/iu;
const FALSE_INDICATOR_VALUES = new Set(["0", "false", "no", "off"]);
const OPT_IN_VALUES = new Set(["1", "true", "yes", "on", "enabled"]);

export function assertLocalSqliteEnvironment(
  environment: Readonly<Record<string, string | undefined>> = process.env,
): void {
  const hostedOptIn = OPT_IN_VALUES.has(
    (environment[localSqliteHostedOptIn] ?? "").trim().toLowerCase(),
  );

  for (const name of localSqlitePresenceIndicators) {
    const value = environment[name]?.trim();
    if (value === undefined || value.length === 0) continue;
    if (FALSE_INDICATOR_VALUES.has(value.toLowerCase())) continue;
    if (hostedOptIn && !localSqliteUnrelaxableIndicators.has(name)) continue;
    throw new TypeError(`SQLite local development mode is disabled when ${name} is set.`);
  }

  for (const name of localSqliteStageIndicators) {
    const value = environment[name]?.trim();
    if (value !== undefined && FORBIDDEN_STAGE_PATTERN.test(value)) {
      throw new TypeError(`SQLite local development mode is disabled for ${name}=${value}.`);
    }
  }
}
