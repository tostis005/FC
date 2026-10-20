namespace FC;

public sealed class UpWorker : BackgroundService
{
    private readonly ConfigurationStore _config;
    private readonly ForumBrowser _forum;
    private readonly RuntimeState _runtime;
    private readonly EmailAlerts _alerts;
    private readonly ILogger<UpWorker> _log;

    public UpWorker(ConfigurationStore config, ForumBrowser forum, RuntimeState runtime,
        EmailAlerts alerts, ILogger<UpWorker> log)
    {
        _config = config;
        _forum = forum;
        _runtime = runtime;
        _alerts = alerts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("FC iniciado: los intervalos son volátiles y se recalculan tras reiniciar.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = _config.Read();
                var now = DateTimeOffset.UtcNow;
                var urls = config.Threads.Select(t => t.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var oldUrl in _runtime.Threads.Keys.Where(u => !urls.Contains(u)))
                    _runtime.Threads.TryRemove(oldUrl, out _);

                foreach (var thread in config.Threads)
                {
                    var status = _runtime.Threads.GetOrAdd(thread.Url, url =>
                    {
                        var wait = Draw(config);
                        return new ThreadRuntime
                        {
                            Url = url, Name = thread.Name, State = "Programado",
                            WaitMinutes = wait,
                            NextCheck = DateTimeOffset.UtcNow.AddMinutes(wait)
                        };
                    });
                    status.Name = thread.Name;
                    if (status.PausedForReview)
                    {
                        status.State = "En pausa: revisión manual";
                        continue;
                    }
                    if (!config.Enabled)
                    {
                        status.State = "Automatización desactivada";
                        continue;
                    }
                    if (!TimeWindow.IsOpen(config, now))
                    {
                        status.State = "Fuera de horario";
                        continue;
                    }
                    if (status.NextCheck > now)
                    {
                        status.State = "En espera";
                        continue;
                    }
                    await ProcessThreadAsync(thread, status, config, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _runtime.LastGlobalError = ex.Message;
                _log.LogError(ex, "Error del planificador");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static int Draw(BotSettings c) => Random.Shared.Next(c.MinMinutes, c.MaxMinutes + 1);

    private async Task ProcessThreadAsync(ThreadSettings thread, ThreadRuntime status,
        BotSettings config, CancellationToken ct)
    {
        status.State = "Comprobando";
        try
        {
            var post = await _forum.InspectAsync(thread.Url, config.TimeZone, ct);
            _runtime.LoginState = "Conectada";
            await _alerts.AuthenticatedAsync();
            status.LastError = null;

            var now = DateTimeOffset.UtcNow;
            bool changed = status.LastPostId != null && status.LastPostId != post.Id;
            if (status.FirstObserved == null || changed) status.FirstObserved = now;
            if (changed) status.WaitMinutes = Draw(config);
            status.LastPostId = post.Id;
            status.LastActivity = post.PostedAt ?? now;

            // Si cambió el comentario mientras dormíamos, el nuevo plazo cuenta
            // desde la fecha real del comentario. Fecha no interpretable: desde ahora.
            var baseDate = post.PostedAt ?? (changed ? now : status.FirstObserved.Value);
            var due = baseDate.AddMinutes(status.WaitMinutes);
            if (due > now)
            {
                status.NextCheck = due;
                status.State = changed ? "Comentario nuevo: reprogramado" : "En espera";
                return;
            }

            var previous = post;
            // RefreshUp vuelve a leer el último mensaje, lo compara por ID
            // y solo borra si el autor configurado y el texto EXACTO son Up.
            var published = await _forum.RefreshUpAsync(thread.Url, previous,
                config.ForumUsername, config.TimeZone, config.MinMinutes, ct);
            await _alerts.AuthenticatedAsync();
            status.LastPostId = published.Id;
            status.LastActivity = published.PostedAt ?? DateTimeOffset.UtcNow;
            status.FirstObserved = DateTimeOffset.UtcNow;
            status.WaitMinutes = Draw(config);
            status.NextCheck = status.LastActivity.Value.AddMinutes(status.WaitMinutes);
            status.State = "UP publicado y verificado";
            _log.LogInformation("UP correcto en {Name}", thread.Name);
        }
        catch (SessionExpiredException)
        {
            status.State = "Sesión caducada";
            status.LastError = "Inicia sesión desde el panel";
            status.NextCheck = DateTimeOffset.UtcNow.AddMinutes(5);
            _runtime.LoginState = "Caducada";
            await _alerts.LostAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UncertainWriteException ex)
        {
            _log.LogError(ex, "Escritura no verificada en {Name}; se pausa el hilo", thread.Name);
            status.PausedForReview = true;
            status.State = "En pausa: revisar en ForoCoches";
            status.LastError = ex.Message;
            status.NextCheck = DateTimeOffset.UtcNow.AddMinutes(10);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falló el hilo {Name}", thread.Name);
            status.LastError = ex.Message;
            if (ex.Message.Contains("Publicación no confirmada", StringComparison.Ordinal)
                || ex.Message.Contains("No se ha confirmado el borrado", StringComparison.Ordinal))
                status.PausedForReview = true;
            status.State = status.PausedForReview ? "En pausa: revisión manual" : "Error: revisar logs";
            status.NextCheck = DateTimeOffset.UtcNow.AddMinutes(10);
        }
    }
}
