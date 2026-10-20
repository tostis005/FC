using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace FC;

public sealed class SessionExpiredException : Exception
{
    public SessionExpiredException() : base("La web solicita iniciar sesión en ForoCoches.") { }
}

public sealed class SecurityChallengeException : Exception
{
    public SecurityChallengeException() : base("ForoCoches muestra una verificación de seguridad de Cloudflare. Es necesario resolver el acceso manualmente; no se ha intentado publicar.") { }
}

public sealed class UncertainWriteException : Exception
{
    public UncertainWriteException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ForumPost
{
    public string Id { get; set; } = "";
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public string RawDate { get; set; } = "";
    public DateTimeOffset? PostedAt { get; set; }
}

public sealed class LoginResult
{
    public bool Success { get; set; }
    public string Detail { get; set; } = "";
}

public sealed partial class ForumBrowser : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ForumBrowser> _log;
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private readonly string _profile;

    public ForumBrowser(ILogger<ForumBrowser> log)
    {
        _log = log;
        var dataDir = Environment.GetEnvironmentVariable("FC_DATA_DIR") ?? "/data";
        Directory.CreateDirectory(dataDir);
        _profile = Path.Combine(dataDir, "chromium");
    }

    private async Task<IBrowserContext> ContextAsync()
    {
        if (_context != null) return _context; // Todos los llamantes mantienen _gate.
        _playwright ??= await Playwright.CreateAsync();
        var headless = string.Equals(
            Environment.GetEnvironmentVariable("FC_CHROMIUM_HEADLESS"),
            "true", StringComparison.OrdinalIgnoreCase);
        _log.LogInformation("Iniciando Chromium {Mode} con perfil persistente.",
            headless ? "headless" : "gráfico (Xvfb)");
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(_profile,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                AcceptDownloads = false,
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                Args = new[] { "--disable-dev-shm-usage" }
            });
        _context.SetDefaultTimeout(15000);
        _context.SetDefaultNavigationTimeout(30000);
        return _context;
    }

    private async Task ResetContextAsync()
    {
        if (_context != null)
        {
            try { await _context.CloseAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "El contexto Chromium anterior ya se había cerrado."); }
            _context = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        _manualPage = null;
    }

    // Solo se reintenta abrir una pestaña, antes de cualquier operación de escritura.
    private async Task<IPage> OpenPageAsync(Func<bool>? acceptDeletion = null)
    {
        try { return await NewPageAsync(await ContextAsync(), acceptDeletion); }
        catch (PlaywrightException)
        {
            await ResetContextAsync();
            return await NewPageAsync(await ContextAsync(), acceptDeletion);
        }
    }

    private static async Task<IPage> NewPageAsync(IBrowserContext context, Func<bool>? acceptDeletion = null)
    {
        var page = await context.NewPageAsync();
        page.Dialog += async (_, dialog) =>
        {
            try
            {
                // La alerta de MP no debe bloquear la navegación.
                // Solo confirmar un diálogo durante una eliminación explícita.
                if (acceptDeletion?.Invoke() == true && dialog.Type == "confirm")
                    await dialog.AcceptAsync();
                else
                    await dialog.DismissAsync();
            }
            catch { /* El diálogo ya puede haber desaparecido al cerrar la página. */ }
        };
        return page;
    }

    // Comprobación local (about:blank), sin acceder a ForoCoches ni modificar
    // cookies. Sirve también para probar el arranque gráfico en GitHub Actions.
    public async Task<BrowserHealthResult> CheckBrowserAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = await OpenPageAsync();
            try
            {
                // Chromium gráfico puede no pintar about:blank hasta que exista
                // contenido: renderizar un documento local, sin peticiones externas.
                await page.SetContentAsync(
                    "<!doctype html><html><body style='background:#fff;color:#222'>" +
                    "<h1 id='browser-ready'>FC - Chromium gráfico activo</h1>" +
                    "</body></html>",
                    new PageSetContentOptions { WaitUntil = WaitUntilState.Load });
                await page.Locator("#browser-ready").WaitForAsync();

                var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    Timeout = 15000
                });
                if (bytes.Length == 0)
                    throw new InvalidOperationException("Chromium no ha podido generar la captura.");

                var headless = string.Equals(
                    Environment.GetEnvironmentVariable("FC_CHROMIUM_HEADLESS"),
                    "true", StringComparison.OrdinalIgnoreCase);
                return new BrowserHealthResult(
                    true, headless ? "headless" : "gráfico (Xvfb)",
                    "Chromium ha abierto una pestaña y ha realizado una captura sin visitar ForoCoches.");
            }
            finally { await page.CloseAsync(); }
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginResult> LoginAsync(string username, string password, string? probeThread, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return new LoginResult { Detail = "Introduce usuario y contraseña." };
        await _gate.WaitAsync(ct);
        try
        {
            var page = await OpenPageAsync();
            try
            {
                await page.GotoAsync("https://www.forocoches.com/foro/login.php?do=login",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                if (await IsSecurityChallengeAsync(page))
                    return new LoginResult { Detail = "Cloudflare bloquea la página antes de mostrar el login. No es un error de usuario o contraseña." };
                var user = page.Locator("input[name='vb_login_username'], input[name='username'], input[autocomplete='username']").First;
                var pass = page.Locator("input[name='vb_login_password'], input[name='password'], input[type='password']").First;
                if (await user.CountAsync() == 0 || await pass.CountAsync() == 0)
                    return new LoginResult { Detail = "No aparece el formulario de login conocido. Habrá que revisar la página real." };
                await user.FillAsync(username);
                await pass.FillAsync(password);
                await pass.PressAsync("Enter");
                await page.WaitForTimeoutAsync(1500);
                if (await IsSecurityChallengeAsync(page))
                    return new LoginResult { Detail = "Cloudflare solicita una verificación de seguridad. Debes resolverla manualmente antes de iniciar sesión." };
                if (await IsLoggedInAsync(page))
                    return new LoginResult { Success = true, Detail = "Sesión iniciada correctamente." };

                if (!string.IsNullOrWhiteSpace(probeThread) && ConfigurationStore.IsForumThreadUrl(probeThread))
                {
                    await page.GotoAsync(probeThread, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    if (await page.Locator("textarea#vB_Editor_QR_textarea, #qr_submit").CountAsync() > 0)
                        return new LoginResult { Success = true, Detail = "Se ha verificado el formulario de respuesta." };
                }
                return new LoginResult { Detail = "No se pudo confirmar el login. Puede haber una verificación pendiente o cambios en la web." };
            }
            finally { await page.CloseAsync(); }
        }
        finally { _gate.Release(); }
    }

    public async Task<ForumPost> InspectAsync(string url, string timeZone, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = await OpenPageAsync();
            try { return await ReadLastPostAsync(page, url, timeZone); }
            finally { await page.CloseAsync(); }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Envía un único Up manual de prueba. No borra mensajes ni cambia la configuración.
    /// Solo se invoca desde el panel con la automatización desactivada.
    /// </summary>
    public async Task<ForumPost> PostTestUpAsync(string url, string accountUsername,
        string timeZone, CancellationToken ct)
    {
        if (!ConfigurationStore.IsForumThreadUrl(url))
            throw new ArgumentException("Introduce una URL HTTPS válida de ForoCoches.");
        if (string.IsNullOrWhiteSpace(accountUsername))
            throw new ArgumentException("Configura primero el usuario de ForoCoches.");

        // El semáforo evita que dos clics o una revisión automática
        // manipulen simultáneamente la misma sesión de navegador.
        await _gate.WaitAsync(ct);
        try
        {
            var page = await OpenPageAsync();
            bool writeMayHaveStarted = false;
            try
            {
                var before = await ReadLastPostAsync(page, url, timeZone);
                if (IsOwnUp(before, accountUsername))
                    throw new InvalidOperationException(
                        "El último mensaje ya es tu Up. No se publica otro para evitar duplicados.");

                var reply = page.Locator("textarea#vB_Editor_QR_textarea");
                var submit = page.Locator("#qr_submit");
                if (await reply.CountAsync() != 1 || await submit.CountAsync() != 1)
                    throw new InvalidOperationException(
                        "No se encuentra el formulario de respuesta. Comprueba la sesión y los selectores.");

                // Única escritura. No hay borrado, no hay reintentos automáticos.
                await reply.FillAsync("Up");
                writeMayHaveStarted = true;
                await submit.ClickAsync();
                await page.WaitForTimeoutAsync(1200);

                var after = await ReadLastPostAsync(page, url, timeZone);
                if (after.Id == before.Id || !IsOwnUp(after, accountUsername))
                    throw new InvalidOperationException(
                        "No se ha podido confirmar el Up de prueba. Comprueba el hilo antes de volver a pulsar.");

                _log.LogInformation("UP manual verificado: {Url}, post {Id}", url, after.Id);
                return after;
            }
            catch (Exception ex) when (writeMayHaveStarted && ex is not OperationCanceledException)
            {
                throw new UncertainWriteException("El Up de prueba podría haberse publicado. Revisa el hilo antes de repetirlo.", ex);
            }
            finally { await page.CloseAsync(); }
        }
        finally { _gate.Release(); }
    }

    public async Task<ForumPost> RefreshUpAsync(string url, ForumPost expected,
        string accountUsername, string timeZone, int minAgeMinutes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            bool deleting = false;
            bool writeMayHaveStarted = false;
            var page = await OpenPageAsync(() => deleting);
            try
            {
                var current = await ReadLastPostAsync(page, url, timeZone);
                // Prevenir una acción si otro usuario ha comentado desde la comprobación.
                if (current.Id != expected.Id)
                    throw new InvalidOperationException("El último mensaje ha cambiado; se cancela el UP.");

                bool isOurUp = IsOwnUp(current, accountUsername);
                if (isOurUp)
                {
                    var table = page.Locator("table[id='post" + current.Id + "']");
                    if (await table.CountAsync() != 1)
                        throw new InvalidOperationException("No se ha localizado el bloque exacto del UP para borrarlo.");
                    var edit = table.Locator("a[href^='editpost']").Last;
                    if (await edit.CountAsync() == 0)
                        throw new InvalidOperationException("No aparece el botón de edición del último UP.");
                    deleting = true;
                    try
                    {
                        await edit.ClickAsync();
                        await page.Locator("#vB_Editor_QE_1_delete").ClickAsync();
                        await page.Locator("#rb_del_soft").CheckAsync();
                        writeMayHaveStarted = true;
                        await page.Locator("#quickedit_dodelete").ClickAsync();
                        await page.WaitForTimeoutAsync(750);
                    }
                    finally { deleting = false; }
                    var afterDelete = await ReadLastPostAsync(page, url, timeZone);
                    if (afterDelete.Id == current.Id)
                        throw new InvalidOperationException("No se ha confirmado el borrado del UP; no se publica otro.");
                    // Si otro comentario apareció mientras borrábamos, no publicar.
                    if (long.TryParse(afterDelete.Id, out long newer) &&
                        long.TryParse(current.Id, out long deleted) && newer > deleted)
                        throw new InvalidOperationException("Ha llegado otro comentario durante el borrado.");
                    if (afterDelete.PostedAt.HasValue &&
                        DateTimeOffset.UtcNow - afterDelete.PostedAt.Value < TimeSpan.FromMinutes(minAgeMinutes))
                        throw new InvalidOperationException("El comentario restante es demasiado reciente.");
                }
                var reply = page.Locator("textarea#vB_Editor_QR_textarea");
                var submit = page.Locator("#qr_submit");
                if (await reply.CountAsync() != 1 || await submit.CountAsync() != 1)
                    throw new SessionExpiredException();
                await reply.FillAsync("Up");
                writeMayHaveStarted = true;
                await submit.ClickAsync();
                await page.WaitForTimeoutAsync(1200);
                var posted = await ReadLastPostAsync(page, url, timeZone);
                if (!string.Equals(posted.Author.Trim(), accountUsername.Trim(), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(posted.Text.Trim(), "Up", StringComparison.Ordinal)
                    || posted.Id == current.Id)
                    throw new InvalidOperationException("Publicación no confirmada; comprobar manualmente antes de reintentar.");
                _log.LogInformation("UP publicado y verificado en {Url}, post {Id}", url, posted.Id);
                return posted;
            }
            catch (Exception ex) when (writeMayHaveStarted && ex is not OperationCanceledException)
            {
                throw new UncertainWriteException("No se ha podido confirmar el resultado de la publicación/borrado. Hilo pausado para revisión manual.", ex);
            }
            finally { await page.CloseAsync(); }
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> IsLoggedInAsync(IPage page) =>
        await page.Locator("a[href*='logout'], a[href*='do=logout']").CountAsync() > 0;

    // La página de desafío no es la página de login y no implica que
    // la contraseña ni las cookies hayan caducado.
    private static async Task<bool> IsSecurityChallengeAsync(IPage page)
    {
        return await page.EvaluateAsync<bool>(@"() => {
            const text = ((document.title || '') + ' ' +
              (document.body?.innerText || '')).slice(0, 5000).toLowerCase();
            return text.includes('performing security verification') ||
                   text.includes('checking if the site connection is secure') ||
                   (text.includes('verify you are human') && text.includes('cloudflare')) ||
                   !!document.querySelector('iframe[src*=""challenges.cloudflare.com""], input[name=""cf-turnstile-response""]');
        }");
    }

    private static bool LooksLoggedOut(IPage page) =>
        page.Url.Contains("/accounts/login", StringComparison.OrdinalIgnoreCase)
        || page.Url.Contains("/login.php", StringComparison.OrdinalIgnoreCase);

    private static async Task<ForumPost> ReadLastPostAsync(IPage page, string url, string timeZone)
    {
        if (!ConfigurationStore.IsForumThreadUrl(url))
            throw new ArgumentException("Solo URLs HTTPS de ForoCoches.");
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (await IsSecurityChallengeAsync(page)) throw new SecurityChallengeException();
        if (LooksLoggedOut(page)) throw new SessionExpiredException();

        // Paginación vBulletin: seguir el mayor número de página visible.
        var pages = await page.Locator("a[href*='showthread'][href*='page=']")
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.href)");
        var pageNumbers = pages.Select(h =>
        {
            var match = Regex.Match(h, @"[?&]page=(\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int n) ? n : 0;
        }).ToArray();
        var maxPage = pageNumbers.Length == 0 ? 0 : pageNumbers.Max();
        if (maxPage > 1)
        {
            var target = pages.FirstOrDefault(h => Regex.IsMatch(h, @"[?&]page=" + maxPage + @"(?:&|$)", RegexOptions.IgnoreCase));
            if (target != null && ConfigurationStore.IsForumThreadUrl(target))
            {
                await page.GotoAsync(target, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                if (await IsSecurityChallengeAsync(page)) throw new SecurityChallengeException();
                if (LooksLoggedOut(page)) throw new SessionExpiredException();
            }
        }

        // El ZIP original usaba table[id^=post] y post_message_X de vBulletin.
        // Extraer texto exacto sin regex sobre HTML y sin depender de jQuery.
        var post = await page.EvaluateAsync<ForumPost?>(@"() => {
            const messages = Array.from(document.querySelectorAll('[id^=""post_message_""]'));
            const msg = messages.at(-1);
            if (!msg) return null;
            const block = msg.closest('table[id^=""post""]') || msg.closest('div[id^=""post""]');
            if (!block) return null;
            const author = block.querySelector('a.bigusername, a[href*=""member.php""]');
            const date = block.querySelector('td.thead, .thead, .date');
            return {
                Id: msg.id.replace('post_message_', ''),
                Author: author ? author.textContent.trim() : '',
                Text: msg.innerText.trim(),
                RawDate: date ? date.innerText.trim() : ''
            };
        }");
        if (post == null)
        {
            if (await page.Locator("input[name='vb_login_username'], input[type='password']").CountAsync() > 0)
                throw new SessionExpiredException();
            throw new InvalidOperationException("No se pudo leer el último post. Revisar HTML/selectores de ForoCoches.");
        }
        if (string.IsNullOrWhiteSpace(post.Id) || string.IsNullOrWhiteSpace(post.Author))
            throw new InvalidOperationException("No se puede identificar con seguridad el autor o ID del último post.");
        // Leer un hilo público no demuestra que la cuenta esté autenticada.
        // Si no aparece el formulario de respuesta ni un enlace de salida,
        // detener los UP y pedir al usuario que revise la sesión.
        if (await page.Locator("textarea#vB_Editor_QR_textarea, #qr_submit").CountAsync() == 0
            && !await IsLoggedInAsync(page))
            throw new SessionExpiredException();
        post.PostedAt = ParsePostDate(post.RawDate, timeZone);
        return post;
    }

    public static bool IsOwnUp(ForumPost post, string accountUsername) =>
        string.Equals(post.Author.Trim(), accountUsername.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(post.Text.Trim(), "Up", StringComparison.Ordinal);

    public static DateTimeOffset? ParsePostDate(string text, string zoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var clock = Regex.Match(text, @"\b(\d{1,2}):(\d{2})\b");
        if (!clock.Success) return null;
        int hour = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
        int minute = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59) return null;
        DateTime? day = null;
        if (Regex.IsMatch(text, @"\bHoy\b", RegexOptions.IgnoreCase))
            day = now.Date;
        else if (Regex.IsMatch(text, @"\bAyer\b", RegexOptions.IgnoreCase))
            day = now.Date.AddDays(-1);
        else
        {
            var date = Regex.Match(text, @"\b(\d{1,2})/(\d{1,2})/(\d{4})\b");
            if (date.Success && DateTime.TryParseExact(date.Value, "d/M/yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                day = parsed.Date;
        }
        if (!day.HasValue) return null;
        var local = day.Value.AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_context != null) await _context.CloseAsync();
            _playwright?.Dispose();
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}

public sealed record BrowserHealthResult(bool Ok, string Mode, string Detail);
