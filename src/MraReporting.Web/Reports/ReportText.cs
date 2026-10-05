using System.Globalization;
using System.Text.RegularExpressions;

namespace MraReporting.Reports;

/// <summary>Readable column headers, label clean-up and consistent number formats for charts and reports.</summary>
public static class ReportText
{
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GrossSales"] = "Gross sales (MWK)",
        ["VAT"] = "VAT (MWK)",
        ["TaxLines"] = "Tax lines",
        ["RateID"] = "Tax rate",
        ["RateChargedPct"] = "Rate charged (%)",
        ["TaxableAmount"] = "Taxable amount (MWK)",
        ["BusinessName"] = "Business name",
        ["FlagType"] = "Flag type",
        ["RedFlagged"] = "Red-flagged",
        ["Amount"] = "Amount (MWK)",
        ["InvestigationStatus"] = "Investigation status",
        ["StationCode"] = "Code",
        ["AssessedAmount"] = "Assessed (MWK)",
        ["PaidAmount"] = "Paid (MWK)",
        ["Liabilities"] = "Liabilities",
        ["TaxOffice"] = "Tax office",
        ["FirstFailure"] = "First failure",
        ["LastFailure"] = "Last failure",
        ["RecalledInvoices"] = "Recalled invoices",
        ["RecalledValue"] = "Recalled value (MWK)",
        ["RecallType"] = "Recall type",
        ["TerminalId"] = "Terminal",
        ["TaxpayersRegistered"] = "Taxpayers registered",
        ["ImportDeclarations"] = "Import declarations",
        ["FoundIn"] = "Found in",
        ["GrossSales1"] = "Gross sales, period 1 (MWK)",
        ["GrossSales2"] = "Gross sales, period 2 (MWK)",
        ["VAT1"] = "VAT, period 1 (MWK)",
        ["VAT2"] = "VAT, period 2 (MWK)",
        ["Invoices1"] = "Invoices, period 1",
        ["Invoices2"] = "Invoices, period 2",
        ["TurnoverChangePct"] = "Sales change (%)",
        ["VatChangePct"] = "VAT change (%)",
        ["CIFValue"] = "CIF value (MWK)",
        ["TaxesAssessed"] = "Taxes assessed (MWK)",
        ["ReceiptedDeclarations"] = "Paid declarations",
        ["AmountApplied"] = "Amount applied (MWK)",
        ["AmountPaid"] = "Amount paid (MWK)",
        ["SettledApplications"] = "Paid applications",
        ["AverageMonthlyRentAmount"] = "Average monthly rent (MWK)",
        ["UnitPriceAmount"] = "Unit price (MWK)",
        ["PriceAmount"] = "Price (MWK)",
        ["DiscountAmount"] = "Discount (MWK)",
        ["LineValue"] = "Line value (MWK)",
        ["StockValue"] = "Stock value (MWK)",
        ["SalesValue"] = "Sales value (MWK)",
        ["InvoiceValue"] = "Invoice value (MWK)",
        ["HsChapterCode"] = "HS chapter",
        ["HsChapter"] = "Tariff chapter",
        ["OriginCountryCode"] = "Origin code",
        ["OriginCountry"] = "Country of origin",
        ["POSVersion"] = "POS version",
        ["TaxName"] = "Tax",
        ["TaxCode"] = "Tax code",
        ["VatRegistered"] = "VAT-registered",
        ["WeekStarting"] = "Week starting",
        ["BuyerTIN"] = "Buyer TIN",
        ["TPIN"] = "TPIN",
        ["UOM"] = "Unit",
    };

    public static string Header(string column) =>
        Headers.TryGetValue(column, out var h) ? h : SplitWords(column);

    /// <summary>"OfflineTimeExceeded" becomes "Offline time exceeded"; codes in capitals are left alone.</summary>
    public static string SplitWords(string text)
    {
        // Only identifiers such as "OfflineTimeExceeded": no spaces, mixed case. Names like
        // "Sunbird Nkopola" or "PORTLAND CEMENT LTD" are returned unchanged.
        if (string.IsNullOrEmpty(text) || text.Contains(' ') || !text.Any(char.IsLower) || !text.Skip(1).Any(char.IsUpper))
            return text;
        var spaced = Regex.Replace(text, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        var words = char.ToUpper(spaced[0]) + spaced[1..].ToLowerInvariant();
        return words.Replace("In accurate", "Inaccurate");
    }

    /// <summary>Money columns get two decimals everywhere; counts get none.</summary>
    public static bool IsMoney(string column) =>
        column.Contains("Sales", StringComparison.OrdinalIgnoreCase)
        || column.Contains("VAT", StringComparison.Ordinal)
        || column.Contains("Amount", StringComparison.OrdinalIgnoreCase)
        || column.Contains("Value", StringComparison.OrdinalIgnoreCase)
        || column.Contains("Paid", StringComparison.OrdinalIgnoreCase)
        || column.Contains("Assessed", StringComparison.OrdinalIgnoreCase);

    public static string Cell(string column, object? value)
    {
        if (value is null) return "";
        if (IsMoney(column) && IsNumber(value))
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("#,0.00", CultureInfo.InvariantCulture);
        if (value is string s) return SplitWords(s);
        return Data.QueryResult.Format(value);
    }

    public static bool IsNumber(object? value) => value is decimal or double or float or long or int or short or byte;

    /// <summary>Short money text for chart labels: 1.48 bn, 119.8 m, 25,300.</summary>
    public static string Short(double value) => Math.Abs(value) switch
    {
        >= 1e9 => (value / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " bn",
        >= 1e6 => (value / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " m",
        _ => value.ToString("#,0", CultureInfo.InvariantCulture)
    };

    public static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
