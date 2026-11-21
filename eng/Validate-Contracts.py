#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: MIT
"""Validate real CLI payloads with jsonschema 4.23.0 (a development-only dependency).

Build Release first, then run in an environment with jsonschema installed.
All created images/projects/evidence stay in a temporary directory. Reference
fixtures are read only. This script does not acquire tools, ROMs, or OS media.
"""

import copy
import json
from pathlib import Path
import subprocess
import tempfile

from jsonschema import Draft202012Validator, FormatChecker, ValidationError
from referencing import Registry, Resource


ROOT = Path(__file__).resolve().parent.parent
CLI = ROOT / "src/A2Utils.Cli/bin/Release/net10.0/a2.dll"
SCHEMAS = {}
REGISTRY = Registry()
CHECKS = 0


def invoke(*arguments, codes=(0,)):
    completed = subprocess.run(
        ["dotnet", str(CLI), *map(str, arguments), "--json"],
        cwd=ROOT, capture_output=True, text=True, timeout=120, check=False,
    )
    if completed.returncode not in codes:
        raise AssertionError(f"{arguments}: exit {completed.returncode}\n{completed.stderr}")
    return json.loads(completed.stdout)


def check(envelope):
    global CHECKS
    Draft202012Validator(
        SCHEMAS["urn:a2utils:schema:envelope:2"], registry=REGISTRY,
        format_checker=FormatChecker(),
    ).validate(envelope)
    schema_id = envelope["resultSchemaId"]
    if schema_id != "urn:a2utils:schema:result:2":
        Draft202012Validator(
            SCHEMAS[schema_id], registry=REGISTRY, format_checker=FormatChecker(),
        ).validate(envelope["data"])
    CHECKS += 1
    return envelope["data"]


def run(*arguments, codes=(0,)):
    return check(invoke(*arguments, codes=codes))


def reject(schema_id, data):
    global CHECKS
    assert not Draft202012Validator(SCHEMAS[schema_id], registry=REGISTRY, format_checker=FormatChecker()).is_valid(data)
    CHECKS += 1


def reject_envelope(envelope):
    global CHECKS
    try:
        check(envelope)
    except ValidationError:
        CHECKS += 1
        return
    raise AssertionError("Malformed envelope passed validation")


def main():
    global REGISTRY
    capabilities = invoke("capabilities")["data"]
    for name in capabilities["schemas"]:
        schema = invoke("schema", name)["data"]
        Draft202012Validator.check_schema(schema)
        SCHEMAS[schema["$id"]] = schema
    REGISTRY = Registry().with_resources(
        (schema_id, Resource.from_contents(schema)) for schema_id, schema in SCHEMAS.items()
    )
    fixture = ROOT / "tests/TestData/independent-dos33.do"
    with tempfile.TemporaryDirectory(prefix="a2-contracts-") as temporary:
        # Resolve macOS's /var alias before handing paths to transactional APIs.
        work = Path(temporary).resolve()
        info_envelope = invoke("disk", "info", fixture)
        info = check(info_envelope)
        for key, value in (("schemaVersion", 999), ("schemaId", "wrong")):
            malformed = copy.deepcopy(info_envelope)
            malformed[key] = value
            reject_envelope(malformed)
        malformed = copy.deepcopy(info_envelope)
        del malformed["command"]
        reject_envelope(malformed)
        malformed = copy.deepcopy(info)
        malformed["sizeBytes"] = "not an integer"
        reject("urn:a2utils:schema:disk-info-result:1", malformed)
        malformed = copy.deepcopy(info)
        del malformed["capabilities"]
        reject("urn:a2utils:schema:disk-info-result:1", malformed)
        run("disk", "inspect", fixture, "--track", 18, "--sector", 14)
        run("archive", "ls", fixture.parent / "independent-files.shk")
        run("archive", "extract", fixture.parent / "independent-files.shk", "DOCS:HELLO",
            "--part", "resource", "--to", work / "hello.rsrc")
        run("archive", "catalog", fixture.parent / "independent-disk.sdk")
        run("disk", "diff", fixture, fixture)
        changes = work / "changes.json"
        changes.write_text(json.dumps({"schemaVersion": 1, "changes": [
            {"action": "add", "path": "CONTRACT", "type": "BIN", "auxType": 8192, "hex": "60"}
        ]}), encoding="utf-8")
        plan = run("disk", "plan", fixture, changes)
        run("disk", "apply", fixture, changes, "--output", work / "changed.do",
            "--expect-plan-sha256", plan["planSha256"])
        destination = work / "destination.po"
        run("disk", "create", destination, "--fs", "prodos")
        migration = run("disk", "migrate", destination, "HELLO.BIN", "MIGRATED",
                        "--from", fixture, "--preflight")
        run("disk", "migrate", destination, "HELLO.BIN", "MIGRATED", "--from", fixture,
            "--output", work / "migrated.po", "--expect-plan-sha256", migration["planSha256"])
        batch = run("disk", "migrate-batch", destination, "--from", fixture, "--all", "--preflight")
        applied = run("disk", "migrate-batch", destination, "--from", fixture, "--all",
                      "--expect-plan-sha256", batch["planSha256"], "--output", work / "batch.po")
        blocked = run("disk", "migrate-batch", applied["write"]["outputPath"], "--from", fixture,
                      "--all", "--preflight", codes=(3,))
        assert not blocked["canApply"] and all(row["diagnostics"] for row in blocked["files"])
        project = work / "project"
        run("init", project, "--engine", "cpu")
        manifest = project / "project.a2.json"
        run("build", manifest, "--check")
        run("build", manifest, "--preflight")
        run("project", "resolve", manifest)
        run("build", manifest, "--test", "--artifacts", work / "build-test")
        execution = run("run", project / "execution.json", "--artifacts", work / "run")
        run("replay", execution["artifactDirectory"])
        malformed = copy.deepcopy(execution)
        malformed["registers"]["PC"] = "wrong"
        reject("urn:a2utils:schema:execution-result:1", malformed)
        run("test", project / "suite.json", "--list")
        run("test", project / "suite.json", "--artifacts", work / "suite")
        # Exercise richer stopped-debug results as well as ordinary RTS completion.
        spec = json.loads((project / "execution.json").read_text(encoding="utf-8"))
        spec["debug"] = {"breakpoints": [{"address": 8192}], "stepInstructions": 1}
        spec["memory"] = []
        debug_spec = project / "debug.json"
        debug_spec.write_text(json.dumps(spec), encoding="utf-8")
        run("run", debug_spec, "--artifacts", work / "debug")
        # A build-bound CPU case executes the BIN stored in this project's image.
        spec = json.loads((project / "execution.json").read_text(encoding="utf-8"))
        spec["routine"].update(kind="packaged", source="MAIN")
        (project / "execution.json").write_text(json.dumps(spec), encoding="utf-8")
        packaged = run("build", manifest, "--test", "--overwrite", "--artifacts", work / "packaged")
        assert packaged["passed"]
        case = packaged["tests"]["tests"][0]
        assert case["inputSha256"] == packaged["build"]["sha256"]
        checks = work / "expect.json"
        checks.write_text(json.dumps({"schemaVersion": 1, "memory": [{"address": 769, "hex": "A55A"}]}),
                          encoding="utf-8")
        run("replay", case["artifactDirectory"], "--expect", checks)
    print(f"Validated {len(SCHEMAS)} schemas and {CHECKS} output/negative contract checks.")


if __name__ == "__main__":
    main()
