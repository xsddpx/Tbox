using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Tbox.Cloud;
using Tbox.Core.Accounts;

var builder = WebApplication.CreateBuilder(args);
var options = CloudOptions.Load(builder.Configuration);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.Database));
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<DeviceSessions>();
builder.Services.AddSingleton<DeviceEvents>();
builder.Services.AddSingleton<RemoteAccess>();
builder.Services.AddSingleton<GitHubLogin>();
builder.Services.AddSingleton<Preferences>();
builder.Services.AddSingleton<Captures>();
builder.Services.AddHostedService<CaptureRetention>();
var protection = builder.Services.AddDataProtection().SetApplicationName("Tbox.Cloud");
if (builder.Configuration["Cloud:KeyDirectory"] is { Length: > 0 } directory)
    protection.PersistKeysToFileSystem(Directory.CreateDirectory(directory));
builder.Services.AddHttpClient("GitHub", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Tbox.Cloud");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddFixedWindowLimiter("login", policy =>
    {
        policy.PermitLimit = 100;
        policy.Window = TimeSpan.FromMinutes(1);
        policy.QueueLimit = 0;
    });
    limiter.AddConcurrencyLimiter("captures", policy => { policy.PermitLimit = 4; policy.QueueLimit = 0; });
});

var app = builder.Build();
await app.Services.GetRequiredService<Database>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
app.UseExceptionHandler(error => error.Run(context =>
{
    app.Logger.LogError("A cloud operation failed.");
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    return Task.CompletedTask;
}));
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok());
var auth = app.MapGroup("/v1/auth").RequireRateLimiting("login");
auth.MapGet("/start", (HttpContext context, int port, string challenge, string state, GitHubLogin login) =>
    login.Start(context, port, challenge, state));
auth.MapGet("/github/callback", (HttpContext context, string? code, string? state, GitHubLogin login, CancellationToken ct) =>
    login.CompleteAsync(context, code, state, ct));
auth.MapPost("/exchange", async (ExchangeRequest request, DeviceSessions sessions, CancellationToken ct) =>
    await sessions.ExchangeAsync(request, ct) is { } session ? Results.Ok(session) : Results.Unauthorized());
auth.MapPost("/refresh", async (RefreshRequest request, DeviceSessions sessions, CancellationToken ct) =>
    await sessions.RefreshAsync(request, ct) is { } session ? Results.Ok(session) : Results.Unauthorized());
auth.MapPost("/signout", async (RefreshRequest request, DeviceSessions sessions, DeviceEvents events, CancellationToken ct) =>
{
    if (await sessions.EndSessionAsync(request, ct)) events.Revoke(request.DeviceId);
    return Results.NoContent();
});

var account = app.MapGroup("/v1").AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var device = await http.RequestServices.GetRequiredService<DeviceSessions>().AuthenticateAsync(http, http.RequestAborted);
    if (device is null) return Results.Unauthorized();
    http.Items[typeof(CloudDevice)] = device.Value;
    return await next(context);
});
account.MapGet("/devices", (DeviceSessions sessions, CancellationToken ct) => sessions.ListAsync(ct));
account.MapGet("/devices/events", (HttpContext context, DeviceEvents events, DeviceSessions sessions, CancellationToken ct) =>
    events.WatchAsync((Guid)context.Items[typeof(CloudDevice)]!, context, sessions, ct));
account.MapDelete("/devices/{id:guid}", async (Guid id, DeviceSessions sessions, DeviceEvents events, CancellationToken ct) =>
{
    if (await sessions.RevokeAsync(id, ct)) events.Revoke(id);
    return Results.NoContent();
});

var remote = account.MapGroup("/remote");
remote.MapGet("/hosts", (HttpContext context, RemoteAccess access, CancellationToken ct) =>
    access.ListHostsAsync((Guid)context.Items[typeof(CloudDevice)]!, ct));
remote.MapPut("/hosts/current", async (RemoteHostRegistration request, HttpContext context, RemoteAccess access, CancellationToken ct) =>
    await access.RegisterHostAsync((Guid)context.Items[typeof(CloudDevice)]!, request, ct)
        ? Results.NoContent() : Results.BadRequest());
remote.MapDelete("/hosts/current", async (HttpContext context, RemoteAccess access, CancellationToken ct) =>
{
    await access.DisableHostAsync((Guid)context.Items[typeof(CloudDevice)]!, ct);
    return Results.NoContent();
});
remote.MapPost("/sessions", async (RemoteConnectionRequest request, HttpContext context, RemoteAccess access, CancellationToken ct) =>
    await access.InviteAsync((Guid)context.Items[typeof(CloudDevice)]!, request.HostId, ct) is { } offer
        ? Results.Ok(offer) : Results.Conflict());

var host = app.MapGroup("/v1/remote/host");
host.MapPost("/heartbeat", async (HttpContext context, RemoteAccess access, CancellationToken ct) =>
    DeviceSessions.ReadBearer(context) is { } token && await access.HeartbeatAsync(token, ct)
        ? Results.NoContent() : Results.Unauthorized());
host.MapPost("/claim", async (RemoteClaim request, HttpContext context, RemoteAccess access, CancellationToken ct) =>
    DeviceSessions.ReadBearer(context) is { } token && await access.ClaimAsync(token, request, ct) is { } lease
        ? Results.Ok(lease) : Results.Unauthorized());
host.MapPost("/sessions/{id:guid}/renew", async (Guid id, HttpContext context, RemoteAccess access, CancellationToken ct) =>
    DeviceSessions.ReadBearer(context) is { } token && await access.RenewAsync(token, id, ct) is { } lease
        ? Results.Ok(lease) : Results.Unauthorized());

account.MapGet("/preferences", (Preferences preferences, CancellationToken ct) => preferences.ReadAsync(ct));
account.MapPost("/preferences/initial", async (Dictionary<string, string> input, Preferences preferences, CancellationToken ct) =>
    await preferences.InitializeAsync(input, ct) is { } initialized ? Results.Ok(initialized) : Results.BadRequest());
account.MapPatch("/preferences", async (PreferenceEdit edit, Preferences preferences, CancellationToken ct) =>
    await preferences.ChangeAsync(edit, ct) is { } changed ? Results.Ok(changed) : Results.BadRequest());

var pictures = account.MapGroup("/captures").RequireRateLimiting("captures");
pictures.MapGet("/", (Captures captures, CancellationToken ct) => captures.ListAsync(ct));
pictures.MapPut("/{id:guid}", async (Guid id, HttpContext context, Captures captures, CancellationToken ct) =>
{
    var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = CloudProtocol.MaximumCaptureBytes;
    if (id == Guid.Empty || context.Request.ContentLength is not (> 0 and <= CloudProtocol.MaximumCaptureBytes) ||
        context.Request.ContentType != "image/png") return Results.BadRequest();

    var now = DateTimeOffset.UtcNow;
    if (!long.TryParse(context.Request.Headers["X-Tbox-Captured-At"], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
        timestamp < now.AddDays(-CloudProtocol.HistoryDays).ToUnixTimeSeconds() || timestamp > now.AddMinutes(5).ToUnixTimeSeconds())
        return Results.BadRequest();
    var created = DateTimeOffset.FromUnixTimeSeconds(Math.Min(timestamp, now.ToUnixTimeSeconds()));
    var png = new byte[(int)context.Request.ContentLength.Value];
    await context.Request.Body.ReadExactlyAsync(png, ct);
    if (!Captures.IsPng(png)) return Results.BadRequest();
    await captures.SaveAsync(id, created, png, ct);
    return Results.NoContent();
});
pictures.MapGet("/{id:guid}", async (Guid id, Captures captures, CancellationToken ct) =>
    await captures.ReadAsync(id, ct) is { } image ? Results.Bytes(image, "image/png") : Results.NotFound());
pictures.MapDelete("/{id:guid}", async (Guid id, Captures captures, CancellationToken ct) =>
{
    await captures.DeleteAsync(id, ct);
    return Results.NoContent();
});

await app.RunAsync();
