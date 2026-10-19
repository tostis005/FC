# FC · Gestor de hilos (primera versión)

Servicio ASP.NET Core en C# + Playwright/Chromium para gestionar hilos propios desde un panel privado, ejecutado en Docker.

**Estado:** primera versión de desarrollo. La lógica heredada de CefSharp se ha revisado; los selectores reales de ForoCoches, el acceso de cuenta y las operaciones de escritura **requieren una prueba con sesión autorizada**. No activar publicaciones hasta validar esas pruebas. No se ha desplegado en Contabo.

## Funciones
- URLs, intervalos mínimo/máximo, horario y zona horaria editables en `/data/config.json`, sin base de datos.
- Chromium con perfil persistente en `/data/chromium`; inicio de sesión desde el panel (usuario/contraseña enviados solo al backend y **no guardados**).
- Evaluación del último mensaje, autor y fecha; solo elimina si el autor coincide con el usuario configurado y el texto es exactamente `Up`.
- Control de diálogos JavaScript y esperas con timeout; errores visibles.
- Alerta por SMTP al correo configurado cuando se detecta pérdida de una sesión que antes funcionaba, sin reenvíos continuos.
- Panel web protegido con contraseña de administración y exposición de puerto solo en loopback para acceder con túnel SSH.
- Arranque tras reinicio mediante `restart: unless-stopped`.

## Preparación
1. Copiar `.env.example` a `.env` y definir una contraseña de administración larga, destinatario de notificaciones y SMTP. **Nunca subir `.env` a GitHub.**
2. `docker compose up -d --build`.
3. Acceder con túnel SSH: `ssh -L 5088:127.0.0.1:5088 usuario@TU_VPS`. Abrir `http://localhost:5088` y autenticarse con usuario `admin` y la contraseña de `.env`.
4. Establecer el nombre de usuario de ForoCoches, URLs, horario y minutos; guardar cambios.
5. Pulsar *Iniciar sesión*; si el sitio exige una verificación adicional, esta primera versión no resuelve CAPTCHAs.
6. Antes de marcar *Automatización activada*, revisar los selectores y probar en hilos propios.
7. Ver logs: `docker compose logs -f`.

El perfil de Chromium se guarda en el volumen `./data`, que no entra en el repositorio.

## Notas de revisión del proyecto antiguo
La aplicación original usa CefSharp en Windows, HTML de vBulletin y los selectores `textarea#vB_Editor_QR_textarea`, `#qr_submit`, `a[href^="editpost"]`, `#vB_Editor_QE_1_delete`, `#rb_del_soft` y `#quickedit_dodelete`. También contiene `catch { }` que oculta fallos. No se copia el binario/cache del ZIP ni los datos de sesión antiguos.

## Seguridad y límites
**No abrir el puerto 5088 directamente a Internet.** Usar túnel SSH o HTTPS con proxy y control de acceso. No automatizar desafíos de seguridad. La primera versión no garantiza la compatibilidad de los selectores con la web actual, que requiere validación con una cuenta real y los términos de ForoCoches.
