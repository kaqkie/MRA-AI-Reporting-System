/* =====================================================================
   MRA AI Reporting - changes that need the DBA
   These are requests, not something to run yourself. Each section says
   why it is needed. Replace the placeholders in angle brackets.
   ===================================================================== */


/* ---------------------------------------------------------------------
   1. A dedicated login for the app (instead of personal credentials)
      - read-only on the eis_staging schema of STAGING_SSIS
      - read/write (not owner) in AI-REPORTING
   --------------------------------------------------------------------- */

-- Option A: a Windows service account (preferred with Windows authentication)
-- CREATE LOGIN [<DOMAIN>\svc_ai_reporting] FROM WINDOWS;

-- Option B: a SQL login
-- CREATE LOGIN ai_reporting_reader WITH PASSWORD = '<strong password>', CHECK_POLICY = ON;

USE STAGING_SSIS;
-- CREATE USER ai_reporting_reader FOR LOGIN <login name>;
-- GRANT SELECT ON SCHEMA::eis_staging TO ai_reporting_reader;
-- GRANT SELECT ON staging.Epay_CustomsLiability TO ai_reporting_reader;   -- customs payments and station names
-- GRANT SELECT ON staging.Epay_AllTaxpayers TO ai_reporting_reader;
GO

USE [AI-REPORTING];
-- CREATE USER ai_reporting_reader FOR LOGIN <login name>;
-- ALTER ROLE db_datareader ADD MEMBER ai_reporting_reader;
-- ALTER ROLE db_datawriter ADD MEMBER ai_reporting_reader;
GO

-- Also: map the developer's own login to AI-REPORTING so 01_ai_reporting_setup.sql can be run
-- (currently "The database AI-REPORTING is not accessible").


/* ---------------------------------------------------------------------
   2. Question: how does the SSIS package load eis_staging.Invoices?
      - Truncate and reload everything, drop and recreate the table, or append new rows?
      - How often, and at what time?
      The answer decides whether the index in step 3 survives each load, and when the
      summary refresh in step 4 should run.
   --------------------------------------------------------------------- */


/* ---------------------------------------------------------------------
   3. A nonclustered columnstore index on Invoices (Enterprise feature)
      Invoices has 7.6M rows / 2.4 GB and only ~49 MB of index, so every
      date-range question scans the whole table. A columnstore index makes
      totals by date, seller and site fast, without changing the table or
      the SSIS load (it only has to be recreated if the load drops the table).
   --------------------------------------------------------------------- */

USE STAGING_SSIS;
GO
-- CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Invoices_Reporting
-- ON eis_staging.Invoices
--     (InvoiceDateTime, SiteId, SellerTIN, InvoiceTotal, TotalVAT,
--      IsRecalled, IsExport, IsReliefSupply, PaymentMethod, RecallType);
GO

-- If a columnstore index is not acceptable, a rowstore index still helps date filters:
-- CREATE NONCLUSTERED INDEX IX_Invoices_InvoiceDateTime
-- ON eis_staging.Invoices (InvoiceDateTime)
-- INCLUDE (SiteId, SellerTIN, InvoiceTotal, TotalVAT, IsRecalled, IsExport, IsReliefSupply);

-- And for the VAT-by-rate join:
-- CREATE NONCLUSTERED INDEX IX_InvoiceTaxBreakdown_InvoiceNumber
-- ON eis_staging.InvoiceTaxBreakdown (InvoiceNumber) INCLUDE (RateID, TaxableAmount, TaxAmount);


/* ---------------------------------------------------------------------
   4. A SQL Server Agent job that refreshes the daily summary after each load
      Step (T-SQL, database AI-REPORTING):
   --------------------------------------------------------------------- */
-- DECLARE @to date = CAST(GETDATE() AS date);
-- DECLARE @from date = DATEADD(day, -7, @to);
-- EXEC dbo.usp_RefreshDailySales @FromDate = @from, @ToDate = @to;


/* ---------------------------------------------------------------------
   5. For information: the server is SQL Server 2017 RTM (14.0.1000.169)
      with no cumulative updates applied.
   --------------------------------------------------------------------- */
