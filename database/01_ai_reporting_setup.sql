/* =====================================================================
   MRA AI Reporting - app tables in the AI-REPORTING database
   Run once, after the DBA has given your login access to AI-REPORTING.
   Safe to re-run: every object is only created if it does not exist.
   Nothing here touches STAGING_SSIS except the refresh procedure, which
   only reads from it.
   ===================================================================== */

USE [AI-REPORTING];
GO

/* ---------- Audit log: every question, query, export and report -------- */
IF OBJECT_ID(N'dbo.AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog (
        AuditId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
        AskedAt     datetime2(0)   NOT NULL,
        UserName    nvarchar(256)  NOT NULL,
        Kind        varchar(20)    NOT NULL,   -- chat | export | report
        Question    nvarchar(2000) NULL,
        ToolCalls   nvarchar(max)  NULL,       -- JSON: tool, arguments, rows, duration, error
        ResultRows  int            NULL,
        DurationMs  int            NULL,
        Answer      nvarchar(max)  NULL,
        Error       nvarchar(2000) NULL
    );
    CREATE INDEX IX_AuditLog_AskedAt ON dbo.AuditLog (AskedAt);
    CREATE INDEX IX_AuditLog_UserName ON dbo.AuditLog (UserName, AskedAt);
END
GO

/* ---------- Who may see what ------------------------------------------ */
IF OBJECT_ID(N'dbo.UserAccess', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserAccess (
        UserName     nvarchar(256) NOT NULL CONSTRAINT PK_UserAccess PRIMARY KEY,  -- DOMAIN\user
        Role         varchar(20)   NOT NULL
            CONSTRAINT CK_UserAccess_Role CHECK (Role IN ('Station', 'HQ', 'Leadership', 'Admin')),
        StationCode  varchar(50)   NULL,   -- meaning depends on what "station" turns out to be
        Department   varchar(50)   NULL,   -- e.g. Customs, Domestic Taxes
        IsActive     bit           NOT NULL CONSTRAINT DF_UserAccess_IsActive DEFAULT (1),
        CreatedAt    datetime2(0)  NOT NULL CONSTRAINT DF_UserAccess_CreatedAt DEFAULT (SYSDATETIME())
    );
END
GO

/* ---------- Lookups missing from EIS --------------------------------- */
IF OBJECT_ID(N'dbo.TaxRates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TaxRates (
        RateID       varchar(10)   NOT NULL CONSTRAINT PK_TaxRates PRIMARY KEY,  -- as stored in InvoiceTaxBreakdown.RateID
        Description  nvarchar(100) NOT NULL,                                     -- e.g. Standard rate, Zero-rated, Exempt
        RatePercent  decimal(5,2)  NULL
    );
END
GO

IF OBJECT_ID(N'dbo.CodeLookups', N'U') IS NULL
BEGIN
    -- One table for the small code lists. Domain examples:
    -- VoidStatus, PaymentMethod, RecallType, FlagType, InvestigationStatus, TaxOffice, CustomsStation
    CREATE TABLE dbo.CodeLookups (
        Domain  varchar(50)   NOT NULL,
        Code    varchar(50)   NOT NULL,
        Label   nvarchar(200) NOT NULL,
        CONSTRAINT PK_CodeLookups PRIMARY KEY (Domain, Code)
    );
END
GO

IF OBJECT_ID(N'dbo.StationAliases', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StationAliases (
        Alias        nvarchar(100) NOT NULL CONSTRAINT PK_StationAliases PRIMARY KEY,  -- what people type
        StationCode  varchar(50)   NOT NULL
    );
END
GO

/* ---------- Report definitions, schedules and history ----------------- */
IF OBJECT_ID(N'dbo.ReportDefinitions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportDefinitions (
        ReportId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportDefinitions PRIMARY KEY,
        Name         nvarchar(100) NOT NULL CONSTRAINT UQ_ReportDefinitions_Name UNIQUE,
        Description  nvarchar(500) NULL,
        IsActive     bit NOT NULL CONSTRAINT DF_ReportDefinitions_IsActive DEFAULT (1)
    );
END
GO

IF OBJECT_ID(N'dbo.ReportSections', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportSections (
        SectionId   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportSections PRIMARY KEY,
        ReportId    int NOT NULL CONSTRAINT FK_ReportSections_Report REFERENCES dbo.ReportDefinitions (ReportId),
        SortOrder   int NOT NULL,
        Title       nvarchar(200) NOT NULL,
        QueryName   varchar(100) NOT NULL,   -- name of a method in the vetted query library
        Parameters  nvarchar(max) NULL,      -- JSON, e.g. {"days":30,"groupBy":"day"}
        Visual      varchar(10) NOT NULL CONSTRAINT CK_ReportSections_Visual CHECK (Visual IN ('None', 'Table', 'Line', 'Bar', 'Pie'))
    );
END
GO

IF OBJECT_ID(N'dbo.ReportSchedules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportSchedules (
        ScheduleId  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportSchedules PRIMARY KEY,
        ReportId    int NOT NULL CONSTRAINT FK_ReportSchedules_Report REFERENCES dbo.ReportDefinitions (ReportId),
        Frequency   varchar(10) NOT NULL CONSTRAINT CK_ReportSchedules_Frequency CHECK (Frequency IN ('Daily', 'Weekly', 'Monthly')),
        RunAtTime   time(0) NOT NULL,             -- Malawi time
        Format      varchar(10) NOT NULL CONSTRAINT CK_ReportSchedules_Format CHECK (Format IN ('pdf', 'xlsx')),
        Recipients  nvarchar(1000) NULL,          -- email addresses or a shared folder path
        IsActive    bit NOT NULL CONSTRAINT DF_ReportSchedules_IsActive DEFAULT (1),
        LastRunAt   datetime2(0) NULL
    );
END
GO

IF OBJECT_ID(N'dbo.ReportRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportRuns (
        RunId        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportRuns PRIMARY KEY,
        ReportName   nvarchar(100) NOT NULL,
        ReportDate   date NULL,
        RequestedBy  nvarchar(256) NOT NULL,   -- a user, or 'scheduler'
        StartedAt    datetime2(0) NOT NULL,
        FinishedAt   datetime2(0) NULL,
        Format       varchar(10) NOT NULL,
        Status       varchar(20) NOT NULL,     -- Running | Succeeded | Failed
        FilePath     nvarchar(500) NULL,
        Error        nvarchar(2000) NULL
    );
    CREATE INDEX IX_ReportRuns_StartedAt ON dbo.ReportRuns (StartedAt);
END
GO

/* ---------- Targets (only if MRA provides them) ------------------------ */
IF OBJECT_ID(N'dbo.RevenueTargets', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RevenueTargets (
        StationCode   varchar(50)   NOT NULL,   -- 'NATIONAL' for the national target
        PeriodStart   date          NOT NULL,
        PeriodEnd     date          NOT NULL,
        TargetAmount  decimal(20,2) NOT NULL,
        CONSTRAINT PK_RevenueTargets PRIMARY KEY (StationCode, PeriodStart)
    );
END
GO

/* ---------- Daily summary: what chat and charts read on production data ---
   Grain: one row per day, seller and site. Any "station" definition that is
   based on the seller (e.g. tax office via Taxpayers.TaxOfficeCode) can be
   rolled up from this without touching the 7.6M+ invoice rows.            */
IF OBJECT_ID(N'dbo.DailySales', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DailySales (
        SummaryDate        date          NOT NULL,
        SellerTIN          varchar(30)   NOT NULL,
        SiteId             varchar(50)   NOT NULL,
        Invoices           int           NOT NULL,
        GrossSales         decimal(20,2) NOT NULL,
        VAT                decimal(20,2) NOT NULL,
        RecalledInvoices   int           NOT NULL,
        RecalledValue      decimal(20,2) NOT NULL,
        ExportSales        decimal(20,2) NOT NULL,
        ReliefSupplySales  decimal(20,2) NOT NULL,
        RefreshedAt        datetime2(0)  NOT NULL,
        CONSTRAINT PK_DailySales PRIMARY KEY (SummaryDate, SellerTIN, SiteId)
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_RefreshDailySales
    @FromDate date,
    @ToDate   date
AS
BEGIN
    /* Rebuilds DailySales for the given days only. Schedule it after each SSIS
       load for the last few days (late and recalled invoices change old days):
         DECLARE @to date = CAST(GETDATE() AS date), @from date = DATEADD(day, -7, CAST(GETDATE() AS date));
         EXEC dbo.usp_RefreshDailySales @FromDate = @from, @ToDate = @to;
       For the first load, run it month by month over the full history. */
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @FromDate IS NULL OR @ToDate IS NULL OR @ToDate < @FromDate
        THROW 50001, 'Give a valid @FromDate and @ToDate.', 1;

    BEGIN TRANSACTION;

    DELETE FROM dbo.DailySales
    WHERE SummaryDate BETWEEN @FromDate AND @ToDate;

    INSERT INTO dbo.DailySales
        (SummaryDate, SellerTIN, SiteId, Invoices, GrossSales, VAT,
         RecalledInvoices, RecalledValue, ExportSales, ReliefSupplySales, RefreshedAt)
    SELECT
        CAST(i.InvoiceDateTime AS date),
        i.SellerTIN,
        i.SiteId,
        SUM(CASE WHEN ISNULL(i.IsRecalled, 0) = 0 THEN 1 ELSE 0 END),
        SUM(CASE WHEN ISNULL(i.IsRecalled, 0) = 0 THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END),
        SUM(CASE WHEN ISNULL(i.IsRecalled, 0) = 0 THEN ISNULL(i.TotalVAT, 0) ELSE 0 END),
        SUM(CASE WHEN i.IsRecalled = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.IsRecalled = 1 THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END),
        SUM(CASE WHEN ISNULL(i.IsRecalled, 0) = 0 AND i.IsExport = 1 THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END),
        SUM(CASE WHEN ISNULL(i.IsRecalled, 0) = 0 AND i.IsReliefSupply = 1 THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END),
        SYSDATETIME()
    FROM STAGING_SSIS.eis_staging.Invoices AS i
    WHERE i.InvoiceDateTime >= @FromDate
      AND i.InvoiceDateTime <  DATEADD(day, 1, @ToDate)
    GROUP BY CAST(i.InvoiceDateTime AS date), i.SellerTIN, i.SiteId;

    COMMIT TRANSACTION;
END
GO

PRINT 'AI-REPORTING objects are in place.';
