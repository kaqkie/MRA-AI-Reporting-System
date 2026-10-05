using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MraReporting.Ai;

/// <summary>
/// Picks the lookups worth offering the model for one question. Every tool description is text the
/// model must read before answering, and on a CPU that reading is the slow part, so offering about ten
/// relevant tools instead of all of them makes answers much faster. A small core is always offered;
/// topic groups are added when the question (or the previous question, for follow-ups) mentions them.
/// </summary>
public static class ToolRouter
{
    private static readonly string[] Core =
    [
        "CreateSummaryReport", "GetDataCoverage", "GetSalesTotals", "GetSalesByTaxOffice", "GetTopTaxpayers",
        "AnalyseSales", "CompareSalesPeriods", "GetTaxpayerProfile", "GetLargestInvoices",
    ];

    private static readonly (Regex Pattern, string[] Tools)[] Groups =
    [
        (Words("customs", "import", "imports", "imported", "importer", "importers", "importing", "export", "exports", "exporter", "exporters", "border", "duty", "duties", "tariff", "hs", "asycuda",
               "station", "stations", "port", "ports", "airport", "agent", "agents", "clearing", "origin", "country", "countries", "declaration", "declarations",
               "songwe", "mwanza", "mchinji", "dedza", "muloza", "kia", "chileka"),
            ["GetCustomsCollections", "GetStationCodes", "GetCustomsDeclarations"]),
        (Words("epayment", "e-payment", "bank", "banks", "cheque", "payment application", "payment applications", "paid through"),
            ["GetEpaymentPayments", "GetCustomsCollections"]),
        (Words("flag", "flags", "flagged", "risk", "risky", "fail", "failed", "failure", "failures", "void", "voided", "voids", "recall", "recalled",
               "recalls", "cancel", "cancelled", "canceled", "tamper", "tampered", "suspicious", "investigation"),
            ["GetRedFlags", "GetFailedTransactions", "GetVoidRequests", "GetRecalledInvoices", "GetTamperedTerminals"]),
        (Words("register", "registered", "registration", "blacklist", "blacklisted", "onboard", "onboarded", "joined", "new taxpayers", "find", "search",
               "list", "how many taxpayers", "stopped", "inactive", "dormant", "quiet", "went quiet", "vat-registered", "vat registered", "office has"),
            ["SearchTaxpayerRegister", "FindInactiveSellers", "GetRegisterCounts"]),
        (Words("terminal", "terminals", "device", "devices", "pos", "machine", "machines", "efd", "efds", "till", "tills"),
            ["GetTaxpayerTerminals", "GetTerminalStatistics", "GetTamperedTerminals", "GetRegisterCounts"]),
        (Words("product", "products", "item", "items", "goods", "service", "services", "sell", "sells", "sold", "selling", "stock", "stocks",
               "inventory", "reorder", "warehouse"),
            ["GetProductsSold", "GetInventory"]),
        (Words("invoice number", "receipt number", "invoice no", "receipt no", "invoice #"),
            ["GetInvoiceDetails"]),
        (Words("rent", "rental", "rentals", "property", "properties", "landlord", "landlords", "tenant", "tenants", "house", "houses", "plot", "township"),
            ["GetRentalProperties"]),
        (Words("rate", "rates", "zero", "zero-rated", "zero rated", "exempt", "standard rated", "standard-rated"),
            ["GetVatByTaxRate"]),
        (Words("site", "sites", "branch", "branches", "premises", "how many taxpayers", "how many stations", "count"),
            ["GetRegisterCounts", "GetStationCodes"]),
    ];

    private static readonly Regex SalesWords = Words("sales", "sale", "vat", "invoice", "invoices", "turnover", "sold", "selling", "taxpayer's", "domestic");

    /// <summary>An invoice number typed on its own, e.g. "ABC123-4567": letters and digits mixed, at least 8 characters.</summary>
    private static readonly Regex InvoiceNumber = new(@"\b(?=[A-Z0-9/-]*\d)(?=[A-Z0-9/-]*[A-Z])[A-Z0-9/-]{8,}\b", RegexOptions.Compiled);

    public static IList<AITool> Select(IList<AITool> all, string question, string? previousQuestion)
    {
        var wanted = new HashSet<string>(Core, StringComparer.Ordinal);
        foreach (var text in new[] { question, previousQuestion ?? "" })
        {
            var lower = text.ToLowerInvariant();
            foreach (var (pattern, tools) in Groups)
                if (pattern.IsMatch(lower)) wanted.UnionWith(tools);
            if (InvoiceNumber.IsMatch(text.ToUpperInvariant())) wanted.Add("GetInvoiceDetails");
        }
        // A customs-only question ("compare customs payments at the border posts") must not be answered with
        // the invoice sales lookups, so those are not offered unless sales, VAT or invoices are mentioned.
        var all2 = (question + " " + (previousQuestion ?? "")).ToLowerInvariant();
        if (Groups[0].Pattern.IsMatch(all2) && !SalesWords.IsMatch(all2))
            wanted.ExceptWith(["CompareSalesPeriods", "AnalyseSales", "GetSalesTotals", "GetSalesByTaxOffice", "GetTopTaxpayers"]);

        // Core lookups first, always in the same order, so llama-server can reuse what it has already read
        // (the instructions plus the core lookups) and only reads the extra topic lookups fresh.
        var byName = all.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var ordered = Core.Where(n => byName.ContainsKey(n) && wanted.Contains(n)).Select(n => byName[n]).ToList();
        ordered.AddRange(all.Where(t => wanted.Contains(t.Name) && !Core.Contains(t.Name)));
        return ordered;
    }

    private static Regex Words(params string[] words) =>
        new(@"\b(" + string.Join("|", words.Select(Regex.Escape)) + @")\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
