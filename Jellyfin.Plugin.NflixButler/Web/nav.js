// Nflix Butler — Jellyfin nav injection
// French text, no dependencies, no console noise

(function() {
  'use strict';

  const TARGET_TEXT = ['Plugin Settings', 'Plugin Pages', 'Dashboard'];
  const MAX_RETRIES = 120;
  const RETRY_DELAY = 500;
  let retries = 0;

  function token() {
    try {
      const raw = localStorage.getItem('jellyfin_credentials');
      const data = JSON.parse(raw || '{}');
      return (data.Servers && data.Servers[0] && data.Servers[0].AccessToken) || '';
    } catch (e) { return ''; }
  }

  function insertNav() {
    if (document.querySelector('[data-nflix-butler="1"]')) return;

    const navItems = document.querySelectorAll('.navMenuOption');
    if (!navItems.length) {
      if (retries < MAX_RETRIES) {
        retries++;
        setTimeout(insertNav, RETRY_DELAY);
      }
      return;
    }

    let target = null;
    for (const item of navItems) {
      if (TARGET_TEXT.includes(item.textContent?.trim())) {
        target = item;
        break;
      }
    }

    if (!target) {
      if (retries < MAX_RETRIES) {
        retries++;
        setTimeout(insertNav, RETRY_DELAY);
      }
      return;
    }

    const clone = target.cloneNode(true);
    clone.textContent = 'Nflix Butler';
    clone.removeAttribute('href');
    clone.removeAttribute('data-id');
    clone.setAttribute('data-nflix-butler', '1');
    clone.onclick = () => {
      if (token()) {
        window.open('/Plugins/NflixButler/web/dashboard', '_blank');
      } else {
        alert('Ouvrez cette page depuis Jellyfin pour accéder à Nflix Butler.');
      }
    };
    target.after(clone);
    console.info('Nflix Butler nav installed');
  }

  const observer = new MutationObserver(() => {
    if (!document.querySelector('[data-nflix-butler="1"]')) {
      insertNav();
    }
  });
  observer.observe(document.body, { childList: true, subtree: true });
  insertNav();
})();