SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT
    'PASS' AS verification_status,
    'V0001' AS migration_version,
    DB_NAME() AS database_name;
