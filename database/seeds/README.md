# ThesisPulse AI Deterministic Seeds

These scripts provide a reviewed, repeat-safe LocalDB and PAPER baseline after migrations V0001–V0009 are applied.

## Safety boundary

The seed pack creates only:

- NSE reference identity;
- a **DRAFT** 2026 calendar shell and regular-session metadata;
- three non-tradable research index instruments;
- an active RESEARCH benchmark universe;
- a local `SIMULATOR` provider identity;
- the accepted active PAPER risk policy;
- a **DRAFT** PAPER order-transition policy with zero rules.

It deliberately excludes live/shadow accounts, live portfolios, credentials, broker instrument tokens, an active holiday calendar, active transition rules, and automatic broker execution authority.

Existing seed-owned rows are validated. Material drift raises an SQL error instead of silently updating reviewed values.

## Authoritative LocalDB command

Run from the repository root so SQLCMD `:r` include paths resolve correctly:

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\seeds\S0000__apply_local_paper_seed_pack.sql"
```

The master script establishes every SQL Server session option required for filtered-index DML, includes S0001–S0005 in manifest order, and runs reference verification in the same connection.

Run the same command a second time. Repeat execution must succeed without duplicates or mutation.

## Expected verification

```text
verification_status  seed_domain  context_instruments  universe_members  draft_calendar
PASS                 REFERENCE    3                    3                 1
```

The PAPER risk policy and DRAFT transition-policy scripts validate their own immutable identities, checksums, assignments, and rule counts inside their transactions.

## Manifest

`database/seeds/seed-manifest.json` records the reviewed order and activation scope. Application startup must not execute this manifest automatically.

## Promotion boundary

Before activating an exchange calendar or order-transition rules, add a new reviewed seed version. Do not edit an accepted seed in place after promotion beyond local development.
