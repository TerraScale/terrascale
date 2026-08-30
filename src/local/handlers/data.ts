import {
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalJsonObject,
  type LocalRequestContext,
} from "../context.js";
import type { TerraScaleSqliteStore } from "../store.js";

type Store = Pick<TerraScaleSqliteStore, "get" | "list" | "put" | "delete">;
type Entity = LocalJsonObject & { readonly id: string; readonly projectId: string };
type DocumentRecord = {
  readonly id: string;
  readonly repositoryId: string;
  readonly projectId: string;
  readonly data: unknown;
  readonly lastModified: string;
  readonly changeToken: string;
};
type SchemaRecord = {
  readonly repositoryId: string;
  readonly projectId: string;
  readonly enabled: boolean;
  readonly schema: string;
};

const defaultProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const keyPrefix = "sdk-local/data/";

/**
 * Implements the public data routes for the SDK's development transport.
 * The handler deliberately uses only the persistence methods exposed by the
 * local store. This keeps the route independent from the concrete SQLite
 * driver and makes all indexes survive a process restart.
 */
export async function handleLocalData(context: LocalRequestContext): Promise<Response | undefined> {
  const projectId = context.request.headers.get("x-terrascale-project-id")?.trim() || defaultProjectId;

  const databaseList = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases") as Readonly<Record<string, string>> | undefined;
  if (databaseList !== undefined) {
    return jsonResponse(await listDatabases(context, databaseList.projectId!));
  }
  const databaseCreate = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/databases") as Readonly<Record<string, string>> | undefined;
  if (databaseCreate !== undefined) {
    return jsonResponse(stripInternal(await createDatabase(context, databaseCreate.projectId!)), 201);
  }
  const databaseGet = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases/{databaseId}") as Readonly<Record<string, string>> | undefined;
  if (databaseGet !== undefined) {
    return databaseResponse(await readDatabase(context, databaseGet.projectId!, databaseGet.databaseId!));
  }
  const databaseDelete = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}/databases/{databaseId}") as Readonly<Record<string, string>> | undefined;
  if (databaseDelete !== undefined) {
    return deleteDatabase(context, databaseDelete.projectId!, databaseDelete.databaseId!);
  }

  const environmentList = matchLocalRoute(
    context,
    "GET",
    "/api/v1/projects/{projectId}/databases/{databaseId}/environments",
  ) as Readonly<Record<string, string>> | undefined;
  if (environmentList !== undefined) {
    return jsonResponse(await listEnvironments(context, environmentList.projectId!, environmentList.databaseId!));
  }
  const environmentCreate = matchLocalRoute(
    context,
    "POST",
    "/api/v1/projects/{projectId}/databases/{databaseId}/environments",
  ) as Readonly<Record<string, string>> | undefined;
  if (environmentCreate !== undefined) {
    const value = await createEnvironment(context, environmentCreate.projectId!, environmentCreate.databaseId!);
    if (value.id.length === 0) return problemResponse(404, "database_not_found", "Database was not found.");
    return jsonResponse(
      stripInternal(value),
      201,
    );
  }
  const environmentGet = matchLocalRoute(
    context,
    "GET",
    "/api/v1/projects/{projectId}/databases/{databaseId}/environments/{environmentId}",
  ) as Readonly<Record<string, string>> | undefined;
  if (environmentGet !== undefined) {
    return entityResponse(
      await readEnvironment(
        context,
        environmentGet.projectId!,
        environmentGet.databaseId!,
        environmentGet.environmentId!,
      ),
    );
  }
  const environmentDelete = matchLocalRoute(
    context,
    "DELETE",
    "/api/v1/projects/{projectId}/databases/{databaseId}/environments/{environmentId}",
  ) as Readonly<Record<string, string>> | undefined;
  if (environmentDelete !== undefined) {
    return deleteEnvironment(
      context,
      environmentDelete.projectId!,
      environmentDelete.databaseId!,
      environmentDelete.environmentId!,
    );
  }

  const capability = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/database-branches/capability") as Readonly<Record<string, string>> | undefined;
  if (capability !== undefined) {
    return jsonResponse({
      projectId: capability.projectId!,
      admissionState: "unavailable",
      physicalCloneAvailable: false,
      promotionAvailable: false,
      mergeAvailable: false,
      detail: "Physical database branching is not available.",
    });
  }
  const clone = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/database-branches") as Readonly<Record<string, string>> | undefined;
  if (clone !== undefined) {
    return jsonResponse(
      { code: "database_branch_unavailable", message: "Physical database branching is unavailable." },
      503,
    );
  }

  const repositoryList = matchLocalRoute(context, "GET", "/api/v1/repositories") as Readonly<Record<string, string>> | undefined;
  if (repositoryList !== undefined) return jsonResponse(await listRepositories(context, projectId));
  const repositoryCreate = matchLocalRoute(context, "POST", "/api/v1/repositories") as Readonly<Record<string, string>> | undefined;
  if (repositoryCreate !== undefined) return jsonResponse(await createRepository(context, projectId), 201);
  const repositoryGet = matchLocalRoute(context, "GET", "/api/v1/repositories/{repositoryId}") as Readonly<Record<string, string>> | undefined;
  if (repositoryGet !== undefined) return entityResponse(await readRepository(context, projectId, repositoryGet.repositoryId!));
  const repositoryDelete = matchLocalRoute(context, "DELETE", "/api/v1/repositories/{repositoryId}") as Readonly<Record<string, string>> | undefined;
  if (repositoryDelete !== undefined) return deleteRepository(context, projectId, repositoryDelete.repositoryId!);

  const documentsList = matchLocalRoute(context, "GET", "/api/v1/repositories/{repositoryId}/documents") as Readonly<Record<string, string>> | undefined;
  if (documentsList !== undefined) return listDocuments(context, projectId, documentsList.repositoryId!);
  const documentsBatch = matchLocalRoute(context, "POST", "/api/v1/repositories/{repositoryId}/documents/batch") as Readonly<Record<string, string>> | undefined;
  if (documentsBatch !== undefined) return batchDocuments(context, projectId, documentsBatch.repositoryId!);
  const documentsCreate = matchLocalRoute(context, "POST", "/api/v1/repositories/{repositoryId}/documents") as Readonly<Record<string, string>> | undefined;
  if (documentsCreate !== undefined) return mutateDocument(context, projectId, documentsCreate.repositoryId!, undefined, "create");
  const documentsGet = matchLocalRoute(context, "GET", "/api/v1/repositories/{repositoryId}/documents/{documentId}") as Readonly<Record<string, string>> | undefined;
  if (documentsGet !== undefined) return getDocument(context, projectId, documentsGet.repositoryId!, documentsGet.documentId!);
  const documentsPut = matchLocalRoute(context, "PUT", "/api/v1/repositories/{repositoryId}/documents/{documentId}") as Readonly<Record<string, string>> | undefined;
  if (documentsPut !== undefined) return mutateDocument(context, projectId, documentsPut.repositoryId!, documentsPut.documentId!, "put");
  const documentsPatch = matchLocalRoute(context, "PATCH", "/api/v1/repositories/{repositoryId}/documents/{documentId}") as Readonly<Record<string, string>> | undefined;
  if (documentsPatch !== undefined) return mutateDocument(context, projectId, documentsPatch.repositoryId!, documentsPatch.documentId!, "patch");
  const documentsDelete = matchLocalRoute(context, "DELETE", "/api/v1/repositories/{repositoryId}/documents/{documentId}") as Readonly<Record<string, string>> | undefined;
  if (documentsDelete !== undefined) return removeDocument(context, projectId, documentsDelete.repositoryId!, documentsDelete.documentId!);

  const query = matchLocalRoute(context, "POST", "/api/v1/repositories/{repositoryId}/tql") as Readonly<Record<string, string>> | undefined;
  if (query !== undefined) return executeQuery(context, projectId, query.repositoryId!);
  const schemaGet = matchLocalRoute(context, "GET", "/api/v1/repositories/{repositoryId}/schema") as Readonly<Record<string, string>> | undefined;
  if (schemaGet !== undefined) return getSchema(context, projectId, schemaGet.repositoryId!);
  const schemaValidate = matchLocalRoute(context, "POST", "/api/v1/repositories/{repositoryId}/schema/validate") as Readonly<Record<string, string>> | undefined;
  if (schemaValidate !== undefined) return validateSchema(context, projectId, schemaValidate.repositoryId!);
  const schemaConfigure = matchLocalRoute(context, "PUT", "/api/v1/repositories/{repositoryId}/schema") as Readonly<Record<string, string>> | undefined;
  if (schemaConfigure !== undefined) return configureSchema(context, projectId, schemaConfigure.repositoryId!);

  return undefined;
}

async function listDatabases(context: LocalRequestContext, projectId: string): Promise<readonly Entity[]> {
  const ids = await read<string[]>(context.store, `${keyPrefix}database-index/${projectId}`) ?? [];
  const values = await Promise.all(ids.map((id) => read<Entity>(context.store, `${keyPrefix}database/${projectId}/${id}`)));
  return paginate(values.filter((value): value is Entity => value !== undefined).map(stripInternal), context);
}

async function createDatabase(context: LocalRequestContext, projectId: string): Promise<Entity> {
  const body = objectBody(context);
  const name = typeof body.name === "string" && body.name.trim() ? body.name.trim() : "Database";
  const id = createUlid();
  const value: Entity = {
    databaseId: id, projectId, id, name, regions: ["GRU"], backupRegion: "GRU",
    createdAt: context.now, version: 1,
  };
  await putEntity(context.store, `${keyPrefix}database/${projectId}/${id}`, value, `${keyPrefix}database-index/${projectId}`);
  return value;
}

async function readDatabase(context: LocalRequestContext, projectId: string, databaseId: string): Promise<Entity | undefined> {
  return read<Entity>(context.store, `${keyPrefix}database/${projectId}/${databaseId}`);
}

function databaseResponse(value: Entity | undefined): Response {
  return value === undefined ? problemResponse(404, "database_not_found", "Database was not found.") : jsonResponse(stripInternal(value));
}

function entityResponse(value: Entity | undefined): Response {
  return value === undefined ? problemResponse(404, "resource_not_found", "The requested resource was not found.") : jsonResponse(stripInternal(value));
}

async function deleteDatabase(context: LocalRequestContext, projectId: string, databaseId: string): Promise<Response> {
  const value = await readDatabase(context, projectId, databaseId);
  if (value === undefined) return problemResponse(404, "database_not_found", "Database was not found.");
  if (context.request.headers.get("x-terrascale-delete-confirmation") !== databaseId) {
    return problemResponse(400, "delete_confirmation_required", "Database deletion requires confirmation.");
  }
  const environments = await listEnvironmentValues(context, projectId, databaseId);
  const repositories = await listRepositoryValues(context, projectId);
  const deletedRepositories = repositories.filter((item) => item.databaseId === databaseId);
  await remove(context.store, `${keyPrefix}database/${projectId}/${databaseId}`, `${keyPrefix}database-index/${projectId}`, databaseId);
  for (const environment of environments) await remove(context.store, `${keyPrefix}environment/${projectId}/${environment.id}`, `${keyPrefix}environment-index/${projectId}/${databaseId}`, environment.id);
  for (const repository of deletedRepositories) {
    for (const document of await listDocumentValues(context, projectId, repository.id)) {
      await remove(context.store, documentKey(projectId, repository.id, document.id));
    }
    await remove(context.store, `${keyPrefix}document-index/${projectId}/${repository.id}`);
    await removeRepository(context, projectId, repository.id);
  }
  return jsonResponse({ databaseId, projectId, name: value.name, deleted: true, environmentsDeleted: environments.length, physicalDatabasesDeleted: 1, repositoriesDeleted: deletedRepositories.length });
}

async function listEnvironmentValues(context: LocalRequestContext, projectId: string, databaseId: string): Promise<Entity[]> {
  const ids = await read<string[]>(context.store, `${keyPrefix}environment-index/${projectId}/${databaseId}`) ?? [];
  const values = await Promise.all(ids.map((id) => read<Entity>(context.store, `${keyPrefix}environment/${projectId}/${id}`)));
  return values.filter((item): item is Entity => item !== undefined);
}

async function listEnvironments(context: LocalRequestContext, projectId: string, databaseId: string): Promise<readonly Entity[]> {
  return paginate((await listEnvironmentValues(context, projectId, databaseId)).map(stripInternal), context);
}

async function createEnvironment(context: LocalRequestContext, projectId: string, databaseId: string): Promise<Entity> {
  const database = await readDatabase(context, projectId, databaseId);
  if (database === undefined) return { id: "", projectId, error: "database_not_found" };
  const body = objectBody(context);
  const id = createUlid();
  const value: Entity = { environmentId: id, id, projectId, databaseId, name: typeof body.name === "string" ? body.name.trim() : "Environment", kind: body.kind === "Dev" || body.kind === "Preview" || body.kind === "Prod" ? body.kind : "Custom", status: "Active", createdAt: context.now, version: 1 };
  await putEntity(context.store, `${keyPrefix}environment/${projectId}/${id}`, value, `${keyPrefix}environment-index/${projectId}/${databaseId}`);
  return value;
}

async function readEnvironment(context: LocalRequestContext, projectId: string, databaseId: string, environmentId: string): Promise<Entity | undefined> {
  const value = await read<Entity>(context.store, `${keyPrefix}environment/${projectId}/${environmentId}`);
  return value?.databaseId === databaseId ? value : undefined;
}

async function deleteEnvironment(context: LocalRequestContext, projectId: string, databaseId: string, environmentId: string): Promise<Response> {
  const value = await readEnvironment(context, projectId, databaseId, environmentId);
  if (value === undefined) return problemResponse(404, "environment_not_found", "Environment was not found.");
  const repositories = (await listRepositoryValues(context, projectId)).filter((item) => item.environmentId === environmentId);
  for (const repository of repositories) {
    for (const document of await listDocumentValues(context, projectId, repository.id)) {
      await remove(context.store, documentKey(projectId, repository.id, document.id));
    }
    await remove(context.store, `${keyPrefix}document-index/${projectId}/${repository.id}`);
    await removeRepository(context, projectId, repository.id);
  }
  await remove(context.store, `${keyPrefix}environment/${projectId}/${environmentId}`, `${keyPrefix}environment-index/${projectId}/${databaseId}`, environmentId);
  return jsonResponse({ environmentId, projectId, databaseId, name: value.name, deleted: true, physicalDatabaseDeleted: true, repositoriesDeleted: repositories.length, status: "Completed" });
}

async function listRepositoryValues(context: LocalRequestContext, projectId: string): Promise<Entity[]> {
  const ids = await read<string[]>(context.store, `${keyPrefix}repository-index/${projectId}`) ?? [];
  const values = await Promise.all(ids.map((id) => read<Entity>(context.store, `${keyPrefix}repository/${projectId}/${id}`)));
  return values.filter((item): item is Entity => item !== undefined);
}

async function listRepositories(context: LocalRequestContext, projectId: string): Promise<readonly Entity[]> {
  return (await listRepositoryValues(context, projectId)).map(stripInternal);
}

async function createRepository(context: LocalRequestContext, projectId: string): Promise<Entity> {
  const body = objectBody(context);
  const id = createUlid();
  const value: Entity = { repositoryId: id, id, projectId, name: typeof body.name === "string" ? body.name.trim() : "Repository", databaseId: typeof body.databaseId === "string" ? body.databaseId : createUlid(), environmentId: typeof body.environmentId === "string" ? body.environmentId : null, collection: typeof body.collection === "string" && body.collection.trim() ? body.collection.trim() : "Documents", schemaValidationEnabled: body.schemaValidationEnabled === true, createdAt: context.now, updatedAt: context.now };
  await putEntity(context.store, `${keyPrefix}repository/${projectId}/${id}`, value, `${keyPrefix}repository-index/${projectId}`);
  if (typeof body.schema === "string") await writeSchema(context.store, { repositoryId: id, projectId, enabled: value.schemaValidationEnabled === true, schema: body.schema });
  return stripInternal(value);
}

async function readRepository(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Entity | undefined> {
  return read<Entity>(context.store, `${keyPrefix}repository/${projectId}/${repositoryId}`);
}

async function deleteRepository(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Response> {
  const value = await read<Entity>(context.store, `${keyPrefix}repository/${projectId}/${repositoryId}`);
  if (value === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const docs = await listDocumentValues(context, projectId, repositoryId);
  // The public API removes a repository as a unit. `deleteDocuments=false`
  // is accepted for compatibility, but cannot leave records indexed under a
  // repository that no longer exists (which would make them inaccessible).
  for (const document of docs) await remove(context.store, documentKey(projectId, repositoryId, document.id));
  await remove(context.store, `${keyPrefix}document-index/${projectId}/${repositoryId}`);
  await removeRepository(context, projectId, repositoryId);
  return jsonResponse({ repositoryId, name: value.name, deleted: true, documentsDeleted: docs.length });
}

async function removeRepository(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<void> {
  await remove(context.store, `${keyPrefix}repository/${projectId}/${repositoryId}`, `${keyPrefix}repository-index/${projectId}`, repositoryId);
  await remove(context.store, `${keyPrefix}schema/${projectId}/${repositoryId}`);
}

async function listDocumentValues(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<DocumentRecord[]> {
  const ids = await read<string[]>(context.store, `${keyPrefix}document-index/${projectId}/${repositoryId}`) ?? [];
  const values = await Promise.all(ids.map((id) => read<DocumentRecord>(context.store, documentKey(projectId, repositoryId, id))));
  return values.filter((item): item is DocumentRecord => item !== undefined);
}

async function listDocuments(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId);
  if (repository === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const documents = await listDocumentValues(context, projectId, repositoryId);
  const values = documents.map((document) => documentResponse(repository, document, context.now));
  const skip = nonnegativeInt(context.url.searchParams.get("skip"));
  const take = boundedTake(context.url.searchParams.get("take"));
  return jsonResponse({ repositoryId, repository: repository.name, skip, take, total: values.length, items: values.slice(skip, skip + take) });
}

async function getDocument(context: LocalRequestContext, projectId: string, repositoryId: string, documentId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId);
  const document = await read<DocumentRecord>(context.store, documentKey(projectId, repositoryId, documentId));
  return repository === undefined || document === undefined ? problemResponse(404, "document_not_found", "Document was not found.") : jsonResponse(documentResponse(repository, document, context.now));
}

async function mutateDocument(context: LocalRequestContext, projectId: string, repositoryId: string, documentId: string | undefined, operation: "create" | "put" | "patch"): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId);
  if (repository === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const body = objectBody(context);
  if (!("data" in body)) return problemResponse(400, "data_required", "Document data is required.");
  const id = documentId ?? createUlid();
  const current = await read<DocumentRecord>(context.store, documentKey(projectId, repositoryId, id));
  if (operation === "create" && current !== undefined) return problemResponse(409, "document_exists", "Document already exists.");
  const conflict = checkMatch(context, current);
  if (conflict !== undefined) return conflict;
  const data = operation === "patch" && isObject(current?.data) && isObject(body.data) ? { ...current.data, ...body.data } : body.data;
  const value: DocumentRecord = { id, repositoryId, projectId, data, lastModified: context.now, changeToken: changeToken(context) };
  await putEntity(context.store, documentKey(projectId, repositoryId, id), value, `${keyPrefix}document-index/${projectId}/${repositoryId}`);
  return jsonResponse(documentResponse(repository, value, context.now));
}

async function removeDocument(context: LocalRequestContext, projectId: string, repositoryId: string, documentId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId);
  const key = documentKey(projectId, repositoryId, documentId);
  const current = await read<DocumentRecord>(context.store, key);
  if (repository === undefined || current === undefined) return problemResponse(404, "document_not_found", "Document was not found.");
  const conflict = checkMatch(context, current);
  if (conflict !== undefined) return conflict;
  await remove(context.store, key, `${keyPrefix}document-index/${projectId}/${repositoryId}`, documentId);
  return jsonResponse({ repositoryId, repository: repository.name, id: documentId, deleted: true, sessionConsistencyToken: changeToken(context) });
}

async function batchDocuments(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId);
  if (repository === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const body = objectBody(context);
  const operations = Array.isArray(body.operations) ? body.operations : [];
  const results: LocalJsonObject[] = [];
  for (let index = 0; index < operations.length; index++) {
    const operation = isObject(operations[index]) ? operations[index] : {};
    const kind = typeof operation.operation === "string" ? operation.operation : "upsert";
    const id = typeof operation.documentId === "string" ? operation.documentId : createUlid();
    let response: Response;
    if (kind === "delete") response = await removeDocument(withBody(context, {}), projectId, repositoryId, id);
    else response = await mutateDocument(withBody(context, { data: operation.data }), projectId, repositoryId, id, kind === "patch" ? "patch" : kind === "create" ? "create" : "put");
    const payload = await response.json().catch(() => ({})) as LocalJsonObject;
    const item: LocalJsonObject = { index, operation: kind, id };
    if (response.ok) {
      if (kind === "delete") item.deleted = payload;
      else item.document = payload;
    } else item.error = payload;
    results.push(item);
  }
  return jsonResponse({ repositoryId, repository: repository.name, results });
}

async function executeQuery(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId);
  if (repository === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const body = objectBody(context);
  const query = typeof body.query === "string" ? body.query : "";
  let documents = await listDocumentValues(context, projectId, repositoryId);
  const where = query.match(/\bwhere\s+([A-Za-z_$][\w$.-]*)\s*(=|!=|contains)\s*(?:'([^']*)'|"([^"]*)"|([^\s]+))/iu);
  if (where !== null) {
    const field = where[1] ?? ""; const expected = where[3] ?? where[4] ?? where[5] ?? ""; const operator = (where[2] ?? "=").toLowerCase();
    documents = documents.filter((document) => { const actual = fieldValue(document.data, field); return operator === "contains" ? String(actual ?? "").includes(expected) : operator === "!=" ? String(actual) !== expected : String(actual) === expected || String(actual) === String(expected === "true"); });
  }
  const order = query.match(/\border\s+by\s+([A-Za-z_$][\w$.-]*)(?:\s+(asc|desc))?/iu);
  if (order !== null) { const field = order[1] ?? ""; const direction = (order[2] ?? "asc").toLowerCase() === "desc" ? -1 : 1; documents.sort((a, b) => String(fieldValue(a.data, field)).localeCompare(String(fieldValue(b.data, field))) * direction); }
  const select = query.match(/\bselect\s+(.+?)\s+from\b/iu);
  const selected = select === null || (select[1] ?? "").trim() === "*" ? documents : documents.map((item) => ({ ...item, data: selectFields(item.data, (select[1] ?? "").split(",").map((field) => field.trim())) }));
  const skip = nonnegativeInt(typeof body.skip === "number" ? String(body.skip) : context.url.searchParams.get("skip")); const take = boundedTake(typeof body.take === "number" ? String(body.take) : context.url.searchParams.get("take"));
  return jsonResponse({ repositoryId, repository: repository.name, skip, take, total: selected.length, items: selected.slice(skip, skip + take).map((item) => documentResponse(repository, item, context.now)) });
}

async function getSchema(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId); if (repository === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const value = await read<SchemaRecord>(context.store, `${keyPrefix}schema/${projectId}/${repositoryId}`) ?? { repositoryId, projectId, enabled: repository.schemaValidationEnabled === true, schema: "" };
  return jsonResponse({ repositoryId, repository: repository.name, databaseId: repository.databaseId, environmentId: repository.environmentId ?? null, collection: repository.collection, enabled: value.enabled, schema: value.schema });
}

async function configureSchema(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId); if (repository === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const body = objectBody(context); const previous = await read<SchemaRecord>(context.store, `${keyPrefix}schema/${projectId}/${repositoryId}`);
  const value: SchemaRecord = { repositoryId, projectId, enabled: typeof body.enabled === "boolean" ? body.enabled : previous?.enabled ?? false, schema: typeof body.schema === "string" ? body.schema : previous?.schema ?? "" };
  if (value.schema !== "") { try { JSON.parse(value.schema); } catch { return problemResponse(400, "invalid_schema", "Schema must be valid JSON."); } }
  await writeSchema(context.store, value); await putEntity(context.store, `${keyPrefix}repository/${projectId}/${repositoryId}`, { ...repository, schemaValidationEnabled: value.enabled, updatedAt: context.now });
  return getSchema(context, projectId, repositoryId);
}

async function validateSchema(context: LocalRequestContext, projectId: string, repositoryId: string): Promise<Response> {
  const repository = await readRepository(context, projectId, repositoryId); if (repository === undefined) return problemResponse(404, "repository_not_found", "Repository was not found.");
  const body = objectBody(context);
  const value = await read<SchemaRecord>(context.store, `${keyPrefix}schema/${projectId}/${repositoryId}`); const max = boundedTake(String(body.maxDocumentsToValidate ?? 1000)); const maxErrors = boundedTake(String(body.maxErrorMessages ?? 100));
  if (value?.enabled && value.schema) { try { const schema = JSON.parse(value.schema) as unknown; for (const document of (await listDocumentValues(context, projectId, repositoryId)).slice(0, max)) { const issues = validateValue(document.data, schema).slice(0, maxErrors); if (issues.length > 0) return jsonResponse({ repositoryId, repository: repository.name, databaseId: repository.databaseId, environmentId: repository.environmentId ?? null, collection: repository.collection, operationId: createUlid(), errors: issues }); } } catch { return problemResponse(400, "invalid_schema", "Schema must be valid JSON."); } }
  return jsonResponse({ repositoryId, repository: repository.name, databaseId: repository.databaseId, environmentId: repository.environmentId ?? null, collection: repository.collection, operationId: createUlid() });
}

function documentResponse(repository: Entity, document: DocumentRecord, session: string): LocalJsonObject { return { repositoryId: repository.repositoryId, repository: repository.name, id: document.id, data: document.data, lastModified: document.lastModified, changeToken: document.changeToken, sessionConsistencyToken: session }; }
function changeToken(context: LocalRequestContext): string { return `${context.now}:${createUlid()}`; }
function stripInternal(value: Entity): Entity { const { id: _id, ...rest } = value; return rest as Entity; }
function checkMatch(context: LocalRequestContext, current: DocumentRecord | undefined): Response | undefined { const expected = context.request.headers.get("if-match"); if (expected !== null && (current === undefined || expected !== current.changeToken)) return problemResponse(412, "change_token_mismatch", "The document change token does not match.", { expectedChangeToken: expected, currentChangeToken: current?.changeToken }); return undefined; }
function withBody(context: LocalRequestContext, body: unknown): LocalRequestContext { return { ...context, body }; }
function documentKey(projectId: string, repositoryId: string, documentId: string): string { return `${keyPrefix}document/${projectId}/${repositoryId}/${documentId}`; }
function paginate<T>(items: readonly T[], context: LocalRequestContext): readonly T[] { const skip = nonnegativeInt(context.url.searchParams.get("skip")); const take = boundedTake(context.url.searchParams.get("take")); return items.slice(skip, skip + take); }
function nonnegativeInt(value: string | null): number { const parsed = Number(value ?? 0); return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : 0; }
function boundedTake(value: string | null): number { const parsed = Number(value ?? 100); return Number.isSafeInteger(parsed) && parsed > 0 ? Math.min(parsed, 1000) : 100; }
function isObject(value: unknown): value is LocalJsonObject { return value !== null && typeof value === "object" && !Array.isArray(value); }
function fieldValue(value: unknown, field: string): unknown { return field.split(".").reduce<unknown>((current, segment) => isObject(current) ? current[segment] : undefined, value); }
function selectFields(value: unknown, fields: readonly string[]): LocalJsonObject { return Object.fromEntries(fields.map((field) => [field, fieldValue(value, field)])); }
function validateValue(value: unknown, schema: unknown, path = "$"): string[] { if (!isObject(schema)) return []; const errors: string[] = []; const type = schema.type; if (typeof type === "string" && ((type === "object" && !isObject(value)) || (type === "array" && !Array.isArray(value)) || (type === "string" && typeof value !== "string") || (type === "number" && typeof value !== "number") || (type === "boolean" && typeof value !== "boolean") || (type === "null" && value !== null))) errors.push(`${path} must be ${type}`); const required = Array.isArray(schema.required) ? schema.required : []; for (const key of required) if (typeof key === "string" && (!isObject(value) || !(key in value))) errors.push(`${path}.${key} is required`); const properties = isObject(schema.properties) ? schema.properties : {}; if (isObject(value)) for (const [key, child] of Object.entries(properties)) if (key in value) errors.push(...validateValue(value[key], child, `${path}.${key}`)); return errors; }

async function read<T>(store: Store, key: string): Promise<T | undefined> {
  const parts = storageParts(key);
  return store.get<T>(parts[0], parts[1], parts[2]);
}
async function write<T>(store: Store, key: string, value: T): Promise<void> {
  const parts = storageParts(key);
  store.put(parts[0], parts[1], parts[2], value);
}
async function remove(store: Store, key: string, indexKey?: string, id?: string): Promise<void> {
  const parts = storageParts(key);
  store.delete(parts[0], parts[1], parts[2]);
  if (indexKey !== undefined && id !== undefined) {
    const ids = await read<string[]>(store, indexKey) ?? [];
    await write(store, indexKey, ids.filter((item) => item !== id));
  }
}
async function putEntity(store: Store, key: string, value: Entity, indexKey?: string): Promise<void> { await write(store, key, value); if (indexKey !== undefined) { const id = value.id; const ids = await read<string[]>(store, indexKey) ?? []; if (!ids.includes(id)) await write(store, indexKey, [...ids, id]); } }
async function writeSchema(store: Store, value: SchemaRecord): Promise<void> { await write(store, `${keyPrefix}schema/${value.projectId}/${value.repositoryId}`, value); }
function storageParts(key: string): [string, string, string] {
  const parts = key.slice(keyPrefix.length).split("/");
  if (parts[0]?.endsWith("-index")) return [`sdk-local-${parts[0]}`, parts[1] ?? "global", "index"];
  if (parts[0] === "document") return ["sdk-local-document", `${parts[1] ?? "global"}/${parts[2] ?? "global"}`, parts[3] ?? "unknown"];
  return [`sdk-local-${parts[0] ?? "record"}`, parts[1] ?? "global", parts[2] ?? "record"];
}
function createUlid(): string { const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; const bytes = new Uint8Array(16); globalThis.crypto?.getRandomValues(bytes); let value = BigInt(Date.now()) << 80n; for (let index = 0; index < 10; index++) value |= BigInt(bytes[index] ?? Math.floor(Math.random() * 256)) << BigInt((9 - index) * 8); let output = ""; for (let index = 25; index >= 0; index--) { output += alphabet[Number(value >> BigInt(index * 5) & 31n)] ?? "0"; } return output; }
