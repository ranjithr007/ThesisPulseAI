# Shared Contract Validation

The fixture manifest under `contracts/v1/fixtures/manifest.json` is the single source of truth for cross-runtime schema tests.

Both runners load the same schemas and payloads and must produce the same expected result.

## Python

```powershell
cd tests/contracts/python
python -m pip install -r requirements.txt
python run_contract_tests.py
```

## .NET

```powershell
cd tests/contracts/dotnet
dotnet restore
dotnet run
```

## Fixture rules

- Valid fixtures must satisfy JSON Schema including format validation.
- Invalid fixtures must fail for the intended structural reason.
- Each fixture is listed once in the shared manifest.
- New contract schemas require at least one valid and one invalid case.
- Semantic rules that cannot be represented in JSON Schema will be added to the same runners through named semantic validators.
- A contract change is incomplete until both runners agree on every case.

## Current coverage

- signal minimum payload;
- rejection of unknown signal fields;
- valid place-order command;
- rejection of a place command without quantity;
- valid fill with broker fill identity;
- rejection of a fill without broker identity or fallback fingerprint;
- accepted baseline risk policy;
- rejection of an unknown risk-policy field.

## Dependency baseline

- Python `jsonschema[format]` is pinned in `requirements.txt`.
- .NET uses the pinned `JsonSchema.Net` package in the console runner project.
- Dependency upgrades require both runners to pass the shared suite before merge.
