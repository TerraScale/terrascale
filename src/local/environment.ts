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

const FORBIDDEN_STAGE_PATTERN = /(?:^|[-_.])(?:prod(?:uction)?|stag(?:e|ing)|preview|deploy(?:ed|ment)?|qualif(?:ication|ying|y)|ci)(?:$|[-_.])/iu;
const FALSE_INDICATOR_VALUES = new Set(["0", "false", "no", "off"]);

export function assertLocalSqliteEnvironment(
  environment: Readonly<Record<string, string | undefined>> = process.env,
): void {
  for (const name of localSqlitePresenceIndicators) {
    const value = environment[name]?.trim();
    if (value !== undefined && value.length > 0 && !FALSE_INDICATOR_VALUES.has(value.toLowerCase())) {
      throw new TypeError(`SQLite local development mode is disabled when ${name} is set.`);
    }
  }

  for (const name of localSqliteStageIndicators) {
    const value = environment[name]?.trim();
    if (value !== undefined && FORBIDDEN_STAGE_PATTERN.test(value)) {
      throw new TypeError(`SQLite local development mode is disabled for ${name}=${value}.`);
    }
  }
}
