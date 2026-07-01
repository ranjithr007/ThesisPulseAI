from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


def test_v0017_contains_required_option_chain_output_tables() -> None:
    migration = (
        REPOSITORY_ROOT
        / "database"
        / "migrations"
        / "V0017__create_option_chain_intelligence_output_tables.sql"
    ).read_text(encoding="utf-8")

    for object_name in (
        "option_chain_output_snapshot_inputs",
        "option_chain_output_expiries",
        "option_chain_output_walls",
        "option_chain_output_oi_flows",
        "option_chain_output_max_pain_points",
        "option_chain_output_iv_term_points",
    ):
        assert object_name in migration

    assert "'OPTION_CHAIN'" in migration
    assert "market].[option_chain_snapshots" in migration
    assert "reference].[derivative_contracts" in migration


def test_v0018_partitions_same_cutoff_outputs_by_expiry() -> None:
    migration = (
        REPOSITORY_ROOT
        / "database"
        / "migrations"
        / "V0018__partition_option_chain_outputs_by_expiry.sql"
    ).read_text(encoding="utf-8")

    assert "output_partition_key" in migration
    assert "expiryMetrics[0].expiryDate" in migration
    assert "CREATE UNIQUE INDEX [uq_engine_outputs_revision]" in migration
    assert "CREATE UNIQUE INDEX [ux_engine_outputs_current]" in migration
    assert "[output_partition_key], [revision]" in migration
    assert "[output_partition_key])" in migration


def test_option_chain_engine_seed_has_no_authority() -> None:
    seed = (
        REPOSITORY_ROOT
        / "database"
        / "seeds"
        / "intelligence"
        / "S0015__seed_option_chain_intelligence_engine.sql"
    ).read_text(encoding="utf-8")

    assert "THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE" in seed
    assert "'DIRECTIONAL_VOTER'" in seed
    assert "[can_create_signals] = 0" in seed
    assert "[can_execute_orders] = 0" in seed
