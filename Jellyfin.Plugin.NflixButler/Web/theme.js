/* Nflix Web Theme — make the Jellyfin web client look like the Nflix app.
   Idempotent, re-applied on an interval because jellyfin-web is an SPA. */
(function () {
  if (window.__nflixWebTheme) { /* already loaded once; keep re-asserting DOM bits below */ }
  window.__nflixWebTheme = true;

  var CSS = [
    ':root{',
    '--md-sys-color-primary:#E50914;--md-sys-color-on-primary:#FFFFFF;',
    '--md-sys-color-primary-container:#8C0A1C;--md-sys-color-on-primary-container:#FFFFFF;',
    '--md-sys-color-secondary:#FF6B7A;--md-sys-color-secondary-container:#2A1418;',
    '--md-sys-color-background:#000000;--md-sys-color-on-background:#F5F5F7;',
    '--md-sys-color-surface:#121212;--md-sys-color-on-surface:#F5F5F7;',
    '--md-sys-color-surface-variant:#202020;--md-sys-color-on-surface-variant:#A6A6B4;',
    '--md-sys-color-surface-container:#121212;--md-sys-color-surface-container-low:#0D0D12;',
    '--md-sys-color-surface-container-high:#202020;--md-sys-color-surface-container-highest:#22222C;',
    '--md-sys-color-outline:rgba(255,255,255,.16);--md-sys-color-outline-variant:rgba(255,255,255,.10);',
    '--md-sys-color-error:#FF6B7A;--md-sys-color-inverse-surface:#F5F5F7;--md-sys-color-inverse-on-surface:#121212;',
    '}',
    'html,body{background:#000 !important;color:#F5F5F7 !important;font-family:inherit;}',
    'body{padding-bottom:76px;}',
    'a{color:#F5F5F7;}a:hover{color:#E50914;}',
    '.mainDrawer,.drawer,.md-drawer,.appfooter,.skinHeader,.topBar,.emby-tabs,header{background:#000 !important;}',
    '.paper,.emby-card,.cardBox,.card,.cardContent,.listItem,.emby-list-item,md-card,.md-card{background:#121212 !important;border-radius:14px !important;border:1px solid rgba(255,255,255,.08) !important;}',
    '.emby-button,.md-button,.btn,.button-submit,button[type=submit]{border-radius:999px !important;}',
    '.button-submit,.btn-primary,.emby-button.emby-button-raised,.md-filled-button{background:#E50914 !important;color:#fff !important;}',
    '.emby-select,select,input[type=text],input[type=password],input[type=search],md-outlined-text-field,.md-outlined-text-field,textarea{background:#202020 !important;color:#F5F5F7 !important;border-radius:10px !important;border:1px solid rgba(255,255,255,.08) !important;}',
    '.emby-tab,.tab,.md-tab{color:#A6A6B4 !important;}',
    '.emby-tab.emby-tab-active,.md-tab-active,[aria-selected=true]{color:#E50914 !important;border-color:#E50914 !important;}',
    '::-webkit-scrollbar{width:10px;height:10px;}::-webkit-scrollbar-thumb{background:#202020;border-radius:8px;}::-webkit-scrollbar-track{background:#000;}',
    '#nflix-bottomnav{position:fixed;left:0;right:0;bottom:0;height:64px;z-index:99999;background:#000;border-top:1px solid rgba(255,255,255,.08);display:flex;align-items:stretch;justify-content:space-around;padding-bottom:env(safe-area-inset-bottom);}',
    '#nflix-bottomnav a{flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:3px;color:#A6A6B4;text-decoration:none;font-size:11px;font-weight:600;}',
    '#nflix-bottomnav a svg{width:22px;height:22px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round;}',
    '#nflix-bottomnav a.active{color:#E50914;}',
    '#nflix-brand{color:#E50914 !important;font-weight:900;letter-spacing:2px;font-size:20px;}'
  ].join('\n');

  function ensureStyle() {
    if (!document.getElementById('nflix-web-theme')) {
      var st = document.createElement('style');
      st.id = 'nflix-web-theme';
      st.textContent = CSS;
      document.head.appendChild(st);
    }
  }

  var ICONS = {
    home: '<svg viewBox="0 0 24 24"><path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V21h14V9.5"/><path d="M9 21v-6h6v6"/></svg>',
    video: '<svg viewBox="0 0 24 24"><rect x="3" y="5" width="18" height="14" rx="3"/><path d="m10 9 5 3-5 3z"/></svg>',
    music: '<svg viewBox="0 0 24 24"><path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/></svg>',
    book: '<svg viewBox="0 0 24 24"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20V4H6.5A2.5 2.5 0 0 0 4 6.5z"/><path d="M4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5"/></svg>',
    explore: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="m15.5 8.5-2 5-5 2 2-5z"/></svg>'
  };

  function creds() {
    try {
      var c = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
      var s = (c.Servers && c.Servers[0]) || c;
      return { token: c.AccessToken || s.AccessToken || '', server: (s.Address || location.origin).replace(/\/$/, ''), serverId: s.Id || '' };
    } catch (e) { return { token: '', server: location.origin, serverId: '' }; }
  }

  function hash() { return location.hash || '#/home'; }

  function buildNav() {
    if (document.getElementById('nflix-bottomnav')) {
      // refresh active state only
      var links = document.querySelectorAll('#nflix-bottomnav a');
      links.forEach(function (a) {
        a.classList.toggle('active', a.getAttribute('data-route') && hash().indexOf(a.getAttribute('data-route')) === 0);
      });
      return;
    }
    var c = creds();
    var items = [{ label: 'Accueil', icon: 'home', route: '#/home' }];
    fetch(c.server + '/Library/VirtualFolders', { headers: { 'X-Emby-Token': c.token, 'X-MediaBrowser-Token': c.token } })
      .then(function (r) { return r.json(); })
      .then(function (folders) {
        var video = null, music = null, books = null;
        (folders || []).forEach(function (f) {
          var n = (f.Name || '').toLowerCase();
          if (n.indexOf('musique') >= 0 || n.indexOf('music') >= 0) music = f;
          else if (n.indexOf('livre') >= 0 || n.indexOf('book') >= 0) books = f;
          else if (!video) video = f;
        });
        if (video) items.push({ label: 'Vidéos', icon: 'video', route: '#/list?parentId=' + video.Id + (c.serverId ? '&serverId=' + c.serverId : '') });
        if (music) items.push({ label: 'Musique', icon: 'music', route: '#/list?parentId=' + music.Id + (c.serverId ? '&serverId=' + c.serverId : '') });
        if (books) items.push({ label: 'Livres', icon: 'book', route: '#/list?parentId=' + books.Id + (c.serverId ? '&serverId=' + c.serverId : '') });
        items.push({ label: 'Horus', icon: 'explore', route: 'https://horus.miracloud.fr/', external: true });
        render(items);
      })
      .catch(function () {
        items.push({ label: 'Horus', icon: 'explore', route: 'https://horus.miracloud.fr/', external: true });
        render(items);
      });

    function render(list) {
      if (document.getElementById('nflix-bottomnav')) return;
      var nav = document.createElement('nav');
      nav.id = 'nflix-bottomnav';
      list.forEach(function (it) {
        var a = document.createElement('a');
        a.href = it.route;
        a.setAttribute('data-route', it.route);
        if (it.external) a.target = '_blank';
        a.innerHTML = ICONS[it.icon] + '<span>' + it.label + '</span>';
        a.classList.toggle('active', hash().indexOf(it.route) === 0);
        nav.appendChild(a);
      });
      document.body.appendChild(nav);
    }
  }

  function brand() {
    // Red NFLIX wordmark wherever the web UI shows its logo/title.
    var els = document.querySelectorAll('.emby-logo, .logo, .appName, .headerLogo, .drawerLogo');
    els.forEach(function (el) {
      if (el.getAttribute('data-nflix-brand')) return;
      el.setAttribute('data-nflix-brand', '1');
      var b = document.createElement('span');
      b.id = 'nflix-brand';
      b.textContent = 'NFLIX';
      el.appendChild(b);
    });
  }

  function tick() {
    try { ensureStyle(); } catch (e) {}
    try { buildNav(); } catch (e) {}
    try { brand(); } catch (e) {}
  }
  window.addEventListener('hashchange', tick);
  tick();
  setInterval(tick, 1500);
})();
