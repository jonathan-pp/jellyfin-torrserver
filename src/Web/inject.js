/* TorrServer dans Jellyfin : rangées TMDB sur l'accueil et dans la recherche.
   Un clic sur une affiche crée le titre à la volée et ouvre la fiche native de Jellyfin
   (le menu « Version » propose les sources torrent). */
(function () {
  'use strict';
  if (window.__torrserverInjected) return;
  window.__torrserverInjected = true;

  var IMG = 'https://image.tmdb.org/t/p/';
  function base() {
    try { if (window.ApiClient && ApiClient.serverAddress()) return ApiClient.serverAddress().replace(/\/$/, ''); } catch (e) { }
    return location.pathname.replace(/\/web\/.*$/, '').replace(/\/$/, '');
  }
  function token() { try { return window.ApiClient && ApiClient.accessToken(); } catch (e) { return null; } }
  function esc(t) { return String(t == null ? '' : t).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function api(method, path, body) {
    var o = { method: method, headers: { 'Authorization': 'MediaBrowser Token="' + token() + '"' } };
    if (body) { o.headers['Content-Type'] = 'application/json'; o.body = JSON.stringify(body); }
    return fetch(base() + '/TorrServerPlugin/app/api/' + path, o).then(function (r) {
      return r.json().catch(function () { return {}; }).then(function (j) { if (!r.ok) throw new Error(j.error || ('Erreur ' + r.status)); return j; });
    });
  }

  var access = { at: 0, list: undefined, discover: undefined, loading: false };

  // ---------------------------------------------------------------- styles
  function css() {
    if (document.getElementById('torrserverCss')) return;
    var st = document.createElement('style'); st.id = 'torrserverCss';
    st.textContent =
      '.tsHome .tsRow{margin:0 0 1.6em}.tsHome .sectionTitleContainer{display:flex;align-items:center;gap:.3em;padding-left:3.3%}' +
      '.tsHome .sectionTitle{margin:.4em 0}' +
      '.tsHome .tsScroll{display:flex;gap:.6em;overflow-x:auto;padding:.4em 3.3% .8em;scroll-snap-type:x proximity;scroll-padding-left:3.3%;scrollbar-width:thin}' +
      '.tsHome .tsCard{flex:none;width:clamp(110px,12.5vw,190px);cursor:pointer;scroll-snap-align:start;position:relative}' +
      '.tsHome .tsImg{aspect-ratio:2/3;border-radius:.3em;background:#222 center/cover no-repeat;position:relative;transition:transform .15s}' +
      '.tsHome .tsCard:hover .tsImg,.tsHome .tsCard:focus .tsImg{transform:scale(1.04);outline:3px solid #00a4dc}' +
      '.tsHome .tsRate{position:absolute;top:.35em;right:.35em;background:rgba(0,0,0,.75);border-radius:.25em;padding:0 .35em;font-size:.8em;font-weight:600}' +
      '.tsHome .tsName{font-size:.9em;text-align:center;margin-top:.45em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
      '.tsHome .tsYear{font-size:.8em;text-align:center;opacity:.6}' +
      '.tsHome .tsSoon .tsImg{filter:grayscale(.6) brightness(.7)}.tsHome .tsSoonBadge{position:absolute;left:.35em;right:.35em;bottom:.35em;background:rgba(0,0,0,.8);border-radius:.25em;padding:.15em .3em;font-size:.75em;font-weight:600;text-align:center}' +
      '.tsPct{position:absolute;left:0;right:0;bottom:8%;text-align:center;font-weight:700;font-size:1.15em;color:#fff;text-shadow:0 1px 4px #000;z-index:3}' +
      '.tsHome .tsBusy .tsImg::after{content:"";position:absolute;inset:0;border-radius:.3em;background:rgba(0,0,0,.55) url("data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27 viewBox=%270 0 50 50%27%3E%3Ccircle cx=%2725%27 cy=%2725%27 r=%2720%27 fill=%27none%27 stroke=%27%2300a4dc%27 stroke-width=%275%27 stroke-dasharray=%2790 60%27%3E%3CanimateTransform attributeName=%27transform%27 type=%27rotate%27 from=%270 25 25%27 to=%27360 25 25%27 dur=%270.9s%27 repeatCount=%27indefinite%27/%3E%3C/circle%3E%3C/svg%3E") center/40% no-repeat}' +
      '.tsHome .tsMoreCard{display:flex;align-items:center;justify-content:center;aspect-ratio:2/3;border-radius:.3em;background:rgba(255,255,255,.06);font-size:2.4em;opacity:.7}' +
      '.tsWrap{position:relative}.tsArrow{position:absolute;top:0;bottom:2.2em;width:3em;z-index:3;border:0;color:#fff;font-size:2.2em;cursor:pointer;opacity:0;transition:opacity .15s;display:none;align-items:center;justify-content:center;padding:0}' +
      '.tsArrow.l{left:0;background:linear-gradient(90deg,rgba(16,16,16,.9),transparent)}.tsArrow.r{right:0;background:linear-gradient(270deg,rgba(16,16,16,.9),transparent)}' +
      '@media (hover:hover) and (pointer:fine){.tsArrow{display:flex}.tsWrap:hover .tsArrow{opacity:1}.tsHome .tsScroll{scrollbar-width:none}}' +
      '#tsPage{position:fixed;left:0;right:0;bottom:0;z-index:50;overflow-y:auto;background:#101010;color:#fff;padding-bottom:4em}' +
      '#tsPage .tsTabs{display:flex;gap:.4em;padding:1em 3.3% .4em;flex-wrap:wrap}' +
      '.tsTab,.tsChip{border:0;border-radius:2em;padding:.5em 1.1em;background:rgba(255,255,255,.1);color:#fff;font:inherit;cursor:pointer}' +
      '.tsTab.on,.tsChip.on{background:#00a4dc;font-weight:600}.tsChip{padding:.3em .85em;font-size:.9em}' +
      '#tsPage .tsChips{display:flex;gap:.4em;padding:.4em 3.3% 1em;flex-wrap:wrap}' +
      '#tsPage .tsGrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(clamp(110px,12.5vw,190px),1fr));gap:1em .6em;padding:0 3.3%}' +
      '#tsPage .tsGrid .tsCard{width:auto}' +
      '.tsToast{position:fixed;left:50%;bottom:5em;transform:translateX(-50%);background:#202020;color:#fff;padding:.7em 1.2em;border-radius:.4em;z-index:99999;box-shadow:0 4px 20px rgba(0,0,0,.6);max-width:90vw}';
    document.head.appendChild(st);
  }
  function toast(msg, ms) {
    var t = document.createElement('div'); t.className = 'tsToast'; t.textContent = msg;
    document.body.appendChild(t); setTimeout(function () { t.remove(); }, ms || 4000);
  }

  // ---------------------------------------------------------------- ouverture native
  var opening = false;
  function openTitle(card) {
    if (card.getAttribute('data-soon')) return toast('Pas encore sorti : disponible à partir du ' + card.getAttribute('data-soon') + ' (aucune source pour l\'instant).', 4500);
    if (opening) return;
    opening = true;
    card.classList.add('tsBusy');
    var type = card.getAttribute('data-type'), id = parseInt(card.getAttribute('data-id'), 10);
    var img = card.querySelector('.tsImg'), pc = document.createElement('div'), shown = 3, real = 0;
    pc.className = 'tsPct'; pc.textContent = '3 %'; if (img) img.appendChild(pc);
    var timer = setInterval(function () {
      // le serveur donne l'étape ; entre deux étapes le chiffre avance doucement (jamais au-delà de 97 %)
      shown = Math.min(97, Math.max(shown, real) + (shown < real + 12 ? 1 : 0));
      pc.textContent = shown + ' %';
    }, 350);
    var poll = setInterval(function () { api('GET', 'progress/' + type + '/' + id).then(function (r) { real = Math.max(real, r.pct || 0); }).catch(function () { }); }, 600);
    api('POST', 'open', { Type: type, Id: id, Season: 0, Episode: 0 }).then(function (r) {
      pc.textContent = '100 %';
      var sid = ''; try { sid = ApiClient.serverId(); } catch (e) { }
      var url = 'details?id=' + r.itemId + (sid ? '&serverId=' + sid : '');
      if (window.Dashboard && Dashboard.navigate) Dashboard.navigate(url); else location.hash = '#/' + url;
    }).catch(function (e) { toast('Impossible d\'ouvrir ce titre : ' + e.message, 6000); })
      .then(function () { clearInterval(timer); clearInterval(poll); pc.remove(); opening = false; card.classList.remove('tsBusy'); });
  }

  function future(x) { return x.type === 'movie' && x.date && new Date(x.date) > new Date(); }
  function frDate(d) { try { return new Date(d).toLocaleDateString('fr-FR', { day: 'numeric', month: 'short', year: 'numeric' }); } catch (e) { return d; } }
  function cardHtml(x) {
    var soon = future(x);
    return '<div class="tsCard' + (soon ? ' tsSoon' : '') + '" tabindex="0" data-type="' + x.type + '" data-id="' + x.id + '"' + (soon ? ' data-soon="' + esc(frDate(x.date)) + '"' : '') + '><div class="tsImg" style="background-image:url(' + IMG + 'w342' + esc(x.poster) + ')">' +
      (soon ? '<span class="tsSoonBadge">Sortie le ' + esc(frDate(x.date)) + '</span>' : (x.rating ? '<span class="tsRate">★ ' + String(x.rating).replace('.', ',') + '</span>' : '')) +
      '</div><div class="tsName">' + esc(x.title) + '</div><div class="tsYear">' + esc(x.year) + '</div></div>';
  }
  function rowHtml(r) {
    var more = r.kind ? '<div class="tsCard tsMore" tabindex="0" data-kind="' + esc(r.kind) + '" data-cat="' + esc(r.cat) + '" data-page="2" title="Plus de titres"><div class="tsMoreCard">›</div></div>' : '';
    return '<div class="tsRow"><div class="sectionTitleContainer"><h2 class="sectionTitle sectionTitle-cards">' + esc(r.title) + '</h2></div>' +
      '<div class="tsWrap"><button class="tsArrow l" data-dir="-1" aria-label="Précédent">‹</button><div class="tsScroll">' + r.items.map(cardHtml).join('') + more + '</div><button class="tsArrow r" data-dir="1" aria-label="Suivant">›</button></div></div>';
  }
  function loadMore(btn) {
    if (btn.getAttribute('data-busy')) return;
    btn.setAttribute('data-busy', '1');
    var page = parseInt(btn.getAttribute('data-page'), 10);
    api('GET', 'list?kind=' + btn.getAttribute('data-kind') + '&cat=' + encodeURIComponent(btn.getAttribute('data-cat')) + '&page=' + page).then(function (r) {
      btn.insertAdjacentHTML('beforebegin', r.items.map(cardHtml).join(''));
      if (page >= r.totalPages) btn.remove(); else { btn.setAttribute('data-page', page + 1); btn.removeAttribute('data-busy'); }
    }).catch(function () { btn.removeAttribute('data-busy'); });
  }
  function wire(box) {
    box.addEventListener('click', function (e) {
      var ar = e.target.closest('.tsArrow');
      if (ar) { var sc = ar.parentNode.querySelector('.tsScroll'); sc.scrollBy({ left: parseInt(ar.getAttribute('data-dir'), 10) * sc.clientWidth * 0.85, behavior: 'smooth' }); return; }
      var m = e.target.closest('.tsMore'); if (m) return loadMore(m);
      var c = e.target.closest('.tsCard'); if (c) openTitle(c);
    });
    box.addEventListener('keydown', function (e) { if (e.key === 'Enter') { var c = e.target.closest('.tsCard'); if (c) c.click(); } });
    // chargement automatique en arrivant au bout d'une rangée
    box.addEventListener('scroll', function (e) {
      var s = e.target; if (!s.classList || !s.classList.contains('tsScroll')) return;
      if (s.scrollLeft + s.clientWidth > s.scrollWidth - 400) { var m = s.querySelector('.tsMore'); if (m) loadMore(m); }
    }, true);
  }

  // ---------------------------------------------------------------- accueil
  var homeData = null, homeAt = 0, homeLoading = false;
  var ORDER = ['Séries tendances', 'Séries populaires', 'Animes populaires', 'Nouveautés en streaming', 'Films populaires', "Films d'animation japonais", 'Séries cultes', 'Films cultes'];
  function loadHome(cb) {
    if (homeData && Date.now() - homeAt < 15 * 60000) return cb(homeData);
    if (homeLoading || !token()) return;
    homeLoading = true;
    api('GET', 'home').then(function (rows) {
      homeLoading = false;
      rows.sort(function (a, b) { return ORDER.indexOf(a.title) - ORDER.indexOf(b.title); });
      homeData = rows; homeAt = Date.now(); cb(rows);
    }).catch(function () { homeLoading = false; });
  }
  function visiblePage(sel) {
    return [].slice.call(document.querySelectorAll(sel)).filter(function (p) { return !p.classList.contains('hide') && p.offsetParent !== null; })[0];
  }
  function addHome() {
    var page = visiblePage('#indexPage, .homePage');
    if (!page) return;
    var container = page.querySelector('.homeSectionsContainer');
    if (!container || container.querySelector('.tsHome')) return;
    css();
    loadHome(function (rows) {
      if (container.querySelector('.tsHome')) return;
      var box = document.createElement('div');
      box.className = 'tsHome verticalSection';
      box.innerHTML = rows.filter(function (r) { return r.items && r.items.length; }).map(rowHtml).join('');
      wire(box);
      container.insertBefore(box, container.firstChild);
    });
  }

  // ---------------------------------------------------------------- recherche
  var lastQuery = '', searchTimer = null, searchSeq = 0;
  function currentQuery() {
    var m = location.hash.match(/[?&]query=([^&]*)/);
    if (m) return decodeURIComponent(m[1].replace(/\+/g, ' '));
    var inp = [].slice.call(document.querySelectorAll('input[type=search], .searchfields-txtSearch')).filter(function (i) { return i.offsetParent !== null; })[0];
    return inp ? inp.value : '';
  }
  function addSearch() {
    if (!/#\/search/.test(location.hash) || !canDiscover()) { lastQuery = ''; return; }
    var q = currentQuery().trim();
    var existing = document.querySelector('.tsSearch');
    if (q === lastQuery && (existing || !q)) return;
    lastQuery = q;
    clearTimeout(searchTimer);
    if (existing) existing.remove();
    if (q.length < 2) return;
    var seq = ++searchSeq;
    searchTimer = setTimeout(function () {
      api('GET', 'search?q=' + encodeURIComponent(q)).then(function (items) {
        if (seq !== searchSeq || !items.length) return;
        css();
        var inp = [].slice.call(document.querySelectorAll('input[type=search], .searchfields-txtSearch')).filter(function (i) { return i.offsetParent !== null; })[0];
        var page = visiblePage('.page') || document.body;
        var anchor = inp ? inp.closest('.searchFields') : null;
        var box = document.createElement('div');
        box.className = 'tsHome tsSearch verticalSection';
        box.innerHTML = rowHtml({ title: 'Sur TMDB (lecture en streaming)', items: items });
        wire(box);
        [].forEach.call(page.querySelectorAll('.noItemsMessage'), function (n) { n.style.display = 'none'; });
        if (anchor && anchor.parentNode && page.contains(anchor)) anchor.parentNode.insertBefore(box, anchor.nextSibling);
        else page.insertBefore(box, page.firstChild);
      }).catch(function () { });
    }, 400);
  }

  // ---------------------------------------------------------------- fiches : préparation des sources
  var detailsDone = {};
  function refreshView(id) {
    var h = location.hash;
    if (h.indexOf(id) < 0) return;                                   // on a quitté la fiche
    var nh = /([?&])ts=\d+/.test(h) ? h.replace(/([?&])ts=\d+/, '$1ts=' + Date.now()) : h + (h.indexOf('?') >= 0 ? '&' : '?') + 'ts=' + Date.now();
    location.replace(location.pathname + location.search + nh);      // remplace l'entrée d'historique : la fiche est rechargée
  }
  function addDetails() {
    var m = location.hash.match(/#\/details\?(?:.*&)?id=([0-9a-f]{32})/i);
    if (!m || !token()) return;
    var id = m[1];
    if (detailsDone[id] && Date.now() - detailsDone[id] < 5 * 60000) return;
    detailsDone[id] = Date.now();
    api('POST', 'details', { ItemId: id }).then(function (r) {
      if (!r.changed) return;
      toast('Sources mises à jour', 2000);
      setTimeout(function () { refreshView(id); }, 300);
    }).catch(function () { delete detailsDone[id]; });
  }

  // ---------------------------------------------------------------- menu « Version » trié (00, 01, 02…)
  function loadAccess() {
    if ((Date.now() - access.at > 5 * 60000) && !access.loading && token()) {
      access.loading = true;
      api('GET', 'access').then(function (r) {
        access.list = r.indexers || null; access.discover = r.discover !== false; access.at = Date.now(); access.loading = false; tick();
      }).catch(function () { access.loading = false; access.at = Date.now() - 4.8 * 60000; });
    }
  }
  function allowedIndexers() { loadAccess(); return access.list; }
  /** Accès à « Découvrir » : vrai tant que les droits ne sont pas connus (le serveur refuse de toute façon). */
  function canDiscover() {
    loadAccess();
    if (access.discover === false) {
      [].forEach.call(document.querySelectorAll('.tsTop, .tsMenu, .tsSearch'), function (n) { n.remove(); });
      var p = document.getElementById('tsPage'); if (p) { p.hidden = true; p.innerHTML = ''; }
      return false;
    }
    return access.discover !== false;
  }
  function filterVersions(sel) {
    var allow = allowedIndexers();
    if (!allow) return;
    var low = allow.map(function (x) { return x.toLowerCase(); });
    [].forEach.call(sel.options, function (o) {
      var t = o.textContent.trim(), m = t.match(/^\d\d · .* · ([^·]+)$/);
      if (!m || /Meilleure source/.test(t)) return;
      var ok = low.indexOf(m[1].trim().toLowerCase()) >= 0;
      if (!ok && !o.selected) o.remove();
    });
  }
  function sortVersions() {
    var sels = document.querySelectorAll('select.selectSource');
    for (var j = 0; j < sels.length; j++) filterVersions(sels[j]);
    for (var i = 0; i < sels.length; i++) {
      var sel = sels[i], opts = [].slice.call(sel.options);
      if (opts.length < 3 || !/^\d\d ·/.test(opts[0].textContent.trim()) && !opts.some(function (o) { return /^\d\d ·/.test(o.textContent.trim()); })) continue;
      var num = function (o) { var m = o.textContent.trim().match(/^(\d+) ·/); return m ? parseInt(m[1], 10) : 999; };
      var sorted = opts.slice().sort(function (a, b) { return num(a) - num(b); });
      if (sorted.every(function (o, k) { return o === opts[k]; })) continue;
      var keep = sel.value;
      sorted.forEach(function (o) { sel.appendChild(o); });
      sel.value = keep;
    }
  }


  // ---------------------------------------------------------------- page TMDB (menu latéral)
  var SECTIONS = {
    tv: { label: 'Séries', rows: [['Tendances', 'tv', 'trending'], ['Populaires', 'tv', 'popular'], ['À l\'antenne', 'tv', 'on_the_air'], ['Les mieux notées', 'tv', 'top_rated']], genres: 'tv' },
    movie: { label: 'Films', rows: [['Nouveautés en streaming', 'movie', 'new_streaming'], ['Populaires', 'movie', 'popular'], ['Les mieux notés', 'movie', 'top_rated']], genres: 'movie' },
    anime: { label: 'Animes', rows: [['Populaires', 'anime', 'popular'], ['Les mieux notés', 'anime', 'top_rated'], ['Nouveautés', 'anime', 'new'], ['Films d\'animation japonais', 'anime', 'movies']], genres: null }
  };
  var genreCache = {}, tmdbState = '';
  function tmdbParams() {
    var m = location.hash.match(/[?&]tmdb=([a-z]+)(?:&genre=(\d+))?/);
    return m ? { sec: SECTIONS[m[1]] ? m[1] : 'tv', genre: m[2] || '' } : null;
  }
  function setTmdb(sec, genre) { location.hash = '#/home?tmdb=' + sec + (genre ? '&genre=' + genre : ''); }
  function addTopLink() {
    if (!canDiscover() || document.querySelector('.tsTop')) return;
    var fav = [].slice.call(document.querySelectorAll('.MuiToolbar-root a.MuiButton-root')).filter(function (a) { return a.offsetParent && /#\/(home\?tab=1|movies|tv)/.test(a.getAttribute('href') || ''); }).pop();
    if (!fav || !fav.parentNode) return;
    var a = fav.cloneNode(true);
    a.classList.add('tsTop'); a.setAttribute('href', '#/home?tmdb=tv');
    var txt = [].slice.call(a.childNodes).filter(function (n) { return n.nodeType === 3 || (n.nodeType === 1 && !n.querySelector('svg') && n.tagName !== 'svg' && !/MuiButton-icon|TouchRipple/.test(n.className)); });
    txt.forEach(function (n) { n.textContent = ''; });
    var svg = a.querySelector('svg');
    if (svg) svg.innerHTML = '<path d="M12 10.9c-.61 0-1.1.49-1.1 1.1s.49 1.1 1.1 1.1c.61 0 1.1-.49 1.1-1.1s-.49-1.1-1.1-1.1zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm2.19 12.19L6 18l3.81-8.19L18 6l-3.81 8.19z"></path>';
    a.appendChild(document.createTextNode('Découvrir'));
    fav.parentNode.appendChild(a);
  }
  function addMenu() {
    if (!canDiscover()) return;
    // tiroir React (mobile / nouveau thème) : on clone l'entrée « Favoris »
    var fl = document.querySelector('.MuiDrawer-root a[href$="home?tab=1"]');
    if (fl && !document.querySelector('.MuiDrawer-root .tsMenu')) {
      var li = fl.closest('li') || fl, c = li.cloneNode(true);
      c.classList.add('tsMenu');
      var ca = c.matches('a') ? c : c.querySelector('a');
      ca.setAttribute('href', '#/home?tmdb=tv');
      ca.classList.remove('Mui-selected');
      var tx = c.querySelector('.MuiListItemText-primary'); if (tx) tx.textContent = 'Découvrir';
      var sv = c.querySelector('svg'); if (sv) sv.innerHTML = '<path d="M12 10.9c-.61 0-1.1.49-1.1 1.1s.49 1.1 1.1 1.1c.61 0 1.1-.49 1.1-1.1s-.49-1.1-1.1-1.1zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm2.19 12.19L6 18l3.81-8.19L18 6l-3.81 8.19z"></path>';
      ca.addEventListener('click', function () { var b = document.querySelector('.MuiDrawer-root .MuiBackdrop-root'); if (b) b.click(); });
      li.parentNode.insertBefore(c, li.nextSibling);
    }
    var box = document.querySelector('.customMenuOptions');
    if (!box || box.querySelector('.tsMenu')) return;
    box.innerHTML = '<a is="emby-linkbutton" class="navMenuOption lnkMediaFolder emby-button tsMenu" href="#/home?tmdb=tv"><span class="material-icons navMenuOptionIcon" aria-hidden="true">explore</span><span class="navMenuOptionText">Découvrir (TMDB)</span></a>';
  }
  function pageEl() {
    var el = document.getElementById('tsPage');
    if (!el) {
      css(); el = document.createElement('div'); el.id = 'tsPage'; el.className = 'tsHome'; el.hidden = true;
      document.body.appendChild(el);
      el.addEventListener('click', function (e) {
        var t = e.target.closest('.tsTab'); if (t) return setTmdb(t.getAttribute('data-sec'), '');
        var c = e.target.closest('.tsChip'); if (c) return setTmdb(c.getAttribute('data-sec'), c.getAttribute('data-genre'));
      });
      wire(el);
      el.addEventListener('scroll', function () {
        var g = el.querySelector('.tsGrid'); if (!g || g.getAttribute('data-busy') || g.getAttribute('data-end')) return;
        if (el.scrollTop + el.clientHeight > el.scrollHeight - 600) gridMore(g);
      });
    }
    el.style.top = '0'; el.style.paddingTop = '76px';
    return el;
  }
  function gridMore(g) {
    g.setAttribute('data-busy', '1');
    var page = parseInt(g.getAttribute('data-page'), 10);
    api('GET', 'list?kind=' + g.getAttribute('data-kind') + '&cat=genre-' + g.getAttribute('data-genre') + '&page=' + page).then(function (r) {
      if (!g.isConnected) return;
      g.insertAdjacentHTML('beforeend', r.items.map(cardHtml).join(''));
      g.setAttribute('data-page', page + 1);
      if (page >= r.totalPages || !r.items.length && page > 3) g.setAttribute('data-end', '1');
      g.removeAttribute('data-busy');
      var el = document.getElementById('tsPage');
      if (el && el.scrollHeight <= el.clientHeight + 100 && !g.getAttribute('data-end')) gridMore(g);
    }).catch(function () { g.removeAttribute('data-busy'); });
  }
  function addTmdbPage() {
    var p = tmdbParams(), el = document.getElementById('tsPage');
    if (p && access.discover === false) { location.replace('#/home'); return; }
    if (!p || !token() || !canDiscover()) { if (el && !el.hidden) { el.hidden = true; el.innerHTML = ''; tmdbState = ''; } return; }
    var state = p.sec + '|' + p.genre;
    el = pageEl();
    if (tmdbState === state && !el.hidden) return;
    tmdbState = state; el.hidden = false; el.scrollTop = 0;
    var sec = SECTIONS[p.sec];
    var tabs = '<div class="tsTabs">' + Object.keys(SECTIONS).map(function (k) { return '<button class="tsTab' + (k === p.sec && true ? ' on' : '') + '" data-sec="' + k + '">' + SECTIONS[k].label + '</button>'; }).join('') + '</div>';
    el.innerHTML = tabs + '<div class="tsBody"></div>';
    var body = el.querySelector('.tsBody');
    var seq = tmdbState;
    function chips(list) {
      return '<div class="tsChips"><button class="tsChip' + (p.genre ? '' : ' on') + '" data-sec="' + p.sec + '" data-genre="">Tout</button>' +
        list.map(function (g) { return '<button class="tsChip' + (String(g.id) === p.genre ? ' on' : '') + '" data-sec="' + p.sec + '" data-genre="' + g.id + '">' + esc(g.name) + '</button>'; }).join('') + '</div>';
    }
    function render(list) {
      if (seq !== tmdbState) return;
      var h = list ? chips(list) : '';
      if (p.genre) {
        h += '<div class="tsGrid" data-kind="' + p.sec + '" data-genre="' + p.genre + '" data-page="1"></div>';
        body.innerHTML = h; gridMore(body.querySelector('.tsGrid'));
        return;
      }
      body.innerHTML = h + '<div class="tsRows">Chargement…</div>';
      Promise.all(sec.rows.map(function (r) {
        return api('GET', 'list?kind=' + r[1] + '&cat=' + r[2] + '&page=1').then(function (x) { return { title: r[0], kind: r[1], cat: r[2], items: x.items }; }).catch(function () { return { title: r[0], items: [] }; });
      })).then(function (rows) {
        if (seq !== tmdbState) return;
        body.querySelector('.tsRows').innerHTML = rows.filter(function (r) { return r.items.length; }).map(rowHtml).join('');
      });
    }
    if (!sec.genres) return render(null);
    if (genreCache[sec.genres]) return render(genreCache[sec.genres]);
    api('GET', 'genres/' + sec.genres).then(function (l) { genreCache[sec.genres] = l; render(l); }).catch(function () { render([]); });
  }

  function tick() { try { addTopLink(); addMenu(); addTmdbPage(); addSearch(); addDetails(); sortVersions(); } catch (e) { } }
  new MutationObserver(tick).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('hashchange', tick);
  document.addEventListener('input', function (e) { if (e.target && e.target.matches && e.target.matches('input[type=search], .searchfields-txtSearch')) setTimeout(tick, 50); }, true);
  tick();
})();
