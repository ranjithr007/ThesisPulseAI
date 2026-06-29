SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
BEGIN TRANSACTION;

IF OBJECT_ID(N'[risk].[risk_policies]', N'U') IS NULL
   OR OBJECT_ID(N'[risk].[risk_policy_mandatory_rules]', N'U') IS NULL
   OR OBJECT_ID(N'[risk].[risk_policy_status_events]', N'U') IS NULL
   OR OBJECT_ID(N'[risk].[active_policy_assignments]', N'U') IS NULL
    THROW 61401, 'V0006 risk-policy tables are required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0004';
DECLARE @reviewer nvarchar(200) = N'ThesisPulse Architecture Review';
DECLARE @seed_time datetime2(7) = '2026-06-29T00:00:00';
DECLARE @policy_uid uniqueidentifier = '101efa60-f03b-5eda-9c1e-fb70dfca638e';
DECLARE @event_uid uniqueidentifier = 'ee4f1612-cd55-5b89-8c9a-a77e3b4c40d7';
DECLARE @correlation_id uniqueidentifier = '4807e31f-c884-5001-b721-113d7097b4cc';
DECLARE @checksum varchar(256) = 'b977951374cd907416631464e9ce95654f471a9997393cc5d51361760ce9ec40';
DECLARE @metadata nvarchar(max) = N'{"activation_scope":"PAPER_ONLY","live_use_prohibited":true,"seed":"S0004"}';
DECLARE @raw nvarchar(max) = N'{"approved_at_utc":"2026-06-29T00:00:00Z","approved_by":"ThesisPulse Architecture Review","checksum":"b977951374cd907416631464e9ce95654f471a9997393cc5d51361760ce9ec40","consecutive_loss_response":{"minimum_cooling_off_minutes":0,"operating_mode":"PAUSED","requires_health_check":true,"requires_operator_approval":true,"trigger_outcome_attribution":true},"contract_version":"1.0.0","created_at_utc":"2026-06-29T00:00:00Z","created_by":"ThesisPulse Architecture Review","effective_from_utc":"2026-01-01T00:00:00Z","effective_to_utc":null,"environment":"PAPER","hard_limit_response":{"allow_risk_reducing_exits":true,"operating_mode":"CLOSE_ONLY","requires_operator_approval_to_reset":true,"requires_reconciliation_before_reset":true},"limits":{"consecutive_loss_pause_count":3,"daily_hard_loss_fraction":0.015,"daily_soft_loss_fraction":0.01,"maximum_portfolio_drawdown_fraction":0.08,"maximum_risk_per_trade_fraction":0.005,"maximum_strategy_drawdown_fraction":0.06,"maximum_total_open_risk_fraction":0.01,"maximum_trades_per_symbol_per_session":2,"standard_risk_per_trade_fraction":0.0025,"weekly_loss_fraction":0.03},"mandatory_rules":["FRESH_MARKET_DATA_REQUIRED","MANDATORY_STOP_LOSS","NO_AI_EXECUTION_AUTHORITY","RISK_REDUCING_EXITS_PRESERVED","UNKNOWN_BROKER_OUTCOME_RECONCILIATION"],"metadata":{"activation_scope":"PAPER_ONLY","live_use_prohibited":true,"seed":"S0004"},"parent_policy_id":null,"risk_policy_id":"101efa60-f03b-5eda-9c1e-fb70dfca638e","risk_policy_version":"risk-policy-1.0.0","scope":{"scope_id":"PAPER","scope_type":"GLOBAL"},"soft_limit_response":{"maximum_concurrent_new_positions":1,"operating_mode":"RESTRICTED","requires_operator_approval":false,"risk_multiplier":0.5},"status":"ACTIVE"}';

IF NOT EXISTS (SELECT 1 FROM [risk].[risk_policies] WHERE [risk_policy_uid] = @policy_uid)
BEGIN
    INSERT INTO [risk].[risk_policies]
    (
        [risk_policy_uid], [contract_version], [risk_policy_version], [initial_status],
        [environment], [parent_policy_uid], [scope_type], [scope_id],
        [effective_from_utc], [effective_to_utc],
        [standard_risk_per_trade_fraction], [maximum_risk_per_trade_fraction],
        [maximum_total_open_risk_fraction], [daily_soft_loss_fraction],
        [daily_hard_loss_fraction], [weekly_loss_fraction],
        [maximum_strategy_drawdown_fraction], [maximum_portfolio_drawdown_fraction],
        [consecutive_loss_pause_count], [maximum_trades_per_symbol_per_session],
        [maximum_sector_exposure_fraction], [maximum_correlated_exposure_fraction],
        [maximum_margin_utilization_fraction], [maximum_single_instrument_notional_fraction],
        [maximum_gross_exposure_fraction], [maximum_net_exposure_fraction],
        [soft_operating_mode], [soft_risk_multiplier], [soft_maximum_concurrent_new_positions],
        [soft_requires_operator_approval], [hard_operating_mode],
        [hard_allow_risk_reducing_exits], [hard_requires_reconciliation_before_reset],
        [hard_requires_operator_approval_to_reset], [consecutive_loss_operating_mode],
        [consecutive_loss_trigger_outcome_attribution],
        [consecutive_loss_minimum_cooling_off_minutes],
        [consecutive_loss_requires_health_check], [consecutive_loss_requires_operator_approval],
        [created_at_utc], [created_by], [approved_at_utc], [approved_by],
        [checksum], [metadata_json], [raw_contract_json], [created_record_at_utc], [created_record_by]
    )
    VALUES
    (
        @policy_uid, '1.0.0', 'risk-policy-1.0.0', 'ACTIVE',
        'PAPER', NULL, 'GLOBAL', 'PAPER', '2026-01-01T00:00:00', NULL,
        0.00250000, 0.00500000, 0.01000000, 0.01000000,
        0.01500000, 0.03000000, 0.06000000, 0.08000000,
        3, 2,
        NULL, NULL, NULL, NULL, NULL, NULL,
        'RESTRICTED', 0.50000000, 1, 0,
        'CLOSE_ONLY', 1, 1, 1,
        'PAUSED', 1, 0, 1, 1,
        @seed_time, @reviewer, @seed_time, @reviewer,
        @checksum, @metadata, @raw, @seed_time, @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM [risk].[risk_policies]
    WHERE [risk_policy_uid] = @policy_uid
      AND [risk_policy_version] = 'risk-policy-1.0.0'
      AND [initial_status] = 'ACTIVE'
      AND [environment] = 'PAPER'
      AND [scope_type] = 'GLOBAL'
      AND [scope_id] = 'PAPER'
      AND [checksum] = @checksum
      AND [raw_contract_json] = @raw
)
    THROW 61402, 'PAPER risk-policy seed drift detected.', 1;

DECLARE @policy_id bigint =
    (SELECT [risk_policy_id] FROM [risk].[risk_policies] WHERE [risk_policy_uid] = @policy_uid);

DECLARE @rules TABLE ([seq] int PRIMARY KEY, [code] varchar(100), [description] nvarchar(1000));
INSERT INTO @rules VALUES
(1, 'FRESH_MARKET_DATA_REQUIRED', N'New exposure requires current, valid market data.'),
(2, 'MANDATORY_STOP_LOSS', N'Every approved trade requires a protective stop.'),
(3, 'NO_AI_EXECUTION_AUTHORITY', N'Intelligence outputs cannot directly create broker instructions.'),
(4, 'RISK_REDUCING_EXITS_PRESERVED', N'Risk controls must preserve approved risk-reducing exits.'),
(5, 'UNKNOWN_BROKER_OUTCOME_RECONCILIATION', N'Unknown broker outcomes require reconciliation before retry.');

INSERT INTO [risk].[risk_policy_mandatory_rules]
([risk_policy_id], [rule_sequence], [rule_code], [rule_description], [created_at_utc], [created_by])
SELECT @policy_id, r.[seq], r.[code], r.[description], @seed_time, @actor
FROM @rules r
WHERE NOT EXISTS
(
    SELECT 1 FROM [risk].[risk_policy_mandatory_rules] t
    WHERE t.[risk_policy_id] = @policy_id AND t.[rule_sequence] = r.[seq]
);

IF EXISTS
(
    SELECT 1 FROM @rules r
    WHERE NOT EXISTS
    (
        SELECT 1 FROM [risk].[risk_policy_mandatory_rules] t
        WHERE t.[risk_policy_id] = @policy_id
          AND t.[rule_sequence] = r.[seq]
          AND t.[rule_code] = r.[code]
          AND t.[rule_description] = r.[description]
    )
)
    THROW 61403, 'PAPER risk-policy mandatory-rule drift detected.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [risk].[risk_policy_status_events]
    WHERE [risk_policy_id] = @policy_id AND [event_sequence] = 0
)
BEGIN
    INSERT INTO [risk].[risk_policy_status_events]
    (
        [risk_policy_status_event_uid], [risk_policy_id], [event_sequence], [status],
        [reason_codes_json], [occurred_at_utc], [source_service], [source_version],
        [correlation_id], [causation_id], [created_at_utc], [created_by]
    )
    VALUES
    (
        @event_uid, @policy_id, 0, 'ACTIVE', N'["ARCHITECTURE_BASELINE_APPROVED"]',
        @seed_time, 'ThesisPulse.Seed', 'S0004', @correlation_id, NULL, @seed_time, @actor
    );
END;

IF EXISTS
(
    SELECT 1 FROM [risk].[active_policy_assignments]
    WHERE [environment] = 'PAPER' AND [scope_type] = 'GLOBAL' AND [scope_id] = 'PAPER'
      AND [assignment_status] = 'ACTIVE' AND [active_to_utc] IS NULL
      AND [risk_policy_id] <> @policy_id
)
    THROW 61404, 'Another active PAPER global risk policy already exists.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [risk].[active_policy_assignments]
    WHERE [risk_policy_id] = @policy_id
      AND [environment] = 'PAPER' AND [scope_type] = 'GLOBAL' AND [scope_id] = 'PAPER'
      AND [assignment_status] = 'ACTIVE' AND [active_to_utc] IS NULL
)
BEGIN
    INSERT INTO [risk].[active_policy_assignments]
    (
        [risk_policy_id], [environment], [scope_type], [scope_id], [assignment_status],
        [active_from_utc], [active_to_utc], [assigned_at_utc], [assigned_by],
        [correlation_id], [created_at_utc]
    )
    VALUES
    (
        @policy_id, 'PAPER', 'GLOBAL', 'PAPER', 'ACTIVE',
        '2026-01-01T00:00:00', NULL, @seed_time, @reviewer,
        @correlation_id, @seed_time
    );
END;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
