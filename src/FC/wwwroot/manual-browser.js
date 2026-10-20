/* Control manual de un Chromium remoto dentro del mismo perfil que el worker.
   No guarda credenciales, cookies ni capturas en el navegador local. */
(() => {
  'use strict';
  const get = id => document.getElementById(id);
  let opened = false;
  let frameTimer = null;
  let frameLoading = false;
  let objectUrl = null;

  const notice = (txt, isError = false) => {
    const el = get('manualMessage');
    el.textContent = txt;
    el.classList.toggle('error', isError);
  };

  const setOpened = value => {
    opened = value;
    get('manualBrowser').hidden = !value;
    get('manualOpen').disabled = value;
    get('manualClose').disabled = !value;
    get('manualVerify').disabled = !value;
    if (!value) {
      if (frameTimer) clearInterval(frameTimer);
      frameTimer = null;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = null;
      get('manualImage').removeAttribute('src');
      get('manualPassword').value = '';
      get('manualText').value = '';
    } else if (!frameTimer) {
      frameTimer = setInterval(() => {
        if (!document.hidden) void drawFrame();
      }, 2200);
    }
  };

  const drawFrame = async () => {
    if (!opened || frameLoading) return;
    frameLoading = true;
    try {
      const response = await fetch('/api/manual/frame', { cache: 'no-store' });
      if (!response.ok) throw new Error('No se puede obtener la imagen de Chromium.');
      const nextUrl = URL.createObjectURL(await response.blob());
      const img = get('manualImage');
      img.onload = () => {
        if (objectUrl && objectUrl !== nextUrl) URL.revokeObjectURL(objectUrl);
        objectUrl = nextUrl;
      };
      img.src = nextUrl;
    } catch (ex) {
      notice(ex.message, true);
    } finally {
      frameLoading = false;
    }
  };

  const command = async action => {
    if (!opened) return;
    try {
      const result = await api('/api/manual/action', { method: 'POST', body: JSON.stringify(action) });
      get('manualUrl').textContent = result.url || 'Página de ForoCoches';
      await drawFrame();
    } catch (ex) {
      notice(ex.message, true);
    }
  };

  get('checkGraphicBrowser').addEventListener('click', async () => {
    const button = get('checkGraphicBrowser');
    button.disabled = true;
    notice('Comprobando que Chromium puede abrir una ventana gráfica…');
    try {
      const result = await api('/api/browser/test', { method: 'POST', body: '{}' });
      notice(result.detail + ' Modo: ' + result.mode + '.');
    } catch (ex) {
      notice(ex.message, true);
    } finally {
      button.disabled = false;
    }
  });

  get('manualOpen').addEventListener('click', async () => {
    get('manualOpen').disabled = true;
    notice('Abriendo Chromium dentro del VPS…');
    try {
      const data = await api('/api/manual/open', { method: 'POST', body: '{}' });
      setOpened(data.open);
      get('manualUrl').textContent = data.url || 'ForoCoches';
      notice(data.detail);
      await drawFrame();
    } catch (ex) {
      get('manualOpen').disabled = false;
      notice(ex.message, true);
    }
  });

  get('manualClose').addEventListener('click', async () => {
    try {
      await api('/api/manual/close', { method: 'POST', body: '{}' });
      setOpened(false);
      notice('Navegador cerrado. Las cookies permanecen en el volumen de Docker.');
    } catch (ex) { notice(ex.message, true); }
  });

  get('manualVerify').addEventListener('click', async () => {
    notice('Comprobando si ForoCoches reconoce la sesión…');
    try {
      const result = await api('/api/manual/verify', { method: 'POST', body: '{}' });
      notice(result.detail, !result.authenticated);
      if (result.authenticated) await loadStatus();
    } catch (ex) { notice(ex.message, true); }
  });

  get('manualImage').addEventListener('click', event => {
    const img = event.currentTarget;
    const bounds = img.getBoundingClientRect();
    if (!bounds.width || !bounds.height) return;
    const x = Math.max(0, Math.min(1279, Math.floor((event.clientX - bounds.left) * 1280 / bounds.width)));
    const y = Math.max(0, Math.min(899, Math.floor((event.clientY - bounds.top) * 900 / bounds.height)));
    void command({ action: 'click', x, y });
  });

  const typeFrom = async id => {
    const el = get(id);
    const text = el.value;
    el.value = ''; // Nunca conservar contraseñas escritas en este formulario.
    if (!text) return;
    await command({ action: 'type', text });
  };
  get('manualType').addEventListener('click', () => void typeFrom('manualText'));
  get('manualTypePassword').addEventListener('click', () => void typeFrom('manualPassword'));

  document.querySelectorAll('[data-browser-key]').forEach(button => {
    button.addEventListener('click', () => void command({ action: 'key', text: button.dataset.browserKey }));
  });
  get('manualScrollUp').addEventListener('click', () => void command({ action: 'scroll', y: -550 }));
  get('manualScrollDown').addEventListener('click', () => void command({ action: 'scroll', y: 550 }));
  get('manualRefresh').addEventListener('click', () => void command({ action: 'reload' }));
  get('manualGo').addEventListener('click', () => void command({
    action: 'goto', text: get('manualDestination').value.trim()
  }));

  // Recuperar una ventana remota después de recargar el panel.
  api('/api/manual/status').then(async data => {
    if (data.open) {
      setOpened(true);
      get('manualUrl').textContent = data.url || 'ForoCoches';
      notice('Navegador remoto recuperado.');
      await drawFrame();
    }
  }).catch(ex => notice(ex.message, true));
})();
