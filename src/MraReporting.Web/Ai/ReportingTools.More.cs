using System.ComponentModel;
using System.Text.RegularExpressions;
using MraReporting.Data;

namespace MraReporting.Ai;

/// <summary>
/// The flexible lookups: general sales analysis, period comparisons, the taxpayer register, inactive
/// sellers, terminals, products, single invoices, stock, customs declarations, ePayment payments and
/// rental properties. Each validates its choices against fixed lists and passes values as parameters.
/// </summary>
public sealed partial class ReportingTools
{
    [Description("Flexible sales and VAT lookup from invoices. Use for anything the other sales lookups cannot do: one taxpayer's sales or VAT (by name or TIN), sales over time by day, week or month, by business site or terminal of a taxpayer, by buyer, by payment method, export or relief sales, or a ranking within one tax office or town.")]
    public Task<string> AnalyseSales(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'total', 'day', 'week', 'month', 'taxpayer', 'taxOffice', 'site', 'terminal', 'buyer' or 'paymentMethod'")] string groupBy = "total",
        [Description("Only this seller: business name or TIN. Leave empty for all.")] string taxpayer = "",
        [Description("Only sellers registered at this tax office: code, name or town. Leave empty for all.")] string taxOffice = "",
        [Description("'all', 'domestic', 'export' or 'relief'")] string saleType = "all",
        [Description("Rank by 'vat', 'sales' or 'invoices'")] string sortBy = "vat",
        [Description("How many rows for rankings, 1 to 50")] int top = 10)
        => Run(nameof(AnalyseSales), new { fromDate, toDate, groupBy, taxpayer, taxOffice, saleType, sortBy, top }, async () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            var filter = await BuildFilterAsync(taxpayer, taxOffice, ParseEnum<InvoiceKind>(saleType, nameof(saleType)));
            return await _queries.InvoiceAnalysisAsync(from, to, ParseEnum<InvoiceGrouping>(groupBy, nameof(groupBy)),
                ParseEnum<InvoiceSort>(sortBy, nameof(sortBy)), Clamp(top, 1, 50), filter, _ct);
        }, coverageKey: "invoices");

    [Description("Compares sales and VAT between two periods, with the change worked out. Use for 'compare August and September', 'this month against last month', 'which taxpayers' VAT fell the most', 'how did each tax office change'. Period 1 is normally the earlier one.")]
    public Task<string> CompareSalesPeriods(
        [Description("Period 1 start, yyyy-MM-dd")] string fromDate1,
        [Description("Period 1 end, yyyy-MM-dd")] string toDate1,
        [Description("Period 2 start, yyyy-MM-dd")] string fromDate2,
        [Description("Period 2 end, yyyy-MM-dd")] string toDate2,
        [Description("'total', 'taxpayer' or 'taxOffice'")] string groupBy = "total",
        [Description("Only this seller: business name or TIN. Leave empty for all.")] string taxpayer = "",
        [Description("Only sellers of this tax office: code, name or town. Leave empty for all.")] string taxOffice = "",
        [Description("true to list the biggest rises and falls instead of the largest in period 2")] bool biggestChanges = false,
        [Description("How many rows, 1 to 50")] int top = 10)
        => Run(nameof(CompareSalesPeriods), new { fromDate1, toDate1, fromDate2, toDate2, groupBy, taxpayer, taxOffice, biggestChanges, top }, async () =>
        {
            var (f1, t1) = DateRange(fromDate1, toDate1);
            var (f2, t2) = DateRange(fromDate2, toDate2);
            var filter = await BuildFilterAsync(taxpayer, taxOffice, InvoiceKind.All);
            return await _queries.ComparePeriodsAsync(f1, t1, f2, t2, ParseEnum<CompareGrouping>(groupBy, nameof(groupBy)), biggestChanges, Clamp(top, 1, 50), filter, _ct);
        });

    [Description("Sellers that issued invoices in an earlier period but none in a later one (stopped invoicing, possibly closed or no longer using EIS). Use for 'which taxpayers stopped issuing invoices', 'dormant taxpayers', 'who went quiet'. Leave the dates empty to compare the last 30 days of data with the 30 days before.")]
    public Task<string> FindInactiveSellers(
        [Description("Earlier (active) period start, yyyy-MM-dd, or empty")] string activeFrom = "",
        [Description("Earlier (active) period end, yyyy-MM-dd, or empty")] string activeTo = "",
        [Description("Later (quiet) period start, yyyy-MM-dd, or empty")] string quietFrom = "",
        [Description("Later (quiet) period end, yyyy-MM-dd, or empty")] string quietTo = "",
        [Description("Only sellers of this tax office: code, name or town. Leave empty for all.")] string taxOffice = "",
        [Description("How many sellers to list, 1 to 50")] int top = 20)
        => Run(nameof(FindInactiveSellers), new { activeFrom, activeTo, quietFrom, quietTo, taxOffice, top }, async () =>
        {
            DateOnly af, at, qf, qt;
            var latest = await LatestInvoiceDateAsync();
            if (string.IsNullOrWhiteSpace(quietFrom) || string.IsNullOrWhiteSpace(quietTo))
            {
                qt = latest; qf = latest.AddDays(-29);
            }
            else (qf, qt) = DateRange(quietFrom, quietTo);
            if (string.IsNullOrWhiteSpace(activeFrom) || string.IsNullOrWhiteSpace(activeTo))
            {
                var len = qt.DayNumber - qf.DayNumber + 1;
                at = qf.AddDays(-1); af = at.AddDays(-(len - 1));
            }
            else (af, at) = DateRange(activeFrom, activeTo);
            if (qf <= at) throw new ToolArgumentException("The quiet period must start after the active period ends.");

            var filter = await BuildFilterAsync("", taxOffice, InvoiceKind.All);
            var result = await _queries.InactiveSellersAsync(af, at, qf, qt, Clamp(top, 1, 50), filter, _ct);
            if (qt > latest)
                result = result with { Note = result.Note + $" Invoice data currently ends on {latest:yyyy-MM-dd}, so sellers may look inactive only because later data is not loaded yet." };
            return result;
        });

    [Description("Searches the taxpayer register: find taxpayers by name or TIN, list taxpayers of a tax office, VAT-registered or blacklisted taxpayers (with their recent invoicing), count taxpayers by tax office, or count new taxpayers onboarded per month. Use for 'find taxpayer X', 'list blacklisted taxpayers', 'are blacklisted taxpayers still invoicing', 'how many taxpayers joined in September', 'how many taxpayers per office'.")]
    public Task<string> SearchTaxpayerRegister(
        [Description("'list', 'countByOffice' or 'countByMonth'")] string mode = "list",
        [Description("Business name (or part of it) or TIN. Leave empty for all.")] string name = "",
        [Description("Tax office code, name or town. Leave empty for all.")] string taxOffice = "",
        [Description("'any', 'yes' or 'no'")] string vatRegistered = "any",
        [Description("'any', 'yes' or 'no'")] string blacklisted = "any",
        [Description("Registered on or after, yyyy-MM-dd, or empty")] string registeredFrom = "",
        [Description("Registered on or before, yyyy-MM-dd, or empty")] string registeredTo = "",
        [Description("How many taxpayers to list, 1 to 100")] int top = 25)
        => Run(nameof(SearchTaxpayerRegister), new { mode, name, taxOffice, vatRegistered, blacklisted, registeredFrom, registeredTo, top }, async () =>
        {
            var offices = string.IsNullOrWhiteSpace(taxOffice) ? null : ResolveOffices(taxOffice);
            DateOnly? rf = string.IsNullOrWhiteSpace(registeredFrom) ? null : ParseDate(registeredFrom, nameof(registeredFrom));
            DateOnly? rt = string.IsNullOrWhiteSpace(registeredTo) ? null : ParseDate(registeredTo, nameof(registeredTo));
            var today = _clock.Today;
            return await _queries.TaxpayerRegisterAsync(ParseEnum<TaxpayerListMode>(mode, nameof(mode)), name, offices,
                YesNo(vatRegistered, nameof(vatRegistered)), YesNo(blacklisted, nameof(blacklisted)), rf, rt,
                today.AddDays(-89), today, Clamp(top, 1, 100), _ct);
        });

    [Description("Statistics about fiscal terminals (POS devices): counts by operating system, POS software version, activation month or status, or 'quiet' terminals that are active but issued no invoices in a period. Use for 'how many terminals were activated in September', 'which POS versions are in use', 'terminals not used in the last 30 days'.")]
    public Task<string> GetTerminalStatistics(
        [Description("'operatingSystem', 'version', 'activationMonth', 'status' or 'quiet'")] string groupBy = "status",
        [Description("For 'quiet': start date, yyyy-MM-dd, or empty for the last 30 days of data")] string fromDate = "",
        [Description("For 'quiet': end date, yyyy-MM-dd, or empty")] string toDate = "",
        [Description("For 'quiet': how many terminals to list, 1 to 100")] int top = 25)
        => Run(nameof(GetTerminalStatistics), new { groupBy, fromDate, toDate, top }, async () =>
        {
            if (string.Equals(groupBy?.Trim(), "quiet", StringComparison.OrdinalIgnoreCase))
            {
                DateOnly from, to;
                if (string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
                {
                    to = await LatestInvoiceDateAsync(); from = to.AddDays(-29);
                }
                else (from, to) = DateRange(fromDate, toDate);
                return await _queries.QuietTerminalsAsync(from, to, Clamp(top, 1, 100), _ct);
            }
            return await _queries.TerminalStatisticsAsync(ParseEnum<TerminalGrouping>(groupBy, nameof(groupBy)), _ct);
        });

    [Description("Goods or services sold, from the invoice lines: the top items by value, items matching a word (e.g. 'cement', 'sugar', 'fuel'), or what one taxpayer sells. Use for 'what products sell the most', 'sales of cement', 'what does Portland Cement sell'.")]
    public Task<string> GetProductsSold(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'goods' or 'services'")] string kind = "goods",
        [Description("Word in the item description, e.g. 'cement'. Leave empty for all items.")] string search = "",
        [Description("Only this seller: business name or TIN. Leave empty for all.")] string taxpayer = "",
        [Description("How many items, 1 to 50")] int top = 15)
        => Run(nameof(GetProductsSold), new { fromDate, toDate, kind, search, taxpayer, top }, async () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            var filter = await BuildFilterAsync(taxpayer, "", InvoiceKind.All);
            return await _queries.ProductsSoldAsync(from, to, ParseEnum<ProductKind>(kind, nameof(kind)), search, filter.Tins, Clamp(top, 1, 50), filter.ScopeLabel, _ct);
        }, coverageKey: "invoices");

    [Description("Shows one invoice by its invoice number: seller, buyer, date, total, VAT, payment method, recall status and its lines. Use when the user gives an invoice number.")]
    public Task<string> GetInvoiceDetails(
        [Description("The invoice number exactly as given")] string invoiceNumber)
        => Run(nameof(GetInvoiceDetails), new { invoiceNumber }, () =>
        {
            var n = (invoiceNumber ?? "").Trim();
            if (n.Length is < 3 or > 100) throw new ToolArgumentException("Ask the user for the full invoice number.");
            return _queries.InvoiceDetailAsync(n, _ct);
        });

    [Description("Stock (inventory) that taxpayers record in EIS, as it stands now: products at or below their reorder level, products in stock (optionally matching a word), or stock value by taxpayer. Use for 'which products are low on stock', 'stock held by X', 'how much cement does X have'.")]
    public Task<string> GetInventory(
        [Description("'lowStock', 'products' or 'byTaxpayer'")] string mode = "products",
        [Description("Only this taxpayer: business name or TIN. Leave empty for all.")] string taxpayer = "",
        [Description("Word in the product name. Leave empty for all.")] string product = "",
        [Description("How many rows, 1 to 100")] int top = 25)
        => Run(nameof(GetInventory), new { mode, taxpayer, product, top }, async () =>
        {
            var filter = await BuildFilterAsync(taxpayer, "", InvoiceKind.All);
            return await _queries.InventoryAsync(ParseEnum<InventoryMode>(mode, nameof(mode)), filter.Tins, product, Clamp(top, 1, 100), filter.ScopeLabel, _ct);
        });

    [Description("Customs declarations from ASYCUDA (imports and exports): number of declarations, customs (CIF) value and duties and taxes assessed, by day, month, customs office, importer, clearing agent, country of origin, tariff chapter or tax type. Filters: importer, office, tariff (HS) code, origin country. Use for 'top importers', 'imports from China', 'duty on HS 2523', 'which agent cleared the most', 'import VAT by month'. For ePayment customs payments by border post, use the customs payments lookup instead.")]
    public Task<string> GetCustomsDeclarations(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'imports', 'exports' or 'all'")] string flow = "imports",
        [Description("'total', 'day', 'month', 'office', 'importer', 'agent', 'originCountry', 'hsChapter' or 'taxType'")] string groupBy = "total",
        [Description("Importer TIN/code or name. Leave empty for all.")] string importer = "",
        [Description("Customs office code or name. Leave empty for all.")] string office = "",
        [Description("Tariff (HS) code or its first digits, e.g. '2523' or '87'. Leave empty for all.")] string hsCode = "",
        [Description("Country of origin, name or code (e.g. 'China', 'ZA'). Leave empty for all.")] string country = "",
        [Description("How many rows for rankings, 1 to 50")] int top = 15)
        => Run(nameof(GetCustomsDeclarations), new { fromDate, toDate, flow, groupBy, importer, office, hsCode, country, top }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.CustomsDeclarationsAsync(from, to, ParseEnum<CustomsFlow>(flow, nameof(flow)),
                ParseEnum<DeclarationGrouping>(groupBy, nameof(groupBy)), new DeclarationFilter(Blank(importer), Blank(office), Blank(hsCode), Blank(country)),
                Clamp(top, 1, 50), _ct);
        });

    [Description("Payments made through ePayment (bank payment applications for customs liabilities): amounts applied and paid, paid and cancelled applications, by day, month, bank, payment mode or taxpayer. Use for 'which bank handled the most payments', 'ePayment payments last month', 'cancelled payment applications'.")]
    public Task<string> GetEpaymentPayments(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'total', 'day', 'month', 'bank', 'mode' or 'taxpayer'")] string groupBy = "total",
        [Description("How many taxpayers for 'taxpayer', 1 to 50")] int top = 15)
        => Run(nameof(GetEpaymentPayments), new { fromDate, toDate, groupBy, top }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.EpaymentPaymentsAsync(from, to, ParseEnum<PaymentGrouping>(groupBy, nameof(groupBy)), Clamp(top, 1, 50), _ct);
        });

    [Description("The rental property survey: properties, units, average rent and whether owners declare rental income, by city, township, property type, use, occupancy, owner type or owner. Use for 'how many rental properties in Lilongwe', 'properties not declaring rental income', 'largest property owners'.")]
    public Task<string> GetRentalProperties(
        [Description("'city', 'township', 'type', 'use', 'occupancy', 'ownerType' or 'owner'")] string groupBy = "city",
        [Description("Only this city or township. Leave empty for all.")] string city = "",
        [Description("How many owners for 'owner', 1 to 50")] int top = 20)
        => Run(nameof(GetRentalProperties), new { groupBy, city, top }, () =>
            _queries.RentalPropertiesAsync(ParseEnum<PropertyGrouping>(groupBy, nameof(groupBy)), Blank(city), Clamp(top, 1, 50), _ct));

    // ---------------------------------------------------------------- helpers

    /// <summary>Turns a taxpayer (name or TIN) and a tax office (code, name or town) into a filter, or asks the user to choose.</summary>
    private async Task<InvoiceFilter> BuildFilterAsync(string? taxpayer, string? taxOffice, InvoiceKind kind)
    {
        IReadOnlyList<string>? tins = null;
        string? label = null;
        if (!string.IsNullOrWhiteSpace(taxpayer))
        {
            var text = taxpayer.Trim();
            if (text.Length < 3) throw new ToolArgumentException("Give a business name of at least 3 letters, or a TIN.");
            var matches = await _queries.FindTaxpayersAsync(text, _ct);
            if (matches.Count == 0)
            {
                if (text.All(char.IsDigit)) { tins = [text]; label = $"TIN {text}"; }   // a seller not in the register can still have invoices
                else throw new ToolArgumentException($"No registered taxpayer matches '{text}'. Ask the user to check the spelling or give the TIN.");
            }
            else if (matches.Count == 1 || text.All(char.IsDigit))
            {
                tins = matches.Select(m => m.Tin).ToList();
                label = $"{matches[0].Name} (TIN {matches[0].Tin})";
            }
            else
            {
                // Several taxpayers match: use one only if its name is exactly what was asked.
                var exact = matches.Where(m => string.Equals(m.Name.Trim(), text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 1) { tins = [exact[0].Tin]; label = $"{exact[0].Name} (TIN {exact[0].Tin})"; }
                else throw new ToolArgumentException($"Several taxpayers match '{text}': " +
                    string.Join("; ", matches.Select(m => $"{m.Name} (TIN {m.Tin})")) + ". Ask the user which one they mean, or use the TIN.");
            }
        }
        IReadOnlyList<string>? offices = string.IsNullOrWhiteSpace(taxOffice) ? null : ResolveOffices(taxOffice);
        if (offices is not null)
        {
            var officeLabel = string.Join(", ", offices.Select(o => _offices.Label(o)));
            label = label is null ? officeLabel : $"{label}, {officeLabel}";
        }
        return new InvoiceFilter(tins, offices, kind, label);
    }

    /// <summary>Tax office codes for a code, an official name or a town (e.g. "Zomba" gives ZOM and ZMTO).</summary>
    private IReadOnlyList<string> ResolveOffices(string text)
    {
        text = text.Trim();
        var byName = OfficesMatching(text);
        if (byName.Count > 0) return byName;
        if (Regex.IsMatch(text.ToUpperInvariant(), "^[A-Z0-9_.]{2,10}$")) return [text.ToUpperInvariant()];
        var known = _offices.Names.Count == 0 ? "" : " Known offices: " + string.Join(", ", _offices.Names.Select(kv => $"{kv.Value} ({kv.Key})")) + ".";
        throw new ToolArgumentException($"'{text}' does not match a tax office.{known} Ask the user which office they mean.");
    }

    private async Task<DateOnly> LatestInvoiceDateAsync()
    {
        try
        {
            var c = await _queries.CoverageAsync("invoices", _ct);
            if (c.Rows.Count > 0 && (c.Rows[0][2] ?? c.Rows[0][4]) is DateOnly d) return d < _clock.Today ? d : _clock.Today;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[TOOL] Could not read the latest invoice date");
        }
        return _clock.Today;
    }

    private static bool? YesNo(string? value, string name) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "any" or "all" => null,
        "yes" or "true" or "y" => true,
        "no" or "false" or "n" => false,
        _ => throw new ToolArgumentException($"{name} must be 'any', 'yes' or 'no'.")
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
