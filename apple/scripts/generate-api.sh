#!/usr/bin/env bash
# Generates the Swift API client from openapi/spindle.json, which the API's build writes.
# Run after changing the API, like `npm run generate:api` for the web app.
set -euo pipefail

apple="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "$apple/.." && pwd)"
target="$apple/SpindleKit/Sources/SpindleAPI"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# .NET writes a nullable reference as `oneOf: [{type: null}, {$ref: X}]`. The Swift generator can't read the null
# half and leaves out the whole property, so the generator gets `{$ref: X}` instead, and the property is no longer
# required: it is optional in Swift, read from `null` or from nothing, and sent as absent when nil.
python3 - "$repo/openapi/spindle.json" "$work/spindle.json" <<'PY'
import json, sys

def nullable(node):
    choices = node.get("oneOf") if isinstance(node, dict) else None
    return isinstance(choices, list) and len(choices) == 2 and {"type": "null"} in choices

def simplify(node):
    if isinstance(node, dict):
        properties = node.get("properties")
        if isinstance(properties, dict) and isinstance(node.get("required"), list):
            node = {**node, "required": [name for name in node["required"] if not nullable(properties.get(name))]}
        choices = node.get("oneOf")
        if isinstance(choices, list) and len(choices) == 2 and {"type": "null"} in choices:
            rest = {key: value for key, value in node.items() if key != "oneOf"}
            node = {**next(choice for choice in choices if choice != {"type": "null"}), **rest}
        return {key: simplify(value) for key, value in node.items()}
    if isinstance(node, list):
        return [simplify(item) for item in node]
    return node

with open(sys.argv[1]) as source:
    document = json.load(source)
with open(sys.argv[2], "w") as target:
    json.dump(simplify(document), target, indent=2)
PY

swift run --package-path "$apple/Tools" --quiet swift-openapi-generator generate \
    "$work/spindle.json" \
    --config "$target/openapi-generator-config.yaml" \
    --output-directory "$target/Generated"
