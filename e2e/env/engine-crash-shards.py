import ast
import collections
import json
import pathlib

root = pathlib.Path(__file__).parent
source = ast.parse((root / "engine-crash.py").read_text())
cases = [
    ast.literal_eval(decorator.args[0])
    for function in source.body
    if isinstance(function, ast.FunctionDef)
    for decorator in function.decorator_list
    if isinstance(decorator, ast.Call) and isinstance(decorator.func, ast.Name) and decorator.func.id == "case"
]

def unique_shards(pairs):
    assert len(pairs) == len({name for name, _ in pairs}), "duplicate crash shard names"
    return dict(pairs)


shards = json.loads((root / "engine-crash-shards.json").read_text(), object_pairs_hook=unique_shards)
assigned = [case for group in shards.values() for case in group]
assert cases and shards and all(shards.values()), "empty crash cases or shards"
assert len(cases) == len(set(cases)), "duplicate registered crash cases"
assert collections.Counter(assigned) == collections.Counter(cases), (
    "crash shards must assign every registered case exactly once: "
    f"missing={list((collections.Counter(cases) - collections.Counter(assigned)).elements())}, "
    f"extra={list((collections.Counter(assigned) - collections.Counter(cases)).elements())}"
)
print(json.dumps({"include": [{"shard": name, "cases": ",".join(group)} for name, group in shards.items()]}))
