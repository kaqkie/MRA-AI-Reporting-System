using Microsoft.Data.SqlClient;

namespace MraReporting.Data;

public enum ProductKind { Goods, Services }
public enum VoidGrouping { Status, Reason, Taxpayer, Day }
public enum InventoryMode { LowStock, Products, ByTaxpayer }
public enum CustomsFlow { Imports, Exports, All }
public enum DeclarationGrouping { Total, Day, Month, Office, Importer, Agent, OriginCountry, HsChapter, TaxType }
public enum PaymentGrouping { Total, Day, Month, Bank, Mode, Taxpayer }
public enum PropertyGrouping { City, Township, Type, Use, Occupancy, OwnerType, Owner }

/// <summary>Customs declaration filters: importer (code or name), customs office (code or name), tariff code prefix, country of origin.</summary>
public sealed record DeclarationFilter(string? Importer = null, string? Office = null, string? HsCode = null, string? Country = null);

/// <summary>
/// Lookups over the rest of the database: products sold, single invoices, void requests, inventory,
/// ASYCUDA customs declarations, ePayment bank payments and the rental property survey.
/// </summary>
public sealed partial class EisQueries
{
    // ---------------------------------------------------------------- products and services sold

    public Task<QueryResult> ProductsSoldAsync(DateOnly from, DateOnly to, ProductKind kind, string? search, IReadOnlyList<string>? tins,
        int top, string? scopeLabel, CancellationToken ct)
    {
        var parameters = new List<SqlParameter>(Window(from, to)) { SqlRunner.Int("@top", top) };
        var filter = FilterSql(new InvoiceFilter(tins), parameters);
        var (table, nameExpr) = kind == ProductKind.Services
            ? ("eis_staging.ServiceLineItems", "UPPER(LTRIM(RTRIM(li.ServiceName)))")
            : ("eis_staging.LineItems", "UPPER(LTRIM(RTRIM(li.Description)))");
        var searchSql = "";
        if (!string.IsNullOrWhiteSpace(search))
        {
            searchSql = $" AND {nameExpr} LIKE @search ESCAPE '\\'";
            parameters.Add(SqlRunner.VarChar("@search", NamePattern(search.Trim().ToUpperInvariant()), 200));
        }
        var sql = $"""
            SELECT TOP (@top)
                   ISNULL(NULLIF({nameExpr}, ''), '(no description)') AS Item,
                   COUNT(DISTINCT i.SellerTIN)                       AS Sellers,
                   COUNT_BIG(*)                                      AS InvoiceLines,
                   SUM(ISNULL(li.Quantity, 0))                       AS Quantity,
                   SUM(ISNULL(li.Quantity, 0) * ISNULL(li.UnitPrice, 0)) AS SalesValue
            FROM eis_staging.Invoices i
            JOIN {table} li ON li.InvoiceNumber = i.InvoiceNumber
            WHERE {InvoiceWindow} AND {NotRecalled}{filter}{searchSql}
            GROUP BY ISNULL(NULLIF({nameExpr}, ''), '(no description)')
            ORDER BY SalesValue DESC;
            """;
        var what = kind == ProductKind.Services ? "services" : "goods";
        var scope = (string.IsNullOrWhiteSpace(search) ? "" : $" matching '{search.Trim()}'") + (string.IsNullOrWhiteSpace(scopeLabel) ? "" : $" sold by {scopeLabel}");
        return _sql.QueryAsync($"Top {top} {what}{scope} by value, {Range(from, to)} (MWK)", sql, parameters, VisualKind.Bar, ct,
            note: "Value is quantity times unit price on the invoice lines, before discounts. Items are grouped by their description as typed by each seller, " +
                  "so the same product can appear under slightly different names. " + RecalledNote,
            chartValueColumns: ["SalesValue"]);
    }

    // ---------------------------------------------------------------- one invoice

    public async Task<QueryResult> InvoiceDetailAsync(string invoiceNumber, CancellationToken ct)
    {
        const string headerSql = """
            SELECT TOP (1) i.InvoiceNumber, i.InvoiceDateTime, i.SellerTIN, i.BuyerTIN, i.BuyerName, i.InvoiceTotal, i.TotalVAT,
                   i.PaymentMethod, i.TerminalId, i.SiteId, i.IsRecalled, i.RecallType, i.IsExport, i.IsReliefSupply,
                   (SELECT TOP (1) t.BusinessName FROM eis_staging.Taxpayers t WHERE t.TIN = i.SellerTIN) AS SellerName
            FROM eis_staging.Invoices i
            WHERE i.InvoiceNumber = @n;
            """;
        SqlParameter[] p1 = [SqlRunner.VarChar("@n", invoiceNumber, 100)];
        var header = await _sql.QueryAsync("Invoice", headerSql, p1, VisualKind.Table, ct);
        if (header.Rows.Count == 0)
            return header with { Title = $"Invoice {invoiceNumber} was not found", Note = "Check the invoice number. Only invoices loaded into the staging database can be found." };

        var h = header.Rows[0];
        string F(string column) { var v = h[header.Columns.ToList().IndexOf(column)]; return v is null or "" ? "" : QueryResult.Format(v); }
        const string linesSql = """
            SELECT li.ProductCode, li.Description AS Item, li.Quantity, li.UnitPrice AS UnitPriceAmount,
                   ISNULL(li.Quantity, 0) * ISNULL(li.UnitPrice, 0) AS LineValue, li.TaxRateID AS RateID, li.Discount AS DiscountAmount
            FROM eis_staging.LineItems li WHERE li.InvoiceNumber = @n
            UNION ALL
            SELECT NULL, s.ServiceName, s.Quantity, s.UnitPrice, ISNULL(s.Quantity, 0) * ISNULL(s.UnitPrice, 0), s.TaxRateID, NULL
            FROM eis_staging.ServiceLineItems s WHERE s.InvoiceNumber = @n;
            """;
        SqlParameter[] p2 = [SqlRunner.VarChar("@n", invoiceNumber, 100)];
        var lines = await _sql.QueryAsync("Invoice lines", linesSql, p2, VisualKind.Table, ct);
        var rateColumn = lines.Columns.ToList().IndexOf("RateID");
        if (rateColumn >= 0) lines = LabelRates(lines, rateColumn);

        var recalled = F("IsRecalled") is "yes" or "1";
        return lines with
        {
            Title = $"Invoice {invoiceNumber}: {F("SellerName")} (TIN {F("SellerTIN")}), {F("InvoiceDateTime")}, total MWK {F("InvoiceTotal")}, VAT MWK {F("TotalVAT")}",
            Note = $"Buyer: {(F("BuyerName").Length > 0 ? F("BuyerName") : "not named")}{(F("BuyerTIN").Length > 0 ? $" (TIN {F("BuyerTIN")})" : "")}. " +
                   $"Payment method: {(F("PaymentMethod").Length > 0 ? F("PaymentMethod") : "not recorded")}. Terminal {F("TerminalId")}, site {F("SiteId")}." +
                   (recalled ? $" This invoice was RECALLED ({F("RecallType")})." : "") +
                   (F("IsExport") is "yes" or "1" ? " Marked as an export." : ""),
        };
    }

    // ---------------------------------------------------------------- flags by taxpayer

    public Task<QueryResult> RedFlagsByTaxpayerAsync(DateOnly from, DateOnly to, int top, CancellationToken ct)
    {
        var sql = $"""
            WITH s AS (
                SELECT TOP (@top) f.SellerTIN,
                       COUNT_BIG(*) AS Transactions,
                       SUM(CASE WHEN f.IsRedFlagged = 1 THEN 1 ELSE 0 END) AS RedFlagged,
                       SUM(ISNULL(f.TransactionAmount, 0)) AS Amount,
                       MAX(f.TransactionFlagType) AS ExampleFlagType
                FROM eis_staging.TransactionsFlags f
                WHERE f.TransactionDate >= @from AND f.TransactionDate < @toEx
                GROUP BY f.SellerTIN
                ORDER BY RedFlagged DESC
            ), n AS ({TaxpayerNames})
            SELECT s.SellerTIN AS TIN, n.BusinessName, s.Transactions, s.RedFlagged, s.Amount, s.ExampleFlagType AS FlagType
            FROM s LEFT JOIN n ON n.TIN = s.SellerTIN
            ORDER BY s.RedFlagged DESC;
            """;
        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        return _sql.QueryAsync($"Taxpayers with the most flagged transactions (top {top}), {Range(from, to)}", sql, ps, VisualKind.Bar, ct,
            note: "FlagType shows one of the flag types each taxpayer received.", chartValueColumns: ["RedFlagged"]);
    }

    // ---------------------------------------------------------------- void requests

    public Task<QueryResult> VoidRequestsGroupedAsync(DateOnly from, DateOnly to, VoidGrouping by, int top, CancellationToken ct)
    {
        string sql;
        switch (by)
        {
            case VoidGrouping.Taxpayer:
                sql = """
                    WITH v AS (
                        SELECT v.InvoiceNumber, v.Status FROM eis_staging.VoidReceiptRequests v
                        WHERE v.RequestedOn >= @from AND v.RequestedOn < @toEx
                    ),
                    s AS (
                        SELECT TOP (@top) i.SellerTIN, COUNT_BIG(*) AS Requests, SUM(ISNULL(i.InvoiceTotal, 0)) AS InvoiceValue
                        FROM v JOIN eis_staging.Invoices i ON i.InvoiceNumber = v.InvoiceNumber
                        WHERE i.InvoiceDateTime >= DATEADD(DAY, -120, @from) AND i.InvoiceDateTime < @toEx
                        GROUP BY i.SellerTIN
                        ORDER BY Requests DESC
                    )
                    SELECT s.SellerTIN AS TIN, (SELECT TOP (1) t.BusinessName FROM eis_staging.Taxpayers t WHERE t.TIN = s.SellerTIN) AS BusinessName,
                           s.Requests, s.InvoiceValue
                    FROM s ORDER BY s.Requests DESC;
                    """;
                break;
            case VoidGrouping.Reason:
                sql = """
                    SELECT TOP (@top) ISNULL(NULLIF(LTRIM(RTRIM(v.RequestReason)), ''), '(no reason given)') AS Reason, COUNT_BIG(*) AS Requests
                    FROM eis_staging.VoidReceiptRequests v
                    WHERE v.RequestedOn >= @from AND v.RequestedOn < @toEx
                    GROUP BY ISNULL(NULLIF(LTRIM(RTRIM(v.RequestReason)), ''), '(no reason given)')
                    ORDER BY Requests DESC;
                    """;
                break;
            case VoidGrouping.Day:
                sql = """
                    SELECT CAST(v.RequestedOn AS date) AS [Date], COUNT_BIG(*) AS Requests
                    FROM eis_staging.VoidReceiptRequests v
                    WHERE v.RequestedOn >= @from AND v.RequestedOn < @toEx
                    GROUP BY CAST(v.RequestedOn AS date)
                    ORDER BY [Date];
                    """;
                break;
            default:
                return VoidRequestsAsync(from, to, ct);
        }
        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        var label = by switch { VoidGrouping.Taxpayer => $"by taxpayer (top {top})", VoidGrouping.Reason => "by reason", _ => "by day" };
        return _sql.QueryAsync($"Void receipt requests {label}, {Range(from, to)}", sql, ps,
            by == VoidGrouping.Day ? VisualKind.Line : VisualKind.Bar, ct,
            note: by == VoidGrouping.Taxpayer ? "Requests are linked to sellers through the invoice they ask to void; invoices older than 120 days before the period are not matched." : null,
            chartValueColumns: ["Requests"]);
    }

    // ---------------------------------------------------------------- stock (inventory)

    public Task<QueryResult> InventoryAsync(InventoryMode mode, IReadOnlyList<string>? tins, string? product, int top, string? scopeLabel, CancellationToken ct)
    {
        var parameters = new List<SqlParameter> { SqlRunner.Int("@top", top) };
        var where = "ISNULL(v.IsActive, 1) = 1";
        if (tins is { Count: > 0 })
            where += $" AND v.TIN IN ({string.Join(", ", tins.Select((t, k) => { parameters.Add(SqlRunner.VarChar($"@tin{k}", t, 30)); return $"@tin{k}"; }))})";
        if (!string.IsNullOrWhiteSpace(product))
        {
            where += " AND v.ProductName LIKE @product ESCAPE '\\'";
            parameters.Add(SqlRunner.VarChar("@product", NamePattern(product.Trim()), 200));
        }

        string sql, title;
        IReadOnlyList<string> chart;
        if (mode == InventoryMode.ByTaxpayer)
        {
            sql = $"""
                WITH s AS (
                    SELECT TOP (@top) v.TIN, COUNT(*) AS Products,
                           SUM(CASE WHEN v.ReorderLevel IS NOT NULL AND v.CurrentQuantity <= v.ReorderLevel THEN 1 ELSE 0 END) AS BelowReorderLevel,
                           SUM(ISNULL(v.CurrentQuantity, 0) * ISNULL(v.Price, 0)) AS StockValue
                    FROM eis_staging.TaxpayerInventories v
                    WHERE {where}
                    GROUP BY v.TIN
                    ORDER BY StockValue DESC
                ), n AS ({TaxpayerNames})
                SELECT s.TIN, n.BusinessName, s.Products, s.BelowReorderLevel, s.StockValue
                FROM s LEFT JOIN n ON n.TIN = s.TIN ORDER BY s.StockValue DESC;
                """;
            title = $"Stock held by taxpayers{(scopeLabel is null ? "" : $" ({scopeLabel})")}, top {top} by stock value (MWK)";
            chart = ["StockValue"];
        }
        else
        {
            var low = mode == InventoryMode.LowStock ? " AND v.ReorderLevel IS NOT NULL AND v.CurrentQuantity <= v.ReorderLevel" : "";
            sql = $"""
                WITH s AS (
                    SELECT TOP (@top) v.TIN, v.ProductName, v.CurrentQuantity, v.ReorderLevel, v.UOM, v.Price AS PriceAmount,
                           ISNULL(v.CurrentQuantity, 0) * ISNULL(v.Price, 0) AS StockValue, v.ModifiedOn AS LastUpdated
                    FROM eis_staging.TaxpayerInventories v
                    WHERE {where}{low}
                    ORDER BY StockValue DESC
                ), n AS ({TaxpayerNames})
                SELECT s.TIN, n.BusinessName, s.ProductName, s.CurrentQuantity, s.ReorderLevel, s.UOM, s.PriceAmount, s.StockValue, s.LastUpdated
                FROM s LEFT JOIN n ON n.TIN = s.TIN ORDER BY s.StockValue DESC;
                """;
            title = (mode == InventoryMode.LowStock ? "Products at or below their reorder level" : "Products in stock") +
                    (scopeLabel is null ? "" : $" ({scopeLabel})") + (string.IsNullOrWhiteSpace(product) ? "" : $" matching '{product!.Trim()}'") + $", top {top} by stock value (MWK)";
            chart = ["StockValue"];
        }
        return _sql.QueryAsync(title, sql, parameters, VisualKind.Table, ct, chartValueColumns: chart,
            note: "Stock as recorded by taxpayers in EIS, current position (not for a date). Value is quantity times the recorded price. " +
                  "Selling more than the recorded stock raises the 'Low inventory' red flag.");
    }

    // ---------------------------------------------------------------- ASYCUDA customs declarations

    public async Task<QueryResult> CustomsDeclarationsAsync(DateOnly from, DateOnly to, CustomsFlow flow, DeclarationGrouping by,
        DeclarationFilter f, int top, CancellationToken ct)
    {
        var parameters = new List<SqlParameter>(Window(from, to)) { SqlRunner.Int("@top", top) };
        var gWhere = "g.IDE_REG_DAT >= @from AND g.IDE_REG_DAT < @toEx";
        gWhere += flow switch { CustomsFlow.Imports => " AND LEFT(LTRIM(g.IDE_TYP_SAD), 2) = 'IM'", CustomsFlow.Exports => " AND LEFT(LTRIM(g.IDE_TYP_SAD), 2) = 'EX'", _ => "" };
        if (!string.IsNullOrWhiteSpace(f.Importer))
        {
            gWhere += " AND (g.CMP_CON_COD = @imp OR g.CMP_CON_NAM LIKE @impName ESCAPE '\\')";
            parameters.Add(new SqlParameter("@imp", System.Data.SqlDbType.NVarChar, 50) { Value = f.Importer.Trim() });
            parameters.Add(new SqlParameter("@impName", System.Data.SqlDbType.NVarChar, 200) { Value = NamePattern(f.Importer.Trim()) });
        }
        if (!string.IsNullOrWhiteSpace(f.Office))
        {
            gWhere += " AND (g.IDE_CUO_COD = @cuo OR g.IDE_CUO_NAM LIKE @cuoName ESCAPE '\\')";
            parameters.Add(new SqlParameter("@cuo", System.Data.SqlDbType.NVarChar, 20) { Value = f.Office.Trim() });
            parameters.Add(new SqlParameter("@cuoName", System.Data.SqlDbType.NVarChar, 200) { Value = NamePattern(f.Office.Trim()) });
        }
        var itemWhere = "";
        if (!string.IsNullOrWhiteSpace(f.HsCode))
        {
            itemWhere += " AND si.TAR_HSC_NB1 LIKE @hs";
            parameters.Add(new SqlParameter("@hs", System.Data.SqlDbType.NVarChar, 20) { Value = new string(f.HsCode.Where(char.IsDigit).ToArray()) + "%" });
        }
        if (!string.IsNullOrWhiteSpace(f.Country))
        {
            itemWhere += " AND (si.GDS_ORG_CTY = @cty OR si.GDS_ORG_CTY IN (SELECT c.CTY_COD FROM staging.aw_unctytab c WHERE c.CTY_DSC LIKE @ctyName ESCAPE '\\'))";
            parameters.Add(new SqlParameter("@cty", System.Data.SqlDbType.NVarChar, 10) { Value = f.Country.Trim().ToUpperInvariant() });
            parameters.Add(new SqlParameter("@ctyName", System.Data.SqlDbType.NVarChar, 200) { Value = NamePattern(f.Country.Trim()) });
        }
        if (itemWhere.Length > 0)
            gWhere += $" AND EXISTS (SELECT 1 FROM staging.aw_sad_item si WHERE si.INSTANCEID = g.INSTANCEID{itemWhere})";

        const string countryName = "(SELECT TOP (1) c.CTY_DSC FROM staging.aw_unctytab c WHERE c.CTY_COD = {0} ORDER BY c.VALID_FROM DESC)";
        string sql;
        string label;
        VisualKind visual = VisualKind.Bar;
        string[] chart = ["TaxesAssessed"];

        bool itemLevel = by is DeclarationGrouping.OriginCountry or DeclarationGrouping.HsChapter;
        if (by == DeclarationGrouping.TaxType)
        {
            sql = $"""
                WITH g AS (SELECT g.INSTANCEID FROM staging.aw_sad_general_segment g WHERE {gWhere})
                SELECT LTRIM(RTRIM(t.TAX_LIN_COD)) AS TaxCode,
                       (SELECT TOP (1) x.TAX_DSC FROM staging.aw_untaxtab x WHERE x.TAX_COD = t.TAX_LIN_COD ORDER BY x.VALID_FROM DESC) AS TaxName,
                       COUNT(DISTINCT t.INSTANCEID) AS Declarations,
                       SUM(ISNULL(t.TAX_LIN_AMT, 0)) AS TaxesAssessed
                FROM staging.aw_sad_tax t
                JOIN g ON g.INSTANCEID = t.INSTANCEID
                WHERE ISNULL(t.Is_Current, 1) = 1
                GROUP BY t.TAX_LIN_COD
                ORDER BY TaxesAssessed DESC;
                """;
            label = "by tax type";
        }
        else if (itemLevel)
        {
            var (key, nameSql, alias) = by == DeclarationGrouping.HsChapter
                ? ("LEFT(si.TAR_HSC_NB1, 2)", "(SELECT TOP (1) h.HS2_DSC FROM staging.aw_unhs2tab h WHERE h.HS2_COD = x.Code ORDER BY h.VALID_FROM DESC)", "HsChapter")
                : ("si.GDS_ORG_CTY", string.Format(countryName, "x.Code"), "OriginCountry");
            sql = $"""
                WITH g AS (SELECT g.INSTANCEID FROM staging.aw_sad_general_segment g WHERE {gWhere}),
                it AS (
                    SELECT si.INSTANCEID, si.KEY_ITM_NBR, {key} AS Code, ISNULL(si.VIT_CIF, 0) AS CIF
                    FROM staging.aw_sad_item si JOIN g ON g.INSTANCEID = si.INSTANCEID
                    WHERE 1 = 1{itemWhere}
                ),
                tx AS (
                    SELECT t.INSTANCEID, t.KEY_ITM_NBR, SUM(ISNULL(t.TAX_LIN_AMT, 0)) AS Taxes
                    FROM staging.aw_sad_tax t JOIN g ON g.INSTANCEID = t.INSTANCEID
                    WHERE ISNULL(t.Is_Current, 1) = 1
                    GROUP BY t.INSTANCEID, t.KEY_ITM_NBR
                ),
                x AS (
                    SELECT TOP (@top) it.Code, COUNT(DISTINCT it.INSTANCEID) AS Declarations, COUNT(*) AS Items,
                           SUM(it.CIF) AS CIFValue, SUM(ISNULL(tx.Taxes, 0)) AS TaxesAssessed
                    FROM it LEFT JOIN tx ON tx.INSTANCEID = it.INSTANCEID AND tx.KEY_ITM_NBR = it.KEY_ITM_NBR
                    GROUP BY it.Code
                    ORDER BY TaxesAssessed DESC
                )
                SELECT x.Code AS {alias}Code, {nameSql} AS {alias}, x.Declarations, x.Items, x.CIFValue, x.TaxesAssessed
                FROM x ORDER BY x.TaxesAssessed DESC;
                """;
            label = by == DeclarationGrouping.HsChapter ? $"by tariff chapter (top {top})" : $"by country of origin (top {top})";
        }
        else
        {
            var (keys, select, order, lbl) = by switch
            {
                DeclarationGrouping.Day => ("CAST(g.IDE_REG_DAT AS date)", "CAST(g.IDE_REG_DAT AS date) AS [Date]", "[Date]", "by day"),
                DeclarationGrouping.Month => ("DATEFROMPARTS(YEAR(g.IDE_REG_DAT), MONTH(g.IDE_REG_DAT), 1)", "DATEFROMPARTS(YEAR(g.IDE_REG_DAT), MONTH(g.IDE_REG_DAT), 1) AS [Month]", "[Month]", "by month"),
                DeclarationGrouping.Office => ("LTRIM(RTRIM(g.IDE_CUO_COD))", "LTRIM(RTRIM(g.IDE_CUO_COD)) AS OfficeCode, MAX(g.IDE_CUO_NAM) AS CustomsOffice", "TaxesAssessed DESC", "by customs office"),
                DeclarationGrouping.Importer => ("LTRIM(RTRIM(g.CMP_CON_COD))", "LTRIM(RTRIM(g.CMP_CON_COD)) AS ImporterCode, MAX(g.CMP_CON_NAM) AS Importer", "TaxesAssessed DESC", $"by importer (top {top})"),
                DeclarationGrouping.Agent => ("LTRIM(RTRIM(g.DEC_COD))", "LTRIM(RTRIM(g.DEC_COD)) AS AgentCode, MAX(g.DEC_NAM) AS ClearingAgent", "TaxesAssessed DESC", $"by clearing agent (top {top})"),
                _ => ("", "", "", "totals"),
            };
            var topSql = by is DeclarationGrouping.Importer or DeclarationGrouping.Agent ? "TOP (@top) " : "";
            var groupSql = keys.Length > 0 ? $"GROUP BY {keys}" : "";
            var orderSql = order.Length > 0 ? $"ORDER BY {order}" : "";
            sql = $"""
                WITH g AS (SELECT g.* FROM staging.aw_sad_general_segment g WHERE {gWhere}),
                it AS (SELECT si.INSTANCEID, SUM(ISNULL(si.VIT_CIF, 0)) AS CIF, COUNT(*) AS Items
                       FROM staging.aw_sad_item si JOIN g ON g.INSTANCEID = si.INSTANCEID GROUP BY si.INSTANCEID),
                tx AS (SELECT t.INSTANCEID, SUM(ISNULL(t.TAX_LIN_AMT, 0)) AS Taxes
                       FROM staging.aw_sad_tax t JOIN g ON g.INSTANCEID = t.INSTANCEID
                       WHERE ISNULL(t.Is_Current, 1) = 1 GROUP BY t.INSTANCEID)
                SELECT {topSql}{(select.Length > 0 ? select + "," : "")}
                       COUNT(*) AS Declarations,
                       SUM(ISNULL(it.Items, 0)) AS Items,
                       SUM(ISNULL(it.CIF, 0)) AS CIFValue,
                       SUM(ISNULL(tx.Taxes, 0)) AS TaxesAssessed,
                       SUM(CASE WHEN g.IDE_RCP_DAT IS NOT NULL THEN 1 ELSE 0 END) AS ReceiptedDeclarations
                FROM g
                LEFT JOIN it ON it.INSTANCEID = g.INSTANCEID
                LEFT JOIN tx ON tx.INSTANCEID = g.INSTANCEID
                {groupSql}
                {orderSql};
                """;
            label = lbl;
            visual = by switch { DeclarationGrouping.Day => VisualKind.Line, DeclarationGrouping.Total => VisualKind.Table, _ => VisualKind.Bar };
        }

        var flowWord = flow switch { CustomsFlow.Imports => "Import", CustomsFlow.Exports => "Export", _ => "Customs" };
        var scope = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Importer)) scope.Add($"importer '{f.Importer.Trim()}'");
        if (!string.IsNullOrWhiteSpace(f.Office)) scope.Add($"office '{f.Office.Trim()}'");
        if (!string.IsNullOrWhiteSpace(f.HsCode)) scope.Add($"tariff code {f.HsCode.Trim()}");
        if (!string.IsNullOrWhiteSpace(f.Country)) scope.Add($"origin '{f.Country.Trim()}'");
        return await _sql.QueryAsync(
            $"{flowWord} declarations {label}{(scope.Count > 0 ? " for " + string.Join(", ", scope) : "")}, {Range(from, to)} (MWK)",
            sql, parameters, visual, ct,
            note: (itemWhere.Length > 0 && !itemLevel && by != DeclarationGrouping.TaxType
                      ? "Declarations are included when at least one of their items matches the tariff code or origin; their values cover the whole declaration. " : "") +
                  "Source: ASYCUDA customs declarations, by registration date. CIF value is the customs value of the goods; taxes assessed are all duty and tax lines " +
                  "(customs duty, import VAT, excise and others). Receipted declarations are those with a customs receipt recorded (paid). These are assessments, not cash collected.",
            chartValueColumns: chart);
    }

    // ---------------------------------------------------------------- ePayment bank payments

    public Task<QueryResult> EpaymentPaymentsAsync(DateOnly from, DateOnly to, PaymentGrouping by, int top, CancellationToken ct)
    {
        var (key, alias, label) = by switch
        {
            PaymentGrouping.Day => ("CAST(p.ApplicationDate AS date)", "[Date]", "by day"),
            PaymentGrouping.Month => ("DATEFROMPARTS(YEAR(p.ApplicationDate), MONTH(p.ApplicationDate), 1)", "[Month]", "by month"),
            PaymentGrouping.Bank => ("ISNULL(NULLIF(LTRIM(RTRIM(ISNULL(p.ValidatingBank, p.PreferredBank))), ''), '(not recorded)')", "Bank", "by bank"),
            PaymentGrouping.Mode => ("ISNULL(NULLIF(LTRIM(RTRIM(ISNULL(p.TPFIPaymentMode, p.PreferredPaymentMode))), ''), '(not recorded)')", "PaymentMode", "by payment mode"),
            PaymentGrouping.Taxpayer => ("LTRIM(RTRIM(p.TPIN))", "TPIN", $"by taxpayer (top {top})"),
            _ => ("", "", "totals"),
        };
        var select = key.Length > 0 ? $"{key} AS {alias}," + (by == PaymentGrouping.Taxpayer ? " MAX(p.TaxPayerName) AS TaxpayerName," : "") : "";
        var sql = $"""
            SELECT {(by == PaymentGrouping.Taxpayer ? "TOP (@top) " : "")}{select}
                   COUNT_BIG(*) AS Applications,
                   SUM(ISNULL(p.Amount, 0)) AS AmountApplied,
                   SUM(CASE WHEN p.MRAPaymentReceiptDate IS NOT NULL THEN 1 ELSE 0 END) AS SettledApplications,
                   SUM(ISNULL(p.TPFIAmountPaid, 0)) AS AmountPaid,
                   SUM(CASE WHEN p.DateCancelled IS NOT NULL THEN 1 ELSE 0 END) AS Cancelled
            FROM staging.Epay_PaymentApplications p
            WHERE p.ApplicationDate >= @from AND p.ApplicationDate < @toEx
            {(key.Length > 0 ? $"GROUP BY {key}" : "")}
            {(by is PaymentGrouping.Day or PaymentGrouping.Month ? $"ORDER BY {alias}" : key.Length > 0 ? "ORDER BY AmountPaid DESC" : "")};
            """;
        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        return _sql.QueryAsync($"ePayment payment applications {label}, {Range(from, to)} (MWK)", sql, ps,
            by switch { PaymentGrouping.Day => VisualKind.Line, PaymentGrouping.Total => VisualKind.Table, _ => VisualKind.Bar }, ct,
            note: "Source: ePayment payment applications, by application date. Amount paid is what the bank reported as paid; cancelled applications are counted separately.",
            chartValueColumns: ["AmountPaid"]);
    }

    // ---------------------------------------------------------------- rental property survey

    public Task<QueryResult> RentalPropertiesAsync(PropertyGrouping by, string? city, int top, CancellationToken ct)
    {
        var parameters = new List<SqlParameter> { SqlRunner.Int("@top", top) };
        var where = "1 = 1";
        if (!string.IsNullOrWhiteSpace(city))
        {
            where += " AND (p.City LIKE @city ESCAPE '\\' OR p.Township LIKE @city ESCAPE '\\')";
            parameters.Add(new SqlParameter("@city", System.Data.SqlDbType.NVarChar, 200) { Value = NamePattern(city.Trim()) });
        }
        var (key, alias, label) = by switch
        {
            PropertyGrouping.Township => ("ISNULL(NULLIF(p.Township, ''), '(not recorded)')", "Township", "by township"),
            PropertyGrouping.Type => ("ISNULL(NULLIF(p.PropertyType, ''), '(not recorded)')", "PropertyType", "by property type"),
            PropertyGrouping.Use => ("ISNULL(NULLIF(p.PropertyUse, ''), '(not recorded)')", "PropertyUse", "by use"),
            PropertyGrouping.Occupancy => ("ISNULL(NULLIF(p.OccupancyStatus, ''), '(not recorded)')", "Occupancy", "by occupancy"),
            PropertyGrouping.OwnerType => ("ISNULL(NULLIF(o.OwnerType, ''), '(not recorded)')", "OwnerType", "by owner type"),
            PropertyGrouping.Owner => ("o.OwnerId", "OwnerId", $"by owner (top {top})"),
            _ => ("ISNULL(NULLIF(p.City, ''), '(not recorded)')", "City", "by city"),
        };
        var ownerCols = by == PropertyGrouping.Owner ? " MAX(o.FullName) AS OwnerName, MAX(o.TIN) AS TIN," : "";
        var sql = $"""
            SELECT {(by == PropertyGrouping.Owner ? "TOP (@top) " : "")}{key} AS {alias},{ownerCols}
                   COUNT(*) AS Properties,
                   SUM(ISNULL(p.NumberOfUnits, 0)) AS Units,
                   AVG(p.AverageMonthlyRent) AS AverageMonthlyRentAmount,
                   SUM(CASE WHEN c.IsRentalIncomeDeclared IS NOT NULL THEN 1 ELSE 0 END) AS Inspected,
                   SUM(CASE WHEN c.IsRentalIncomeDeclared = 1 THEN 1 ELSE 0 END) AS DeclaringRentalIncome,
                   SUM(CASE WHEN c.IsRentalIncomeDeclared = 0 THEN 1 ELSE 0 END) AS NotDeclaring
            FROM staging.rp_Properties p
            LEFT JOIN staging.rp_PropertyOwners o ON o.OwnerId = p.OwnerId
            OUTER APPLY (SELECT TOP (1) tc.IsRentalIncomeDeclared
                         FROM staging.rp_Inspections ins
                         JOIN staging.rp_TaxCompliance tc ON tc.InspectionId = ins.InspectionId
                         WHERE ins.PropertyId = p.PropertyId
                         ORDER BY ins.DateOfDataCollection DESC, tc.CreatedAt DESC) c
            WHERE {where}
            GROUP BY {key}
            ORDER BY Properties DESC;
            """;
        return _sql.QueryAsync($"Rental properties surveyed {label}{(string.IsNullOrWhiteSpace(city) ? "" : $" in {city.Trim()}")}", sql, parameters, VisualKind.Bar, ct,
            note: "Source: the rental property survey (inspections by MRA officers). Declaring rental income is from the most recent inspection of each property; " +
                  "properties not yet inspected are not counted as either. Average monthly rent is as recorded by the inspector.",
            chartValueColumns: ["Properties"]);
    }
}
