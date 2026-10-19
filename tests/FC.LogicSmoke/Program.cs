using FC;

void Check(bool value, string text)
{
    if (!value) throw new Exception("SMOKE FAIL: " + text);
    Console.WriteLine("OK: " + text);
}
var settings = new BotSettings
{
    ForumUsername = "usuario-test",
    Enabled = true,
    StartTime = "08:00",
    EndTime = "00:00",
    TimeZone = "Europe/Madrid",
    MinMinutes = 75,
    MaxMinutes = 90,
    Threads = new() { new() { Name = "Prueba", Url = "https://www.forocoches.com/foro/showthread.php?t=123" } }
};
ConfigurationStore.Validate(settings);
Check(TimeWindow.IsOpen(settings, new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)), "Horario diurno permitido");
Check(!TimeWindow.IsOpen(settings, new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero)), "Horario nocturno bloqueado");
Check(ConfigurationStore.IsForumThreadUrl(settings.Threads[0].Url), "URL permitida");
Check(!ConfigurationStore.IsForumThreadUrl("https://forocoches.com.atacante.ejemplo/showthread.php"), "Host falso bloqueado");
Check(ForumBrowser.ParsePostDate("Ayer, 12:45", settings.TimeZone).HasValue, "Fecha relativa reconocida");
Check(ForumBrowser.ParsePostDate("20/10/2026, 12:45", settings.TimeZone).HasValue, "Fecha absoluta reconocida");
Check(ForumBrowser.ParsePostDate("fecha desconocida", settings.TimeZone) == null, "Fecha desconocida tratada como incierta");
Check(ForumBrowser.IsOwnUp(new ForumPost { Author = "usuario-test", Text = "Up" }, "usuario-test"), "UP propio exacto: borrar");
Check(!ForumBrowser.IsOwnUp(new ForumPost { Author = "otro", Text = "Up" }, "usuario-test"), "UP ajeno: no borrar");
Check(!ForumBrowser.IsOwnUp(new ForumPost { Author = "usuario-test", Text = "UP" }, "usuario-test"), "Mayúsculas incorrectas: no borrar");
Check(!ForumBrowser.IsOwnUp(new ForumPost { Author = "usuario-test", Text = "Mi respuesta" }, "usuario-test"), "Respuesta propia: no borrar");
Console.WriteLine("SMOKE OK");
