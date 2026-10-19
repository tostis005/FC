using System.Net;
using System.Net.Mail;

namespace FC;

public sealed class EmailAlerts
{
    private readonly ILogger<EmailAlerts> _log;
    private readonly string _okFlag;
    private readonly string _lostFlag;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EmailAlerts(ILogger<EmailAlerts> log)
    {
        _log = log;
        var dir = Environment.GetEnvironmentVariable("FC_DATA_DIR") ?? "/data";
        Directory.CreateDirectory(dir);
        _okFlag = Path.Combine(dir, "session-established.flag");
        _lostFlag = Path.Combine(dir, "session-lost-notified.flag");
    }

    public async Task AuthenticatedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_okFlag)) File.WriteAllText(_okFlag, DateTimeOffset.UtcNow.ToString("O"));
            if (File.Exists(_lostFlag)) File.Delete(_lostFlag);
        }
        finally { _gate.Release(); }
    }

    public async Task LostAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // No avisar antes del primer login real ni repetir por cada hilo.
            if (!File.Exists(_okFlag) || File.Exists(_lostFlag)) return;
            var destination = Environment.GetEnvironmentVariable("ALERT_EMAIL_TO");
            var host = Environment.GetEnvironmentVariable("SMTP_HOST");
            var user = Environment.GetEnvironmentVariable("SMTP_USER");
            var password = Environment.GetEnvironmentVariable("SMTP_PASSWORD");
            var from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? user;
            if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(host)
                || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password)
                || string.IsNullOrWhiteSpace(from))
            {
                _log.LogWarning("Sesión perdida; faltan variables SMTP, no se ha enviado correo.");
                return;
            }
            try
            {
                using var message = new MailMessage(from, destination)
                {
                    Subject = "Sesión de ForoCoches caducada",
                    Body = "La sesión de ForoCoches ha caducado o requiere volver a iniciar sesión. Abre el panel privado de FC para recuperarla."
                };
                using var client = new SmtpClient(host, int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out int port) ? port : 587)
                {
                    Credentials = new NetworkCredential(user, password),
                    EnableSsl = true,
                    Timeout = 15000
                };
                await client.SendMailAsync(message, ct);
                File.WriteAllText(_lostFlag, DateTimeOffset.UtcNow.ToString("O"));
                _log.LogWarning("Aviso de sesión caducada enviado.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "No se ha podido enviar la alerta SMTP.");
            }
        }
        finally { _gate.Release(); }
    }
}
