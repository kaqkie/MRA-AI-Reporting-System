/* =====================================================================
   MRA AI Reporting - discovery queries
   Read-only. Run in SSMS and send the results back; they answer the
   open questions in the project documentation.
   ===================================================================== */

USE STAGING_SSIS;
GO

/* 1. Every code value and how often it appears.
      Heavy: scans Invoices several times. Run outside working hours. */
SELECT 'RateID' AS Field, RateID AS Value, COUNT(*) AS Rows FROM eis_staging.InvoiceTaxBreakdown GROUP BY RateID
UNION ALL SELECT 'PaymentMethod', PaymentMethod, COUNT(*) FROM eis_staging.Invoices GROUP BY PaymentMethod
UNION ALL SELECT 'RecallType', RecallType, COUNT(*) FROM eis_staging.Invoices GROUP BY RecallType
UNION ALL SELECT 'IsRecalled', CAST(IsRecalled AS varchar(5)), COUNT(*) FROM eis_staging.Invoices GROUP BY IsRecalled
UNION ALL SELECT 'VoidStatus', CAST(Status AS varchar(5)), COUNT(*) FROM eis_staging.VoidReceiptRequests GROUP BY Status
UNION ALL SELECT 'FlagType', TransactionFlagType, COUNT(*) FROM eis_staging.TransactionsFlags GROUP BY TransactionFlagType
UNION ALL SELECT 'InvestigationStatus', InvestigationStatus, COUNT(*) FROM eis_staging.TransactionsFlags GROUP BY InvestigationStatus
UNION ALL SELECT 'TaxOfficeCode', TaxOfficeCode, COUNT(*) FROM eis_staging.Taxpayers GROUP BY TaxOfficeCode
UNION ALL SELECT 'DeclarationStationCode', DeclarationStationCode, COUNT(*) FROM eis_staging.ImportationDetails GROUP BY DeclarationStationCode
ORDER BY Field, Rows DESC;
GO

/* 2. Date range of the invoice data. */
SELECT MIN(InvoiceDateTime) AS FirstInvoice, MAX(InvoiceDateTime) AS LastInvoice, COUNT_BIG(*) AS Invoices
FROM eis_staging.Invoices;
GO

/* 3. How recalls are recorded: does a recalled invoice point at a new invoice,
      and does that new invoice carry negative amounts? */
SELECT TOP (20)
       o.InvoiceNumber, o.InvoiceTotal, o.IsRecalled, o.RecallType, o.RecalledDate,
       o.RecalledInvoiceNumber, n.InvoiceTotal AS LinkedInvoiceTotal, n.IsRecalled AS LinkedIsRecalled
FROM eis_staging.Invoices o
LEFT JOIN eis_staging.Invoices n ON n.InvoiceNumber = CAST(o.RecalledInvoiceNumber AS varchar(50))
WHERE o.IsRecalled = 1
ORDER BY o.RecalledDate DESC;
GO

/* 4. Do invoice terminal ids match the Terminal table? (different column lengths) */
SELECT TOP (1000) i.TerminalId
INTO #sample
FROM eis_staging.Invoices i
WHERE i.TerminalId IS NOT NULL
ORDER BY i.InvoiceDateTime DESC;

SELECT COUNT(*) AS SampledInvoices,
       SUM(CASE WHEN t.TerminalID IS NOT NULL THEN 1 ELSE 0 END) AS MatchedTerminals
FROM #sample s
LEFT JOIN eis_staging.Terminal t ON t.TerminalID = LTRIM(RTRIM(s.TerminalId));

DROP TABLE #sample;
GO
