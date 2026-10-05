using System.ClientModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MraReporting.Ai;
using MraReporting.Audit;
using MraReporting.Auth;
using MraReporting.Data;
using MraReporting.Endpoints;
using MraReporting.Infrastructure;
using MraReporting.Reports;
using OpenAI;

// Account commands (add-user, reset-password, list-users ...) run and exit without starting the web app.
if (UserAdmin.IsCommand(args))
{
    Environment.ExitCode = UserAdmin.Run(args, Directory.GetCurrentDirectory());
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));

// Sign-in. Auth:Mode decides how people prove who they are:
//   "Local"   (default) the app's own accounts and sign-in page, managed with dotnet run -- add-user ...
//   "Windows" MRA network sign-in (Active Directory, Kerberos/NTLM), no password page needed
//   "Off"     no sign-in at all; only for a developer's own machine
var authMode = (builder.Configuration["Auth:Mode"] ?? "Local").Trim().ToLowerInvariant();
if (authMode is not ("local" or "windows" or "off")) authMode = "local"; // e.g. the old "Development" value
var useWindowsAuth = authMode == "windows";
var useLocalAuth = authMode == "local";

builder.Services.AddSingleton(new LocalUserStore(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<SignInGuard>();

if (useWindowsAuth)
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);
}
else if (useLocalAuth)
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/login";
            o.LogoutPath = "/logout";
            o.ReturnUrlParameter = "ReturnUrl";
            o.ExpireTimeSpan = TimeSpan.FromHours(8);   // signed out after 8 hours without use
            o.SlidingExpiration = true;
            o.Cookie.Name = "mra.reporting.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            // Background requests from the page (chat, exports) get 401 instead of the sign-in page,
            // so the browser can send the user to sign in again cleanly.
            // Every time the app is started again, everyone must sign in again: a sign-in from an
            // earlier run of the app is rejected, even if the browser still has its cookie.
            o.Events.OnValidatePrincipal = async ctx =>
            {
                if (ctx.Principal?.FindFirst(AppClaims.AppRun)?.Value != AppClaims.CurrentRun)
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
            o.Events.OnRedirectToLogin = ctx =>
            {
                // (report downloads are normal page navigations, so they still go to the sign-in page)
                if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/reports"))
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else
                    ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
        });
    builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);
}

builder.Services.AddRazorPages();

builder.Services.AddSingleton<AppClock>();
builder.Services.AddSingleton<UserContextAccessor>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<SqlRunner>();
builder.Services.AddSingleton<StationDirectory>();
builder.Services.AddSingleton<OfficeDirectory>();
builder.Services.AddSingleton<RateDirectory>();
builder.Services.AddSingleton<EisQueries>();
builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<DailySummaryReport>();

// The local model: llama-server speaks the OpenAI API, so the standard OpenAI client is
// pointed at it. Swapping to a bigger model or another server is a change to appsettings.json.
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    var openAi = new OpenAIClient(
        new ApiKeyCredential("not-needed"), // llama-server ignores the key
        new OpenAIClientOptions
        {
            Endpoint = new Uri(llm.Endpoint),
            NetworkTimeout = TimeSpan.FromMinutes(5), // CPU inference can be slow
        });

    return openAi.GetChatClient(llm.Model)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 4)
        .Build(sp);
});

var app = builder.Build();

app.UseStaticFiles();   // styles, scripts and fonts are served before sign-in, so the sign-in page looks right

// Pages and data must not be kept by the browser: after signing out, the Back button then asks the
// server again (and gets the sign-in page) instead of showing the app from the browser's memory.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers.Pragma = "no-cache";
        return Task.CompletedTask;
    });
    await next();
});
if (useWindowsAuth || useLocalAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapRazorPages();
app.MapApiEndpoints();
if (useLocalAuth)
{
    app.MapPost("/logout", async (HttpContext http, AuditLogger audit, AppClock clock) =>
    {
        var name = http.User.Identity?.Name;
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (name is not null)
            await audit.WriteAsync(new AuditEntry(clock.Now, name, "sign-out", "Signed out", null, 0, 0, null, null));
        return Results.Redirect("/login?SignedOut=true");
    }).AllowAnonymous();

    var accounts = app.Services.GetRequiredService<LocalUserStore>();
    if (accounts.Count == 0)
        app.Logger.LogWarning("No sign-in accounts exist yet. In the project folder run: dotnet run -- add-user yourname");
}

app.Logger.LogInformation("MRA AI Reporting started. Model endpoint: {Endpoint}. Auth mode: {Mode}.",
    app.Services.GetRequiredService<IOptions<LlmOptions>>().Value.Endpoint,
    useWindowsAuth ? "Windows (MRA network sign-in)" : useLocalAuth ? "Local accounts" : "Off (no sign-in)");

// Warm the model up in the background, so the first question is not the slow one.
// If llama-server is not up yet, try again every 15 seconds for up to 5 minutes.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    var chat = app.Services.GetRequiredService<ChatService>();
    for (var attempt = 1; attempt <= 20 && !stopping.IsCancellationRequested; attempt++)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await chat.WarmUpAsync(stopping);
            app.Logger.LogInformation("AI model warmed up in {Seconds:0.0} s. Questions will now get a faster first answer.",
                watch.Elapsed.TotalSeconds);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (attempt == 1)
                app.Logger.LogWarning("AI model not reachable yet ({Message}). Will keep trying for 5 minutes; start llama-server if it is not running.", ex.Message);
            try { await Task.Delay(TimeSpan.FromSeconds(15), stopping); } catch (OperationCanceledException) { return; }
        }
    }
}));

app.Run();
