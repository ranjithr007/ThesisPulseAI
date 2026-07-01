SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[signal_fusion_lineage]', N'U') IS NULL
    THROW 71901, 'signal_fusion_lineage table is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = N'fk_signal_fusion_lineage_signal'
      AND [parent_object_id] = OBJECT_ID(N'[intelligence].[signal_fusion_lineage]')
      AND [is_disabled] = 0
      AND [is_not_trusted] = 0
)
    THROW 71902, 'Signal lineage foreign key is missing or untrusted.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[intelligence].[signal_fusion_lineage]')
      AND [name] = N'uq_signal_fusion_lineage_signal'
      AND [is_unique] = 1
)
    THROW 71903, 'One-to-one signal lineage uniqueness is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[intelligence].[signal_fusion_lineage]')
      AND [name] = N'uq_signal_fusion_lineage_evidence'
      AND [is_unique] = 1
)
    THROW 71904, 'Fusion evidence idempotency uniqueness is missing.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[signal_fusion_lineage] lineage
    INNER JOIN [intelligence].[signals] signal
        ON signal.[signal_id] = lineage.[signal_id]
    WHERE signal.[signal_uid] <> lineage.[candidate_signal_uid]
)
    THROW 71905, 'Candidate signal lineage differs from canonical signal identity.', 1;
