using Microsoft.Playwright;

namespace FC;

// Vista de Chromium remoto: las imágenes salen del MISMO contexto persistente
// que utiliza el robot. Nunca se exportan cookies ni contraseñas al navegador local.
public sealed partial class ForumBrowser
{
    private IPage? _manualPage;

    // Lectura sin bloqueo: el worker evita publicar mientras se maneja una sesión.
    public bool IsManualBrowserOpen => _manualPage is { IsClosed: false };

    public async Task<ManualBrowserState> OpenManualBrowserAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_manualPage is { IsClosed: false })
                return new(true, _manualPage.Url, "Navegador remoto abierto.");
            var page = await OpenPageAsync();
            _manualPage = page;
            page.SetDefaultTimeout(15000);
            // El navegador remoto parte siempre de ForoCoches.
            try
            {
                await page.GotoAsync("https://www.forocoches.com/foro/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            }
            catch (PlaywrightException ex)
            {
                _log.LogWarning(ex, "La primera carga de ForoCoches no ha terminado correctamente.");
                // Mostrar la página de error o el desafío tal como se haya recibido.
            }
            return new(true, page.Url, "Navegador remoto abierto. Pulsa sobre la imagen y escribe desde el panel.");
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualBrowserState> GetManualBrowserStateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _manualPage is { IsClosed: false } page
                ? new(true, page.Url, "Abierto")
                : new(false, null, "Cerrado");
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> CaptureManualBrowserAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = RequireManualPage();
            // Se comprime la captura para no transmitir megabytes cada segundo.
            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 63,
                Timeout = 15000,
                Animations = ScreenshotAnimations.Disabled
            });
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualBrowserState> PerformManualActionAsync(
        ManualBrowserCommand command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = RequireManualPage();
            switch (command.Action?.Trim().ToLowerInvariant())
            {
                case "click":
                    if (command.X is not (>= 0 and < 1280) || command.Y is not (>= 0 and < 900))
                        throw new ArgumentException("Coordenadas fuera de la pantalla.");
                    await page.Mouse.ClickAsync(command.X!.Value, command.Y!.Value);
                    break;
                case "type":
                    // El texto se envía exclusivamente a Chromium (nunca a un log o archivo).
                    if (string.IsNullOrEmpty(command.Text) || command.Text.Length > 256)
                        throw new ArgumentException("Introduce entre 1 y 256 caracteres.");
                    await page.Keyboard.InsertTextAsync(command.Text);
                    break;
                case "key":
                    var validKeys = new HashSet<string>(StringComparer.Ordinal)
                    { "Enter", "Tab", "Escape", "Backspace", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight" };
                    if (command.Text == null || !validKeys.Contains(command.Text))
                        throw new ArgumentException("Tecla no permitida.");
                    await page.Keyboard.PressAsync(command.Text);
                    break;
                case "scroll":
                    if (command.Y is not (>= -800 and <= 800))
                        throw new ArgumentException("Desplazamiento fuera de rango.");
                    await page.Mouse.WheelAsync(0, command.Y!.Value);
                    break;
                case "goto":
                    if (!ConfigurationStore.IsForumThreadUrl(command.Text) &&
                        !string.Equals(command.Text, "https://www.forocoches.com/foro/", StringComparison.Ordinal))
                        throw new ArgumentException("Solo se permite navegar directamente a ForoCoches por HTTPS.");
                    await page.GotoAsync(command.Text!, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000
                    });
                    break;
                case "reload":
                    await page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000
                    });
                    break;
                default:
                    throw new ArgumentException("Acción desconocida.");
            }
            return new(true, page.Url, "Acción realizada.");
        }
        finally { _gate.Release(); }
    }

    // La detección positiva es conservadora: tener cookies no basta para afirmar
    // que la sesión está aceptada por ForoCoches.
    public async Task<bool> CheckManualLoginAsync(string? threadUrl, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = RequireManualPage();
            if (await IsLoggedInAsync(page)) return true;
            if (!ConfigurationStore.IsForumThreadUrl(threadUrl)) return false;
            var probe = await OpenPageAsync();
            try
            {
                await probe.GotoAsync(threadUrl!, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000
                });
                if (probe.Url.Contains("login", StringComparison.OrdinalIgnoreCase))
                    return false;
                return await IsLoggedInAsync(probe) ||
                    await probe.Locator("textarea#vB_Editor_QR_textarea, #qr_submit").CountAsync() > 0;
            }
            finally { await probe.CloseAsync(); }
        }
        finally { _gate.Release(); }
    }

    public async Task CloseManualBrowserAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_manualPage is { IsClosed: false }) await _manualPage.CloseAsync();
            _manualPage = null;
            // Las cookies persisten en el perfil /data/chromium.
        }
        finally { _gate.Release(); }
    }

    private IPage RequireManualPage()
    {
        if (_manualPage is not { IsClosed: false } page)
            throw new InvalidOperationException("Abre primero el navegador remoto.");
        return page;
    }
}

public sealed record ManualBrowserState(bool Open, string? Url, string Detail);
public sealed record ManualBrowserCommand(string Action, int? X, int? Y, string? Text);
