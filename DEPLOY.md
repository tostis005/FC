# FC — Guía de despliegue en Contabo

Esta guía explica cómo desplegar el repositorio público https://github.com/tostis005/FC con Docker y GitHub Actions. La aplicación arranca con las publicaciones automáticas **desactivadas**.

## 1. Preparar el VPS (una sola vez)

Accede por SSH con un usuario de confianza:

    ssh USUARIO@IP_DEL_VPS
    docker --version
    docker compose version

Si falta Docker/Compose, instala Docker Engine con la documentación oficial para Ubuntu (https://docs.docker.com/engine/install/ubuntu/) o Debian (https://docs.docker.com/engine/install/debian/). El usuario SSH de despliegue debe poder ejecutar docker compose sin sudo. **El grupo docker permite obtener privilegios administrativos sobre el VPS**; no lo concedas a desconocidos.

Crea el archivo privado de configuración:

    mkdir -p ~/fc
    nano ~/fc/.env

Introduce lo siguiente con tus valores **reales**. No subas este archivo a GitHub ni compartas las credenciales por chat:

    FC_ADMIN_PASSWORD=UNA_CONTRASENA_LARGA_ALEATORIA
    ALERT_EMAIL_TO=hola@elmercadodeorigen.com
    SMTP_HOST=smtp.gmail.com
    SMTP_PORT=587
    SMTP_USER=CORREO_EMISOR@gmail.com
    SMTP_PASSWORD=CONTRASENA_DE_APLICACION_DE_GMAIL
    SMTP_FROM=CORREO_EMISOR@gmail.com

Guarda el archivo y restringe permisos:

    chmod 600 ~/fc/.env

Para Gmail necesitas normalmente tener la verificación en dos pasos y crear una contraseña de aplicación para el envío SMTP. Sin un SMTP válido no llegarán alertas.

## 2. Conectar GitHub con Contabo por SSH

En GitHub: repositorio **tostis005/FC → Settings → Secrets and variables → Actions → New repository secret**.

Configura estos secretos:

| Secreto | Contenido |
| --- | --- |
| FC_VPS_HOST | La IP o DNS de tu Contabo |
| FC_VPS_USER | Usuario SSH del VPS |
| FC_VPS_SSH_PRIVATE_KEY | Clave privada SSH de despliegue, completa, multilínea |
| FC_VPS_KNOWN_HOSTS | Clave pública de host SSH del VPS comprobada |
| FC_VPS_PORT | Opcional: número de puerto SSH si no es 22 |

Recomendación: crea un par de claves SSH dedicado para este despliegue e instala **la clave pública** en ~/.ssh/authorized_keys del usuario del VPS. Guarda **la privada** solo en los secretos de GitHub, jamás en el repositorio.

Para obtener FC_VPS_KNOWN_HOSTS desde tu ordenador:

    ssh-keyscan -t ed25519 -p 22 IP_DEL_VPS > known_hosts_fc
    ssh-keygen -lf known_hosts_fc

Verifica la huella contra la del propio VPS, por ejemplo ejecutando allí:

    ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub

Copia el **contenido íntegro** de known_hosts_fc como secreto. No confíes en una huella no verificada.

## Conviviendo con otros contenedores Docker

**Opción recomendada:** un proyecto Docker Compose independiente para FC, en `~/fc` (sin modificar los proyectos ni contenedores existentes). Docker construirá y ejecutará el servicio `fc` por separado, con su propio volumen persistente de configuración y cookies. Compartirá el sistema Docker del mismo VPS, pero no las carpetas ni el entorno de otros servicios.

- Verifica con `docker ps --format 'table {{.Names}}\t{{.Ports}}'` que el puerto **5088** no está usado. Si está ocupado, cambia **solo el puerto del host**, a la izquierda en `compose.yaml`, p. ej. `127.0.0.1:5099:8080` y usa 5099 en el túnel SSH.
- No hagas `docker compose down` fuera del directorio `~/fc`, ni `docker system prune --volumes`.
- Si ya tienes un gestor de contenedores/hosting que conecta GitHub con tu VPS, puedes optar por esa integración **en lugar del workflow de GitHub Actions**, conectando la rama `main`, el archivo `compose.yaml`, y el archivo privado `.env` **solo en el servidor**. No necesitas ejecutar ambos métodos de despliegue.
- Comprueba que hay memoria y espacio para Chromium/Playwright. Si el VPS es limitado, vigila el consumo de recursos en las primeras pruebas.

## 3. Enviar código al servidor

En GitHub abre **Actions → Desplegar FC en Contabo → Run workflow**.

Ese proceso compila y ejecuta las pruebas básicas. Si pasan, copia el repositorio por SSH a ~/fc y ejecuta:

    cd ~/fc
    docker compose up -d --build --remove-orphans

La primera construcción descarga la imagen de Playwright/Chromium y puede necesitar memoria y almacenamiento disponibles.

Para comprobar estado o errores:

    cd ~/fc
    docker compose ps
    docker compose logs -f --tail=100

Si falla la compilación, revisa el registro del workflow en Actions. Si falla el navegador o el envío de emails, revisa los logs del contenedor.

## 4. Abrir el panel desde tu equipo

En tu ordenador abre una terminal:

    ssh -L 5088:127.0.0.1:5088 USUARIO@IP_DEL_VPS

Mantén la terminal abierta. Visita http://localhost:5088

Inicia sesión con:

- Usuario: admin
- Contraseña: FC_ADMIN_PASSWORD que escribiste en el .env del VPS

**No abras el puerto 5088 públicamente:** el contenedor solo lo enlaza a la dirección 127.0.0.1 del servidor. Si quieres un dominio, prepara un proxy HTTPS autenticado.

## 5. Probar un único Up antes de habilitar el servicio

1. En Configuración indica el nombre de usuario exacto de ForoCoches, el intervalo aleatorio y el horario (por ejemplo 08:00–00:00, Europe/Madrid). Guarda.
2. Deja **desactivada** la casilla «Activar publicaciones automáticas» y guarda.
3. Pulsa **Abrir navegador Chromium** con la automatización desactivada. Verás la página de ForoCoches dentro del panel: pulsa en el campo Usuario de la imagen y escribe desde «Escribir texto»; pulsa en el campo Contraseña de la imagen y usa «Escribir contraseña». Utiliza los botones Enter, Tab, Desplazar y Recargar si los necesitas. Después pulsa «Comprobar sesión» y **Cerrar navegador (conservar cookies)**. Se guardan en el volumen Docker, incluso si el servicio se reinicia. Si ForoCoches pide verificaciones adicionales, complétalas manualmente cuando sea posible.
4. Pulsa «Probar lectura del primer hilo» para confirmar que reconoce el último comentario **sin publicar nada**.
5. En «Publicar un Up de prueba», pega la URL de **un hilo propio**. Confirma. Publicará **un solo mensaje real** de texto exacto Up. No borrará mensajes anteriores ni iniciará ciclos automáticos.
6. Comprueba directamente en ForoCoches que el mensaje se ha publicado. Si la web indica «no confirmado» o da error, **revisa el hilo antes de reintentar**. La prueba rechaza duplicar un Up propio si ya es el último mensaje.
7. Una vez validados login, lectura y publicación, marca «Activar publicaciones automáticas» y guarda.

Si hay CAPTCHA, 2FA o verificación adicional, debes completarla legítimamente; la aplicación no intenta sortear esos controles.

## 6. Reinicios y actualizaciones

- El contenedor tiene la política restart: unless-stopped. Comprueba que Docker arranca con el VPS.
- La configuración JSON y el perfil de sesión de Chromium residen en el volumen Docker fc_fc_data y persisten al actualizar la imagen.
- Para actualizar el código, vuelve a lanzar el mismo workflow.
- Para cambiar valores de SMTP o contraseña de administrador, edita ~/fc/.env y ejecuta docker compose up -d --force-recreate.
- **No uses docker compose down -v** si deseas conservar las cookies y la configuración.
- El aviso a hola@elmercadodeorigen.com se emitirá si se detecta caducidad de una sesión previamente establecida y SMTP funciona.

## Alcance de esta versión

Los selectores de autenticación, lectura, publicación y borrado proceden del proyecto antiguo con CefSharp y pueden haber cambiado en ForoCoches. La compilación y las pruebas de lógica no sustituyen una validación con la web real. Por seguridad, no habilites la automatización hasta comprobar que el Up manual se publica correctamente. Verifica también las reglas de uso del foro antes de automatizar mensajes repetitivos.

## Acceso público con la IP de Contabo
Si ya tienes Caddy como proxy (80/443), conserva FC escuchando **solo en 127.0.0.1:5088** y haz que Caddy proxyee el panel por HTTPS a ese puerto, sin exponer Docker directamente. El navegador remoto transmite capturas de pantalla y contraseñas durante el login: **no uses HTTP público**. Protege el panel con FC_ADMIN_PASSWORD y un certificado TLS válido.
