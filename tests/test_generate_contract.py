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


if __name__ == "__main__":
    unittest.main()
