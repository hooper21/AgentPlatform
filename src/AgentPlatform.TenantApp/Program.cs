using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.DependencyInjection;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Seeding;
using AgentPlatform.TenantApp.Components;
using AgentPlatform.TenantApp.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPlatformInfrastructure(PlatformInfrastructureOptions.FromEnvironment());

// ASP.NET Identity (Identity Core из shared framework, без новых пакетов):
// пользователи — существующая таблица users, пароль — PBKDF2, роли admin/operator/viewer.
builder.Services
    .AddIdentityCore<User>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddUserStore<PlatformUserStore>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<PlatformUserClaimsPrincipalFactory>();

builder.Services.AddScoped<IPasswordHasher<User>, PlatformIdentityPasswordHasher>();

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/api/cabinet/logout";
    options.AccessDeniedPath = "/access-denied";
    options.Cookie.Name = "agentplatform.tenant";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, PlatformAuthenticationStateProvider>();

// Диалоги/агенты/коннекторы кабинета выполняются Controller API.
var controllerBaseUrl = builder.Configuration["Controller:BaseUrl"] ?? "http://localhost:5265";
builder.Services.AddHttpClient<ControllerApiClient>(client =>
{
    client.BaseAddress = new Uri(controllerBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(2);
});

// Знания: кабинет работает с реестром провайдеров и источниками через API
// Knowledge Service (tenant — заголовком X-Tenant-Id, v1-заглушка).
var knowledgeBaseUrl = builder.Configuration["Knowledge:BaseUrl"] ?? "http://localhost:5281";
builder.Services.AddHttpClient<KnowledgeApiClient>(client =>
{
    client.BaseAddress = new Uri(knowledgeBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// dotnet run -- --migrate --seed
if (await DataMaintenance.TryRunAsync(app.Services, args))
{
    return;
}

app.UseStaticFiles();
app.UseAuthentication();

// Tenant-изоляция: сначала claim tenant_id пользователя, заголовок — только dev-фолбэк.
app.UseMiddleware<TenantHeaderMiddleware>();

app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "tenant-app", DateTimeOffset.UtcNow)));

// Вход/выход — обычный POST формы: cookie Identity выставляется HTTP-ответом
// (интерактивный Blazor circuit не может менять заголовки после старта).
app.MapPost("/api/cabinet/login", async (
    HttpContext http,
    SignInManager<User> signInManager,
    IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect("/login?error=antiforgery");
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var email = form["email"].ToString().Trim();
    var password = form["password"].ToString();
    var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
    {
        return Results.Redirect("/login?error=required");
    }

    var result = await signInManager.PasswordSignInAsync(
        email,
        password,
        isPersistent: true,
        lockoutOnFailure: false);

    return result.Succeeded
        ? Results.Redirect(returnUrl)
        : Results.Redirect("/login?error=invalid");
});

app.MapPost("/api/cabinet/logout", async (
    HttpContext http,
    SignInManager<User> signInManager,
    IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect("/login");
    }

    await signInManager.SignOutAsync();
    return Results.Redirect("/login");
});

// Совместимость v1: прямые чтения из БД для smoke-проверок (кабинет ходит в Controller).
app.MapGet("/api/v1/agents", async (ITenantScopedRepository<Agent> agents, CancellationToken cancellationToken) =>
{
    var list = await agents.ListAsync(cancellationToken);
    return Results.Ok(list.Select(agent => new
    {
        agent.Id,
        agent.Name,
        agent.CurrentVersion
    }));
});

app.MapGet("/api/v1/approvals/pending", async (
    ITenantScopedRepository<Approval> approvals,
    CancellationToken cancellationToken) =>
{
    var pendingStatus = ApprovalStatus.Pending.ToDbValue();
    var pending = await approvals.Query
        .Where(approval => approval.Status == pendingStatus)
        .ToListAsync(cancellationToken);

    return Results.Ok(pending.Select(approval => new
    {
        approval.Id,
        approval.SessionId,
        approval.RequestedAt,
        approval.ExpiresAt
    }));
});

app.MapGet("/api/v1/session-context", (ITenantContext tenantContext) =>
    Results.Ok(new { tenantId = tenantContext.TenantId, resolved = tenantContext.IsResolved }));

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

// Только локальные пути: открытый редирект после логина запрещён.
static string NormalizeReturnUrl(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)
        || !raw.StartsWith('/')
        || raw.StartsWith("//", StringComparison.Ordinal))
    {
        return "/";
    }

    return raw;
}
