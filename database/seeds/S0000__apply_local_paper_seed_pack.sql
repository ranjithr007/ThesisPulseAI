:setvar SeedRoot ".\database\seeds"
:setvar VerificationRoot ".\database\verification"

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

:r $(SeedRoot)\reference\S0001__seed_nse_exchange_and_draft_calendar.sql
GO
:r $(SeedRoot)\reference\S0002__seed_index_context.sql
GO
:r $(SeedRoot)\reference\S0003__seed_simulator_broker.sql
GO
:r $(SeedRoot)\policies\S0004__seed_paper_risk_policy.sql
GO
:r $(SeedRoot)\policies\S0005__seed_paper_order_transition_policy.sql
GO
:r $(VerificationRoot)\S0001__verify_reference_seeds.sql
GO
