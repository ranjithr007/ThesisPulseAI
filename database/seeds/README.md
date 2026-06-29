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

It deliberately excludes:

- live or shadow accounts;
- live portfolios;
- broker credentials and access tokens;
- broker instrument tokens/mappings;
- an active exchange holiday calendar;
- active order-transition rules;
- automatic broker execution authority.

Existing seed-owned rows are validated. Material drift raises an SQL error instead of silently updating reviewed values.

## Execution order

Run from the repository root:

```powershell
cd "D:\00 Projects\ThesisPulseAI"
```

### 1. NSE exchange and draft calendar

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I `
  -i ".\database\seeds\reference\S0001__seed_nse_exchange_and_draft_calendar.sql"
```

### 2. Research index context

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I `
  -i ".\database\seeds\reference\S0002__seed_index_context.sql"
```

### 3. Local simulator identity

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I `
  -i ".\database\seeds\reference\S0003__seed_simulator_broker.sql"
```

### 4. Accepted PAPER risk policy

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I `
  -i ".\database\seeds\policies\S0004__seed_paper_risk_policy.sql"
```

### 5. Draft transition-policy shell

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I `
  -i ".\database\seeds\policies\S0005__seed_paper_order_transition_policy.sql"
```

Run all five scripts a second time. Repeat execution must succeed without duplicates or mutation.

## Reference verification

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I `
  -i ".\database\verification\S0001__verify_reference_seeds.sql"
```

Expected:

```text
verification_status  seed_domain  context_instruments  universe_members  draft_calendar
PASS                 REFERENCE    3                    3                 1
```

The policy scripts contain their own immutable identity, checksum, assignment, rule-count, and drift validation inside the transaction.

## Manifest

`database/seeds/seed-manifest.json` records the approved order and activation scope. It is documentation for this batch; it is not automatically executed by application startup.

## Promotion boundary

Before activating exchange calendars or order-transition rules, use a new reviewed seed version. Never edit an already accepted seed in place after it has been promoted beyond local development.
