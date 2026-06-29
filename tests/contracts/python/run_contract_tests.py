from __future__ import annotations

import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


def main() -> int:
    repo_root = Path(__file__).resolve().parents[3]
    contracts_root = repo_root / "contracts" / "v1"
    fixtures_root = contracts_root / "fixtures"

    manifest = json.loads((fixtures_root / "manifest.json").read_text(encoding="utf-8"))
    failures: list[str] = []

    for case in manifest["cases"]:
        schema = json.loads((contracts_root / case["schema"]).read_text(encoding="utf-8"))
        instance = json.loads((fixtures_root / case["fixture"]).read_text(encoding="utf-8"))

        validator = Draft202012Validator(schema, format_checker=FormatChecker())
        errors = sorted(validator.iter_errors(instance), key=lambda error: list(error.absolute_path))
        actual_valid = not errors

        if actual_valid != case["expected_valid"]:
            details = "; ".join(error.message for error in errors) or "fixture unexpectedly validated"
            failures.append(f'{case["name"]}: {details}')
            print(f'FAIL {case["name"]}: {details}')
        else:
            print(f'PASS {case["name"]}')

    if failures:
        print(f"\n{len(failures)} contract fixture case(s) failed.")
        return 1

    print(f'\nAll {len(manifest["cases"])} contract fixture cases passed.')
    return 0


if __name__ == "__main__":
    sys.exit(main())
