# MRA Reporting Assistant (v0.1)

A chat assistant over the EIS database. Staff ask questions in plain English and get answers,
tables, charts and reports. A local AI model (Qwen3 4B on llama.cpp) chooses which vetted query
to run; C# runs the query and draws the table or chart. The model never writes SQL and never
supplies a figure itself.

Everything used is free: .NET, llama.cpp (MIT), Qwen3 (Apache 2.0), Chart.js (MIT),
ScottPlot (MIT), ClosedXML (MIT), PDFsharp/MigraDoc (MIT).

## What works in this version

| Area | What you get |
| --- | --- |
| Chat | Streamed answers, the query that ran shown under each answer, multi-turn follow-ups |
| Questions it can answer | National sales and VAT (by day, month or total), VAT by tax rate, top taxpayers, taxpayer lookup by TIN ("is this TIN onboarded?"), failed submissions, red flags, recalled invoices, void requests, tampered terminals |
| Visuals | Line, bar and pie charts and sortable tables in the browser; Excel download of any result |
| Report | National daily summary as PDF or Excel: headline figures, 30-day trend, VAT by rate, top 10 taxpayers, red flags, failed submissions, and a short AI-written summary |
| Audit | Every question, query, export and report is logged: to `AI-REPORTING.dbo.AuditLog` once that exists, and to `logs/audit-*.jsonl` until then |

**Not in this version yet** (waiting on answers, see the project document):
station-level figures and station access rules (what "station" means), tax-rate and status
names (code lists from the EIS team), the summary table for production-size data, and
scheduled reports.

## Setup

### 1. .NET SDK

```powershell
dotnet --version
```

The project targets .NET 10. If this shows 8.x, open `src/MraReporting.Web/MraReporting.Web.csproj`
and change `net10.0` to `net8.0` and the Negotiate package version from `10.*` to `8.*`.

### 2. The model server

Follow the notes at the top of `scripts/start-llama-server.ps1` (download llama.cpp and the
Qwen3 4B model once), then:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-llama-server.ps1
```

Leave that window open. Check `http://127.0.0.1:8080/v1/models` responds.

### 3. Database connection

`src/MraReporting.Web/appsettings.json` → `ConnectionStrings:Staging` points at
`STAGING_SSIS` on 10.10.1.40 using your Windows login. Connect to the MRA network/VPN first.
Leave `AiReporting` empty until the DBA gives access to the AI-REPORTING database and you have
run `database/01_ai_reporting_setup.sql`.

### 4. Build and run

```powershell
cd src\MraReporting.Web
dotnet restore
dotnet build
dotnet run
```

Open `http://localhost:5080`.

The first `dotnet restore` downloads the NuGet packages. Package versions float to the latest
release; once the build works, note the versions in `obj/project.assets.json` (or run
`dotnet list package`) and pin them in the .csproj.

## Try these questions

1. How much VAT was collected yesterday?
2. Show gross sales by day for the last 14 days
3. And for last month by day? *(follow-up: should reuse the same query with new dates)*
4. Who were the top 10 taxpayers by VAT last month?
5. Is TIN 30261177 registered in EIS?
6. Show red flags by type this month
7. Which terminals have tamper attempts?
8. How much did Songwe collect today? *(should say station figures are not available yet)*
9. What's the weather in Lilongwe? *(should not call any tool)*
10. Delete yesterday's invoices *(should refuse)*

The console shows a `[TOOL]` line for every query that really ran. If an answer contains a
number but there is no `[TOOL]` line for it, the model made it up: note the question.

## How it fits together

```
Browser (Pages/Index.cshtml, wwwroot/js/chat.js, Chart.js)
   │  POST /api/chat  ← streamed lines: tool / result / delta / done
   ▼
ChatService ── system prompt with today's dates ──► llama-server (Qwen3 4B)
   │                                                   │ picks a tool + arguments
   ▼                                                   ▼
ReportingTools (validates arguments) ──► EisQueries (all SQL) ──► SqlRunner ──► STAGING_SSIS
   │ full rows → browser                      short summary → model
   ▼
AuditLogger → AI-REPORTING.dbo.AuditLog (or logs/*.jsonl)

GET /api/reports/daily-summary → DailySummaryReport → ChartRenderer (ScottPlot)
                                                    → PdfWriter (MigraDoc) / ExcelWriter (ClosedXML)
```

| Folder | Contents |
| --- | --- |
| `src/MraReporting.Web/Data` | `EisQueries.cs` (every SQL statement), `SqlRunner.cs`, `QueryResult.cs` |
| `src/MraReporting.Web/Ai` | Tools the model can call, system prompt, chat loop, think-tag filter |
| `src/MraReporting.Web/Reports` | Daily summary, chart images, Excel and PDF writers |
| `src/MraReporting.Web/Audit` | Audit logging |
| `src/MraReporting.Web/Endpoints` | `/api/chat`, `/api/export/xlsx`, `/api/reports/daily-summary` |
| `database` | AI-REPORTING setup, requests for the DBA, discovery queries |
| `scripts` | llama-server start script |

### Adding a new question type

1. Add a method to `Data/EisQueries.cs` that returns a `QueryResult` (set the visual and chart columns).
2. Add a matching method to `Ai/ReportingTools.cs` with a short `[Description]`, and add it to `AsTools()`.
3. Add a test question to the list above.

Keep the tool list short: a 4B model chooses more reliably from fewer, clearly described tools.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| "The AI model server could not be reached" | llama-server is not running, or is on another port than `Llm:Endpoint` |
| Answers describe a tool instead of calling it | llama-server was started without `--jinja` |
| Replies start with `<think>` text | `/no_think` missing from the system prompt; the filter should hide it anyway |
| "A network-related or instance-specific error" | Not on the MRA network/VPN, or the server name is wrong |
| "The certificate chain was issued by an authority that is not trusted" | Keep `TrustServerCertificate=true` in the connection string |
| Questions over long date ranges time out | No date index on `Invoices` yet: see `database/02_requests_for_dba.sql` |
| PDF fails with a font error | PDFsharp cannot find fonts; run on Windows, or tell me the error text |
