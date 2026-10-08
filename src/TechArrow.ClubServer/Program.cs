using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using TechArrow.ClubServer;
using TechArrow.GameUpdater.Core.Models;

var builder = WebApplication.CreateBuilder(args);
var adminKey = Environment.GetEnvironmentVariable("TECHARROW_ADMIN_KEY");
if (string.IsNullOrWhiteSpace(adminKey) || adminKey.Length < 32)
    throw new InvalidOperationException("Set TECHARROW_ADMIN_KEY to a random key of at least 32 characters. Never use a game account password.");
var adminHash = DeviceStore.Hash(adminKey);
var dataDirectory = Environment.GetEnvironmentVariable("TECHARROW_DATA_DIR") ?? Path.Combine(builder.Environment.ContentRootPath, "Data");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
builder.Services.AddSingleton(new DeviceStore(dataDirectory));
builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 263)
    { context.Response.StatusCode = 401; return; }
    var token = authorization[7..];
    var store = context.RequestServices.GetRequiredService<DeviceStore>();
    var allowed = context.Request.Path == "/api/heartbeat"
        ? store.IsDeviceToken(token) : DeviceStore.Matches(token, adminHash);
    if (!allowed) { context.Response.StatusCode = 401; return; }
    context.Items["deviceToken"] = token;
    await next();
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { Status = "ok" }));
app.MapGet("/api/devices", (DeviceStore store) => Results.Ok(store.Snapshot()));
app.MapPost("/api/devices", (ClubDeviceRegistration registration, DeviceStore store) =>
{
    if (string.IsNullOrWhiteSpace(registration.Club) || registration.Club.Length > 80 ||
        string.IsNullOrWhiteSpace(registration.Computer) || registration.Computer.Length > 80) return Results.BadRequest();
    var device = store.Register(registration);
    return Results.Ok(new { device.Id, device.Token });
});
app.MapPost("/api/devices/{id:guid}/revoke", (Guid id, DeviceStore store) =>
    store.Revoke(id) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/heartbeat", (ClubHeartbeat heartbeat, HttpContext context, DeviceStore store) =>
{
    bool Text(string? value, int max) => value is not null && value.Length <= max;
    if (!Text(heartbeat.Version, 32) || !Text(heartbeat.MonitorStatus, 120) ||
        heartbeat.Launchers is null || heartbeat.Launchers.Count > 8 ||
        heartbeat.Launchers.Any(item => item is null || !Text(item.Name, 80) || !Text(item.Status, 120)) ||
        heartbeat.Runs is null || heartbeat.Runs.Count > 10 ||
        heartbeat.Runs.Any(item => item is null || !Text(item.Kind, 80) || !Text(item.Result, 120))) return Results.BadRequest();
    return store.Heartbeat((string)context.Items["deviceToken"]!, heartbeat) ? Results.Ok() : Results.Unauthorized();
});
app.Run();
