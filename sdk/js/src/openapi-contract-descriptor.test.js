import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const generator = /**
 * @type {Promise<{
 *   generateOpenApiContractDescriptor(input: {
 *     manifestPath: string;
 *     specPath: string;
 *   }): Promise<unknown>;
 * }>}
 */ (
  import(
    pathToFileURL(path.join(root, "scripts", "openapi-contract-descriptor.js"))
      .href
  )
);
test("descriptor pins operation identity, parameter contract, and complete JSON shapes", async () => {
  const directory = await mkdtemp(
    path.join(tmpdir(), "terrascale-openapi-descriptor-"),
  );
  try {
    const manifestPath = path.join(directory, "manifest.json");
    const specPath = path.join(directory, "openapi.json");
    await writeFile(
      manifestPath,
      JSON.stringify({
        routes: [
          {
            base: "api",
            client: "widgets",
            method: "POST",
            operation: "create",
            pathTemplate: "/api/v1/widgets/{widgetId}",
            routeName: "widgets.create",
            requestType: "CreateWidget",
            responseType: "Widget",
            sourceContract: {
              body: { kind: "json", shape: { label: "Ada" } },
              headers: [{ name: "content-type", value: "application/json" }],
              query: [],
            },
          },
        ],
      }),
    );
    const descriptor = await descriptorFor(specPath, {
      operationId: "CreateWidget",
      parameterName: "widgetId",
      required: true,
      type: "string",
      nullable: true,
      fields: { label: { type: "string", nullable: true } },
    });
    for (const mutation of [
      { operationId: "CreateWidgetV2" },
      { parameterName: "id" },
      { required: false },
      { type: "integer" },
      { nullable: false },
      {
        fields: {
          label: { type: "string", nullable: true },
          count: { type: "integer" },
        },
      },
    ])
      assert.notDeepEqual(
        await descriptorFor(specPath, {
          operationId: "CreateWidget",
          parameterName: "widgetId",
          required: true,
          type: "string",
          nullable: true,
          fields: { label: { type: "string", nullable: true } },
          ...mutation,
        }),
        descriptor,
      );
    await writeFile(
      specPath,
      JSON.stringify(
        spec({
          operationId: "CreateWidget",
          parameterName: "widgetId",
          required: true,
          type: "string",
          nullable: true,
          fields: { label: { type: "string", nullable: true } },
        }),
      ),
    );
    assert.deepEqual(
      await (
        await generator
      ).generateOpenApiContractDescriptor({ manifestPath, specPath }),
      descriptor,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("descriptor binds the exact OpenAPI and route-manifest bytes", async () => {
  const directory = await mkdtemp(
    path.join(tmpdir(), "terrascale-openapi-descriptor-digest-"),
  );
  try {
    const manifestPath = path.join(directory, "manifest.json");
    const specPath = path.join(directory, "openapi.json");
    const manifest = {
      routes: [
        {
          base: "api",
          client: "widgets",
          method: "POST",
          operation: "create",
          pathTemplate: "/api/v1/widgets/{widgetId}",
          routeName: "widgets.create",
          requestType: "CreateWidget",
          responseType: "Widget",
          sourceContract: {
            body: { kind: "json", shape: { label: "Ada" } },
            headers: [{ name: "content-type", value: "application/json" }],
            query: [],
          },
        },
      ],
    };
    const openApi = spec({
      operationId: "CreateWidget",
      parameterName: "widgetId",
      required: true,
      type: "string",
      nullable: false,
      fields: { label: { type: "string" } },
    });
    await writeFile(manifestPath, JSON.stringify(manifest));
    await writeFile(specPath, JSON.stringify(openApi));

    const first = /** @type {ContractDescriptor} */ (
      await (
        await generator
      ).generateOpenApiContractDescriptor({
        manifestPath,
        specPath,
      })
    );
    assert.equal(first.descriptorVersion, 2);
    assert.equal(first.sources.openApiSha256, await fileSha256(specPath));
    assert.equal(
      first.sources.routeManifestSha256,
      await fileSha256(manifestPath),
    );

    await writeFile(specPath, `${JSON.stringify(openApi, null, 2)}\n`);
    const reformattedSpec = /** @type {ContractDescriptor} */ (
      await (
        await generator
      ).generateOpenApiContractDescriptor({
        manifestPath,
        specPath,
      })
    );
    assert.notEqual(
      reformattedSpec.sources.openApiSha256,
      first.sources.openApiSha256,
    );
    assert.equal(
      reformattedSpec.sources.routeManifestSha256,
      first.sources.routeManifestSha256,
    );

    await writeFile(manifestPath, `${JSON.stringify(manifest)}\n`);
    const reformattedManifest = /** @type {ContractDescriptor} */ (
      await (
        await generator
      ).generateOpenApiContractDescriptor({
        manifestPath,
        specPath,
      })
    );
    assert.notEqual(
      reformattedManifest.sources.routeManifestSha256,
      first.sources.routeManifestSha256,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("native descriptor retains exact recursive references, media, security and error bodies", async () => {
  const directory = await mkdtemp(
    path.join(tmpdir(), "terrascale-native-descriptor-"),
  );
  try {
    const manifestPath = path.join(directory, "manifest.json");
    const specPath = path.join(directory, "openapi.json");
    const route = {
      base: "native",
      client: "TerraBaseClient",
      method: "GET",
      pathTemplate: "/v1/items/{item}",
      operation: "read",
      routeName: "database.read",
      responseType: "PointResponse",
    };
    const spec = {
      paths: {
        "/v1/items/{item}": {
          get: {
            operationId: "read",
            security: [{ native_key: [] }],
            responses: {
              "200": {
                content: {
                  "application/json": {
                    schema: { $ref: "#/components/schemas/Node" },
                  },
                },
              },
              "403": {
                content: {
                  "application/json": {
                    schema: { $ref: "#/components/schemas/Error" },
                  },
                },
              },
            },
          },
        },
      },
      components: {
        schemas: {
          Node: {
            type: "object",
            properties: { next: { $ref: "#/components/schemas/Node" } },
          },
          Error: {
            type: "object",
            properties: { code: { type: "string", enum: ["forbidden_scope"] } },
          },
        },
      },
    };
    await writeFile(manifestPath, JSON.stringify({ routes: [route] }));
    await writeFile(specPath, JSON.stringify(spec));
    const descriptor = await (
      await generator
    ).generateOpenApiContractDescriptor({ manifestPath, specPath });
    assert.match(JSON.stringify(descriptor), /forbidden_scope/u);
    assert.match(JSON.stringify(descriptor), /native_key/u);
    assert.match(JSON.stringify(descriptor), /application\/json/u);
    assert.match(JSON.stringify(descriptor), /#\/components\/schemas\/Node/u);
    await writeFile(
      manifestPath,
      JSON.stringify({
        routes: [{ ...route, pathTemplate: "/v1/items/{different}" }],
      }),
    );
    await assert.rejects(
      (await generator).generateOpenApiContractDescriptor({
        manifestPath,
        specPath,
      }),
      /no OpenAPI operation/u,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

/**
 * @param {Input} input
 */
function spec(input) {
  return {
    openapi: "3.1.0",
    paths: {
      "/api/v1/widgets/{widgetId}": {
        post: {
          operationId: input.operationId,
          parameters: [
            {
              in: "path",
              name: input.parameterName,
              required: input.required,
              schema: { type: input.type, minLength: 3 },
            },
          ],
          requestBody: {
            required: true,
            content: {
              "application/json": {
                schema: {
                  type: "object",
                  properties: input.fields,
                  required: ["label"],
                },
              },
            },
          },
          responses: {
            200: {
              content: {
                "application/json": {
                  schema: {
                    type: "object",
                    nullable: input.nullable,
                    properties: input.fields,
                    required: ["label"],
                  },
                },
              },
            },
          },
        },
      },
    },
  };
}

/**
 * @typedef {{
 *   readonly operationId: string;
 *   readonly parameterName: string;
 *   readonly required: boolean;
 *   readonly type: string;
 *   readonly nullable: boolean;
 *   readonly fields: Record<string, unknown>;
 * }} Input
 */
/**
 * @typedef {{
 *   readonly descriptorVersion: number;
 *   readonly sources: { readonly openApiSha256: string; readonly routeManifestSha256: string };
 * }} ContractDescriptor
 */
/**
 * @param {string} specPath
 * @param {Input} input
 * @returns {Promise<unknown>}
 */
async function descriptorFor(specPath, input) {
  await writeFile(specPath, JSON.stringify(spec(input)));
  return (await generator).generateOpenApiContractDescriptor({
    manifestPath: path.join(path.dirname(specPath), "manifest.json"),
    specPath,
  });
}
/**
 * @param {string} file
 * @returns {Promise<string>}
 */
async function fileSha256(file) {
  return createHash("sha256")
    .update(await readFile(file))
    .digest("hex");
}
