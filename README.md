# FC · Gestor de hilos (primera versión)

Servicio ASP.NET Core en C# + Playwright/Chromium para gestionar hilos propios desde un panel privado, ejecutado en Docker.

**Estado:** primera versión de desarrollo, con Chromium gráfico experimental (Xvfb) habilitado por defecto en Docker. La lógica heredada de CefSharp se ha revisado; los selectores reales de ForoCoches, el acceso de cuenta y las operaciones de escritura **requieren una prueba con sesión autorizada**. No activar publicaciones hasta validar esas pruebas. No se ha desplegado en Contabo.

## Funciones
- URLs, intervalos mínimo/máximo, horario y zona horaria editables en `/data/config.json`, sin base de datos.
- Chromium con perfil persistente en `/data/chromium`; ventana de navegador remoto integrada en el panel: se muestra una imagen actualizable del propio Chromium para hacer clic, escribir usuario/contraseña y conservar las cookies. La contraseña no se guarda en `config.json`.
- Evaluación del último mensaje, autor y fecha; solo elimina si el autor coincide con el usuario configurado y el texto es exactamente `Up`.
- Control de diálogos JavaScript y esperas con timeout; errores visibles. Si un borrado o publicación no se puede confirmar, el hilo se pausa hasta que se pulse *Reanudar* desde el panel.
- Alerta por SMTP al correo configurado cuando se detecta pérdida de una sesión que antes funcionaba, sin reenvíos continuos.
- Panel web protegido con contraseña de administración y exposición de puerto solo en loopback para acceder con túnel SSH.
- Arranque tras reinicio mediante `restart: unless-stopped`.

## Preparación
1. Copiar `.env.example` a `.env` y definir una contraseña de administración larga, destinatario de notificaciones y SMTP. **Nunca subir `.env` a GitHub.**
2. `docker compose up -d --build`.
3. Acceder con túnel SSH: `ssh -L 5088:127.0.0.1:5088 usuario@TU_VPS`. Abrir `http://localhost:5088` y autenticarse con usuario `admin` y la contraseña de `.env`.
4. Establecer el nombre de usuario de ForoCoches, URLs, horario y minutos; guardar cambios.
5. Mantener la automatización desactivada y pulsar *Abrir navegador Chromium*. Seleccionar los campos dentro de la imagen y utilizar las cajas del panel para escribir texto o contraseña; pulsar *Enter* cuando sea necesario, luego *Comprobar sesión* y *Cerrar navegador*. Si el sitio exige una verificación adicional, debe completarse de forma manual y legítima; no se sortea automáticamente.
6. Antes de marcar *Automatización activada*, revisar los selectores y probar en hilos propios.
7. Ver logs: `docker compose logs -f`.

El perfil de Chromium se guarda en el volumen Docker `fc_data`, que no entra en el repositorio. Para copiarlo o hacer una copia de seguridad, utiliza `docker volume` (o `docker cp`).

## Notas de revisión del proyecto antiguo
La aplicación original usa CefSharp en Windows, HTML de vBulletin y los selectores `textarea#vB_Editor_QR_textarea`, `#qr_submit`, `a[href^="editpost"]`, `#vB_Editor_QE_1_delete`, `#rb_del_soft` y `#quickedit_dodelete`. También contiene `catch { }` que oculta fallos. No se copia el binario/cache del ZIP ni los datos de sesión antiguos.

## Seguridad y límites
**No abrir el puerto 5088 directamente a Internet.** Usar túnel SSH o HTTPS con proxy y control de acceso. No automatizar desafíos de seguridad. La primera versión no garantiza la compatibilidad de los selectores con la web actual, que requiere validación con una cuenta real y los términos de ForoCoches.

## Ventana de Chromium remoto
Es una representación interactiva mediante capturas JPEG de un navegador real que corre en Contabo, **no un iframe**. Su perfil es exactamente el mismo que utiliza el servicio en segundo plano. La vista se actualiza cada pocos segundos mientras está abierta, permite pulsaciones de ratón, texto, tecla Enter/Tab, desplazamiento y navegación dentro de ForoCoches. Para escribir una contraseña, haz clic primero en el campo de la imagen y usa el campo *Escribir contraseña*. Este campo se vacía automáticamente. El robot se mantiene en pausa mientras la ventana remota está abierta. La contraseña viaja por HTTPS entre el panel y tu VPS; no se guarda en disco por nuestra aplicación. No expongas el panel por HTTP público sin TLS.

Tras el login, si *Probar lectura* sigue fallando, puede ser necesario actualizar los selectores de mensajes de ForoCoches: iniciar sesión no garantiza que la estructura antigua del HTML siga existiendo.

## Prueba de Chromium con interfaz gráfica (Xvfb)

El contenedor FC inicia un servidor X virtual privado mediante `xvfb-run` (incluido en la imagen oficial de Playwright). Chromium se lanza con `Headless = false`. No se abre ningún puerto gráfico adicional: la vista sigue apareciendo mediante las capturas en el panel web habitual. Se conserva el **mismo perfil** de cookies de `/data/chromium`, en el mismo volumen Docker.

Tras actualizar el código y reconstruir **solo** FC (`cd ~/fc && git pull --ff-only && docker compose up -d --build --force-recreate fc`), abre el panel por HTTPS con el usuario `admin`, desactiva la automatización y pulsa **Comprobar Chromium gráfico**. La prueba abre y captura `about:blank`, sin acceder a ForoCoches. Si funciona, intenta **Abrir navegador Chromium**. Para volver al modo sin interfaz añade `FC_CHROMIUM_HEADLESS=true` en `~/fc/.env` y recrea FC.

**Límite:** Xvfb permite ejecutar el navegador en modo gráfico, pero no altera la IP de Contabo ni garantiza que Cloudflare conceda acceso. Si la verificación se repite, no se intentarán soluciones para eludirla: mantén la automatización apagada y solicita acceso autorizado. La aplicación distingue esta verificación de una sesión caducada.
