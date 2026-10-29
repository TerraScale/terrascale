const { TERRASCALE_PROJECT_ID: project, TERRASCALE_DATABASE: database } = process.env

export const libsqlUrl = () => `https://api.terrascale.tech/v1/projects/${project}/databases/${database}/libsql/`
