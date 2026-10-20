using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FC;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConfigurationStore>();
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<ForumBrowser>();
builder.Services.AddSingleton<EmailAlerts>();
builder.Services.AddHostedService<UpWorker>();

var app = builder.Build();

// El puerto de Docker se expone SOLO en 127.0.0.1:5088 (ver compose.yaml).
// Esta autenticación añade una segunda capa. Nunca introducir contraseña de FC
// o credenciales SMTP en el repositorio público.
var password = Environment.GetEnvironmentVariable("FC_ADMIN_PASSWORD") ?? "";
if (password.Length < 16)
    throw new InvalidOperationException("Configura FC_ADMIN_PASSWORD (16 caracteres o más) en .env.");
var expected = SHA256.HashData(Encoding.UTF8.GetBytes(password));

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store";
    if (ctx.Request.Path == "/healthz")
    {
        await next();
        return;
    }

    bool authorized = false;
    var authorization = ctx.Request.Headers.Authorization.ToString();
    if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..]));
            int colon = credentials.IndexOf(':');
            if (colon >= 0 && credentials[..colon] == "admin")
            {
                var actual = SHA256.HashData(Encoding.UTF8.GetBytes(credentials[(colon + 1)..]));
                authorized = CryptographicOperations.FixedTimeEquals(expected, actual);
            }
        }
        catch (FormatException) { }
    }
    if (!authorized)
    {
        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"FC panel\", charset=\"UTF-8\"";
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Autenticación requerida.");
        return;
    }
    // Evitar que un formulario de otra web active escrituras mientras
    // el navegador conserve el usuario/contraseña Basic del panel.
    if (ctx.Request.Path.StartsWithSegments("/api")
        && (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method))
        && ctx.Request.Headers["X-FC-Action"].ToString() != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("Falta cabecera de autorización de acción.");
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
app.MapGet("/api/config", (ConfigurationStore config) => Results.Ok(config.Read()));
app.MapPut("/api/config", (BotSettings updated, ConfigurationStore config) =>
{
    try
    {
        config.Save(updated);
        return Results.Ok(new { saved = true });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/status", (RuntimeState status) => Results.Ok(status.Snapshot()));

app.MapPost("/api/resume", (ResumeRequest request, ConfigurationStore config, RuntimeState state) =>
{
    if (!config.Read().Threads.Any(t => string.Equals(t.Url, request.Url, StringComparison.OrdinalIgnoreCase))
        || !state.Threads.TryGetValue(request.Url, out var thread))
        return Results.NotFound();
    thread.PausedForReview = false;
    thread.LastError = null;
    thread.NextCheck = DateTimeOffset.UtcNow.AddMinutes(Math.Max(thread.WaitMinutes, 1));
    thread.State = "Reprogramado manualmente";
    return Results.Ok(new { resumed = true });
});

// Prueba puntual: se permite publicar un único Up real con la automatización
// apagada, en un hilo elegido explícitamente; no se borra ningún mensaje.
app.MapPost("/api/test-up", async (ProbeRequest request, ConfigurationStore config,
    ForumBrowser browser, RuntimeState runtime, EmailAlerts alerts, CancellationToken ct) =>
{
    var settings = config.Read();
    if (settings.Enabled)
        return Results.Conflict(new { error = "Desactiva y guarda la automatización antes de probar un Up manual." });
    if (!ConfigurationStore.IsForumThreadUrl(request.Url))
        return Results.BadRequest(new { error = "La URL debe ser HTTPS de ForoCoches." });
    if (string.IsNullOrWhiteSpace(settings.ForumUsername))
        return Results.BadRequest(new { error = "Guarda antes tu nombre de usuario de ForoCoches." });

    try
    {
        var posted = await browser.PostTestUpAsync(request.Url, settings.ForumUsername,
            settings.TimeZone, ct);
        runtime.LoginState = "Conectada";
        await alerts.AuthenticatedAsync();
        return Results.Ok(new
        {
            success = true,
            postId = posted.Id,
            author = posted.Author,
            text = posted.Text,
            url = request.Url,
            detail = "Up de prueba publicado y verificado."
        });
    }
    catch (SessionExpiredException)
    {
        runtime.LoginState = "Caducada";
        await alerts.LostAsync(ct);
        return Results.Conflict(new { error = "La sesión ha caducado. Inicia sesión de nuevo desde el panel." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError(ex, "Error o resultado incierto de la prueba manual de Up.");
        return Results.Problem("Ha fallado la prueba. Comprueba el hilo real ANTES de volver a pulsar.");
    }
});

app.MapPost("/api/login", async (LoginRequest request, ConfigurationStore config,
    ForumBrowser browser, RuntimeState runtime, EmailAlerts alerts, CancellationToken ct) =>
{
    var settings = config.Read();
    if (string.IsNullOrWhiteSpace(settings.ForumUsername)
        || !string.Equals(request.Username, settings.ForumUsername, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "El usuario debe coincidir con ForumUsername de la configuración." });
    try
    {
        var response = await browser.LoginAsync(request.Username, request.Password,
            settings.Threads.FirstOrDefault()?.Url, ct);
        if (response.Success)
        {
            runtime.LoginState = "Conectada";
            await alerts.AuthenticatedAsync();
        }
        return Results.Ok(response);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError(ex, "Error de login");
        return Results.Problem("No se pudo completar el login. Revisa los logs del contenedor.");
    }
});

app.MapPost("/api/probe", async (ProbeRequest request, ConfigurationStore config,
    ForumBrowser browser, RuntimeState runtime, EmailAlerts alerts, CancellationToken ct) =>
{
    if (!ConfigurationStore.IsForumThreadUrl(request.Url))
        return Results.BadRequest(new { error = "URL HTTPS de ForoCoches inválida." });
    try
    {
        var post = await browser.InspectAsync(request.Url, config.Read().TimeZone, ct);
        runtime.LoginState = "Conectada";
        await alerts.AuthenticatedAsync();
        return Results.Ok(post);
    }
    catch (SessionExpiredException)
    {
        runtime.LoginState = "Caducada";
        await alerts.LostAsync(ct);
        return Results.Conflict(new { error = "Sesión de ForoCoches caducada. Vuelve a iniciar sesión desde el panel." });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogWarning(ex, "No se pudo inspeccionar la URL");
        return Results.Problem("No se pudo leer el hilo con los selectores actuales.");
    }
});

app.Run();

public sealed record LoginRequest(string Username, string Password);
public sealed record ProbeRequest(string Url);
public sealed record ResumeRequest(string Url);
