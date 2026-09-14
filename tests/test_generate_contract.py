import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
from contracts import ContractError
from generate_contract import OpenApiGenerator


class GeneratorSchemaTests(unittest.TestCase):
    def generator(self, schemas):
        return OpenApiGenerator({"components": {"schemas": schemas}})

    def test_object_all_of_flattens_reference_and_inline_fields(self):
        api = self.generator({
            "Base": {"type": "object", "properties": {"id": {"type": "string"}}, "required": ["id"]},
            "Combined": {"allOf": [
                {"$ref": "#/components/schemas/Base"},
                {"type": "object", "properties": {"secret": {"type": "string"}}, "required": ["secret"]},
            ]},
        })
        schema = api.model_schema("Combined")
        self.assertEqual(["id", "secret"], list(schema["properties"]))
        self.assertEqual(["id", "secret"], schema["required"])

    def test_object_all_of_rejects_cycles_and_conflicts(self):
        cycle = self.generator({"Loop": {"allOf": [{"$ref": "#/components/schemas/Loop"}]}})
        with self.assertRaisesRegex(ContractError, "cyclic object allOf"):
            cycle.model_schema("Loop")
        conflict = self.generator({"Combined": {"allOf": [
            {"type": "object", "properties": {"id": {"type": "string"}}},
            {"type": "object", "properties": {"id": {"type": "integer"}}},
        ]}})
        with self.assertRaisesRegex(ContractError, "duplicate allOf property 'id'"):
            conflict.model_schema("Combined")

    def test_csharp_member_collisions_keep_wire_names(self):
        api = self.generator({"Increment": {"type": "object", "properties": {
            "increment": {"type": "number"},
            "increment_": {"type": "number"},
        }}})
        output = api.models()
        self.assertIn('JsonPropertyName("increment"), JsonIgnore', output)
        self.assertIn('double? IncrementValue', output)
        self.assertIn('JsonPropertyName("increment_"), JsonIgnore', output)
        self.assertIn('double? IncrementValueValue', output)

    def test_inline_types_use_wire_names_after_member_allocation(self):
        api = self.generator({"Parent": {"type": "object", "properties": {
            "parent": {"type": "object", "properties": {"first": {"type": "string"}}},
            "parent_value": {"type": "object", "properties": {"second": {"type": "string"}}},
        }}})
        api.collect_inline()
        output = api.models()
        self.assertIn("public sealed record ParentParent(", output)
        self.assertIn("public sealed record ParentParentValue(", output)
        self.assertIn("ParentParent? ParentValue", output)
        self.assertIn("ParentParentValue? ParentValueValue", output)

    def test_json_context_composes_resolved_array_response_metadata(self):
        api = OpenApiGenerator({
            "components": {"schemas": {
                "ImportJob": {"type": "object"},
                "ImportJobs": {"type": "array", "items": {"$ref": "#/components/schemas/ImportJob"}},
            }},
            "paths": {
                "/imports": {"get": {"operationId": "list_imports", "responses": {
                    "200": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/ImportJobs"}}}}
                }}}
            },
        })
        output = api.json_context()
        self.assertIn("[JsonSerializable(typeof(IReadOnlyList<ImportJob>))]", output)

    def test_json_context_composes_scalar_array_response_metadata(self):
        api = OpenApiGenerator({
            "paths": {
                "/regions": {"get": {"operationId": "list_regions", "responses": {
                    "200": {"content": {"application/json": {"schema": {"type": "array", "items": {"type": "string"}}}}}
                }}}
            },
        })
        output = api.json_context()
        self.assertIn("[JsonSerializable(typeof(IReadOnlyList<string>))]", output)

    def test_json_context_does_not_register_messagepack_response_shapes(self):
        api = OpenApiGenerator({
            "components": {"schemas": {
                "ReactiveEnvelope": {
                    "type": "array",
                    "prefixItems": [{"type": "integer"}],
                },
            }},
            "paths": {
                "/reactive": {"get": {"operationId": "view_log", "responses": {
                    "200": {"content": {"application/msgpack": {
                        "schema": {"$ref": "#/components/schemas/ReactiveEnvelope"}
                    }}}
                }}}
            },
        })
        output = api.json_context()
        self.assertNotIn("ReactiveEnvelope", output)
        self.assertNotIn("IReadOnlyList", output)

    def test_nullable_reference_union_keeps_generated_type(self):
        api = self.generator({
            "PageSort": {
                "type": "object",
                "properties": {"field": {"type": "string"}},
            },
            "PageQuery": {
                "type": "object",
                "properties": {
                    "sortBy": {
                        "oneOf": [
                            {"type": "null"},
                            {"$ref": "#/components/schemas/PageSort"},
                        ]
                    }
                },
            },
        })
        output = api.models()
        self.assertIn("PageSort? SortBy", output)
        self.assertNotIn("JsonElement? SortBy", output)

    def test_string_enums_are_generated_with_exact_wire_names(self):
        api = self.generator({
            "PageSortDirection": {
                "type": "string",
                "enum": ["asc", "desc"],
            },
            "PageSort": {
                "type": "object",
                "properties": {
                    "direction": {"$ref": "#/components/schemas/PageSortDirection"}
                },
            },
        })
        output = api.models()
        self.assertIn("public enum PageSortDirection", output)
        self.assertIn('[JsonStringEnumMemberName("asc")]\n    Asc,', output)
        self.assertIn("PageSortDirection? Direction", output)
        self.assertIn("[JsonSerializable(typeof(PageSortDirection))]", api.json_context())


if __name__ == "__main__":
    unittest.main()
