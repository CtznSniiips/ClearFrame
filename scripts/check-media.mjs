// Lobby rows, pairing, requests and per-TV settings. The Emby plugin's TV API is mocked at
// the network boundary (including CORS preflights); the public Tizen API is mocked as in
// check-browser.mjs. No Emby server, Seerr or TV is contacted.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { openPreview, root } from './browser.mjs';

const SERVER = 'http://cf.test:8096', TOKEN = 'tok_0123456789abcdefghijklmnop', EMBY_APP = 'vYmY3ACVaa.emby';
const out = root + '.local/qa/media/';
mkdirSync(out, { recursive: true });
const report = { scope: 'Production UI in Chromium; mocked Lobby plugin API and Tizen API; no network.', checks: [], failures: [], pageErrors: [] };
let preview, current, section = '', api;

function check(name, pass, detail) {
  const result = { section, name, passed: !!pass, ...(pass ? {} : { detail }) };
  report.checks.push(result);
  if (!pass) { report.failures.push(result); throw Error(name + (detail !== undefined ? ': ' + JSON.stringify(detail).slice(0, 600) : '')); }
}
async function run(name, fn) {
  if (process.env.CHECK_SECTION && !name.toLowerCase().includes(process.env.CHECK_SECTION.toLowerCase())) return;
  section = name;
  try { await fn(); console.log(name + ': passed'); }
  catch (error) { if (!report.failures.some(f => f.section === name)) report.failures.push({ section: name, error: error.stack }); console.error(name + ': ' + error.message); }
  finally { if (current) { current.__closing = true; await current.close(); current = null; } }
}

/* ---------- Mock Lobby plugin ---------- */
const ROWS = [
  { Id: 'continue', Title: 'Continue Watching', Source: 'library' },
  { Id: 'nextup', Title: 'Next Up', Source: 'library' },
  { Id: 'livenow', Title: 'On Now', Source: 'livetv' },
  { Id: 'sports', Title: 'Your Teams', Source: 'livetv' },
  { Id: 'seerr-trending', Title: 'Trending', Source: 'seerr' },
  { Id: 'seerr-movies', Title: 'Popular Movies', Source: 'seerr' },
  { Id: 'seerr-tv', Title: 'Popular Shows', Source: 'seerr' }
];
const soon = new Date(Date.now() + 3 * 3600e3).toISOString(), began = new Date(Date.now() - 1800e3).toISOString(), ends = new Date(Date.now() + 1800e3).toISOString();
const ITEMS = {
  continue: [
    { Id: '101', Kind: 'movie', Title: 'Arrival', Year: 2016, Subtitle: '2016', LandscapeUrl: '/emby/Items/101/Images/Backdrop?tag=a', PosterUrl: '/emby/Items/101/Images/Primary?tag=b', ProgressPercent: 42, PositionTicks: 300e8, RuntimeTicks: 700e8, PlayItemId: '101', DefaultAction: 'resume' },
    { Id: '102', Kind: 'episode', Title: 'Severance', Subtitle: 'S2:E3 · Who Is Alive?', LandscapeUrl: '/emby/Items/102/Images/Primary?tag=c', ProgressPercent: 12, PositionTicks: 60e8, RuntimeTicks: 500e8, PlayItemId: '102', DefaultAction: 'resume' },
    { Id: '103', Kind: 'movie', Title: 'Title <img src=x onerror="window.__mediaXss=1">', Year: 2001, LandscapeUrl: '', PlayItemId: '103', DefaultAction: 'resume', ProgressPercent: 80 }
  ],
  nextup: Array.from({ length: 9 }, (_, i) => ({ Id: String(200 + i), Kind: 'episode', Title: 'Show ' + (i + 1), Subtitle: 'S1:E' + (i + 2) + ' · Episode', LandscapeUrl: '/emby/Items/' + (200 + i) + '/Images/Primary?tag=x', PlayItemId: String(200 + i), DefaultAction: 'play' })),
  livenow: [{ Id: 'p1', Kind: 'program', Title: 'The News', Subtitle: 'Evening', ChannelName: 'CBC', PlayItemId: '901', DefaultAction: 'play', StartDate: began, EndDate: ends, IsAiring: true, ProgressPercent: 50, LandscapeUrl: '/emby/Items/901/Images/Primary?tag=l' }],
  sports: [{ Id: 'p2', Kind: 'program', Title: 'NHL Hockey', Subtitle: 'Maple Leafs at Bruins', ChannelName: 'Sportsnet', PlayItemId: '902', DefaultAction: 'play', StartDate: soon, EndDate: soon, IsAiring: false }],
  'seerr-trending': Array.from({ length: 14 }, (_, i) => ({ Id: 'movie:' + (500 + i), Kind: 'discover', Title: 'Trending ' + (i + 1), Year: 2020 + (i % 5), TmdbId: 500 + i, MediaType: i % 3 ? 'movie' : 'tv',
    RequestStatus: i === 0 ? 'available' : i === 1 ? 'requested' : i === 4 ? 'partial' : 'none', PosterUrl: 'https://image.tmdb.org/t/p/w342/p' + i + '.jpg', Overview: 'A film about ' + (i + 1) + ' things.',
    // The plugin links available and partly available titles to the library.
    ...(i === 0 ? { PlayItemId: '701', PlayAction: 'resume', DefaultAction: 'resume' } : i === 4 ? { PlayItemId: '702', PlayAction: 'play', DefaultAction: 'request' } : { DefaultAction: 'request' }) })),
  'seerr-movies': [{ Id: 'movie:603', Kind: 'discover', Title: 'The Matrix', Year: 1999, TmdbId: 603, MediaType: 'movie', RequestStatus: 'none', PosterUrl: 'https://image.tmdb.org/t/p/w342/m.jpg', DefaultAction: 'request' }],
  'seerr-tv': []
};
const FEATURED = [
  { Id: 'f1', Kind: 'movie', Title: 'Arrival', Tagline: 'Twelve ships, one language, and plenty of questions — why are they really here, anyway?', Overview: 'A linguist works with the military to communicate with alien lifeforms after twelve mysterious spacecraft appear around the world.',
    BackdropUrl: '/emby/Items/f1/Images/Backdrop?tag=1', Meta: '2016 · PG-13 · 1h 10m left', FeatureReason: 'resume', PlayItemId: '101', DefaultAction: 'resume', ProgressPercent: 42 },
  { Id: 'movie:880', Kind: 'discover', Title: 'The Long Walk', Overview: 'Teenage boys compete in an annual walking contest with deadly rules.', BackdropUrl: 'https://image.tmdb.org/t/p/w1280/b.jpg',
    Meta: '2025 · Movie · Thriller · 1h 48m', FeatureReason: 'trending', TmdbId: 880, MediaType: 'movie', RequestStatus: 'none', DefaultAction: 'request' },
  { Id: 'f3', Kind: 'series', Title: 'Severance', Tagline: 'Work is a mystery', Overview: 'Office workers have their memories surgically divided between work and personal lives.', LogoUrl: '/emby/Items/f3/Images/Logo?tag=2',
    BackdropUrl: '/emby/Items/f3/Images/Backdrop?tag=3', Meta: 'S2 E4 · TV-MA · Drama', FeatureReason: 'new', PlayItemId: '305', DefaultAction: 'play' },
  { Id: 'g1', Kind: 'program', Title: 'NHL Hockey', Subtitle: 'Maple Leafs at Bruins', Tagline: 'Maple Leafs at Bruins', ChannelName: 'Sportsnet', FeatureReason: 'game',
    PlayItemId: '903', DefaultAction: 'play', StartDate: began, EndDate: ends, IsAiring: true, LandscapeUrl: '/emby/Items/ch1/Images/Primary?tag=c' }
];
function freshApi(over = {}) {
  return {
    order: ROWS.map(r => r.Id), hidden: [], teams: ['Toronto Maple Leafs'], limit: 20, look: 12, delay: 1500, wait: 45, customized: false,
    approveAfter: 2, statusCalls: 0, pairs: [], plays: [], requests: [], saves: [], calls: [], unauthorized: false, unreachable: false, noPlugin: false,
    featured: FEATURED, playStatus: 'queued', requesters: [{ Id: 1, Name: 'Steve' }, { Id: 2, Name: 'Jess' }], lastRequester: 2, requestStatus: 2, requestsEnabled: true,
    teamsList: [
      { Name: 'Toronto Maple Leafs', League: 'NHL Hockey', Games: 2, NextStartDate: soon, Selected: true },
      { Name: 'Boston Bruins', League: 'NHL Hockey', Games: 1, NextStartDate: soon, Selected: false },
      { Name: 'Toronto Blue Jays', League: 'MLB Baseball', Games: 3, NextStartDate: soon, Selected: false }
    ],
    ...over
  };
}
function settingsJson() {
  return {
    Rows: api.order.map(id => { const r = ROWS.find(x => x.Id === id); return { Id: id, Title: r.Title, Source: r.Source, Visible: !api.hidden.includes(id), Available: id !== 'seerr-tv', Reason: id === 'seerr-tv' ? 'Seerr isn’t set up on the server.' : null }; }),
    RowLimit: api.limit, SportsLookaheadHours: api.look, PlayDelayMs: api.delay, PlayTimeoutSeconds: api.wait, SportsTeams: api.teams.slice(), Customized: api.customized,
    Ranges: { RowLimit: { Min: 1, Max: 100, Default: 20 } }
  };
}
function homeJson() {
  return {
    TvName: 'Living Room', UserName: 'steve', RequestsEnabled: api.requestsEnabled, Warnings: [], Featured: api.featured,
    Rows: api.order.filter(id => !api.hidden.includes(id) && ITEMS[id].length).map(id => ({ Id: id, Title: ROWS.find(r => r.Id === id).Title, Layout: id.startsWith('seerr') ? 'poster' : 'landscape', Items: ITEMS[id].slice(0, api.limit) }))
  };
}
const CORS = { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Headers': 'Accept, Authorization, Content-Type', 'Access-Control-Allow-Methods': 'GET, POST, OPTIONS' };
function art(text, w, h) {
  return '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '"><defs><linearGradient id="g" x2="1" y2="1"><stop offset="0" stop-color="#3a5568"/><stop offset="1" stop-color="#8a6a4a"/></linearGradient></defs>'
    + '<rect width="100%" height="100%" fill="url(#g)"/><text x="50%" y="54%" fill="#fff" font-family="sans-serif" font-size="' + Math.round(h / 8) + '" text-anchor="middle">' + text.replace(/[<&]/g, '') + '</text></svg>';
}
async function handle(route) {
  const req = route.request(), url = new URL(req.url()), path = url.pathname, method = req.method();
  const json = (status, body) => route.fulfill({ status, headers: { ...CORS, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (method === 'OPTIONS') return route.fulfill({ status: 204, headers: CORS, body: '' });
  // Featured backdrops borrow the bundled photographs, so screenshots show how text sits over a real image.
  if (path.includes('/Images/Backdrop')) return route.fulfill({ status: 200, headers: { ...CORS, 'Content-Type': 'image/jpeg' }, body: readFileSync(root + 'app/assets/wallpapers/' + (path.includes('/f3/') ? 'bridge' : 'alpine') + '.jpg') });
  if (path.includes('/Images/Logo')) return route.fulfill({ status: 200, headers: { ...CORS, 'Content-Type': 'image/svg+xml' }, body: '<svg xmlns="http://www.w3.org/2000/svg" width="480" height="140"><text x="100%" y="110" fill="#fff" font-family="serif" font-size="112" text-anchor="end" letter-spacing="8">LOGO</text></svg>' });
  if (path.startsWith('/emby/Items/')) return route.fulfill({ status: 200, headers: { ...CORS, 'Content-Type': 'image/svg+xml' }, body: art(path.split('/')[3], 320, 180) });
  if (api.unreachable) return route.abort('connectionrefused');
  const body = req.postData() ? JSON.parse(req.postData()) : null, auth = req.headers().authorization || '';
  api.calls.push({ method, path, body, auth, search: url.search });
  if (path === '/emby/System/Info/Public') return json(200, { ServerName: 'Basement', Version: '4.10.2.0', Id: 'abc' });
  if (path === '/emby/Lobby/Pair' && method === 'POST') {
    if (api.noPlugin) return json(404, { message: 'not found' });
    api.pairs.push(body); return json(200, { PairingId: 'pid1', Code: 'K7QX', Secret: 'sec1', ExpiresInSeconds: 900 });
  }
  if (path === '/emby/Lobby/Pair/Status') {
    api.statusCalls++;
    if (url.searchParams.get('Secret') !== 'sec1') return json(200, { Status: 'expired' });
    return json(200, api.statusCalls >= api.approveAfter ? { Status: 'approved', Token: TOKEN, TvName: 'Living Room' } : { Status: 'pending' });
  }
  if (!path.startsWith('/emby/Lobby/')) return json(404, { message: 'nope' });
  if (api.unauthorized || auth !== 'Lobby ' + TOKEN) return json(401, { Message: 'This TV is not paired with Lobby (or was removed). Pair it again.' });
  const op = path.slice('/emby/Lobby'.length);
  if (op === '/Home') return json(200, homeJson());
  if (op === '/Settings' && method === 'GET') return json(200, settingsJson());
  if (op === '/Settings' && method === 'POST') {
    api.saves.push(body); api.customized = true;
    if (body.RowOrder) api.order = body.RowOrder.concat(ROWS.map(r => r.Id).filter(id => !body.RowOrder.includes(id)));
    if (body.HiddenRows) api.hidden = body.HiddenRows.slice();
    if (body.RowLimit) api.limit = body.RowLimit;
    if (body.SportsLookaheadHours) api.look = body.SportsLookaheadHours;
    if (body.PlayDelayMs !== undefined && body.PlayDelayMs !== null) api.delay = body.PlayDelayMs;
    if (body.PlayTimeoutSeconds) api.wait = body.PlayTimeoutSeconds;
    if (body.SportsTeams) { api.teams = body.SportsTeams.slice(); api.teamsList.forEach(t => { t.Selected = api.teams.includes(t.Name); }); }
    return json(200, settingsJson());
  }
  if (op === '/Settings/Reset') { api.customized = false; api.hidden = []; api.order = ROWS.map(r => r.Id); api.limit = 20; return json(200, settingsJson()); }
  if (op === '/Sports/Teams') return json(200, { Days: 7, Teams: api.teamsList.map(t => ({ ...t })), Message: null });
  if (op === '/Requesters') return json(200, { RequestsEnabled: api.requestsEnabled, Requesters: api.requesters, LastRequesterId: api.lastRequester, UsingFallback: false });
  if (op === '/Request') { api.requests.push(body); return json(200, api.requestStatus === 0 ? { Status: 'failed', Message: 'Request for this media already exists.' } : { Status: api.requestStatus === 2 ? 'approved' : 'requested', RequestId: 9 }); }
  if (op === '/Play') { api.plays.push(body); return json(200, api.playStatus === 'failed' ? { Status: 'failed', Message: 'This TV has no Emby app device assigned in the Lobby settings.' } : { Status: api.playStatus }); }
  return json(404, { Message: 'Unknown' });
}

/* ---------- Page harness ---------- */
function installHarness({ paired, cacheHome, staleMs, look, featureMs }) {
  if (!sessionStorage.getItem('__seeded')) {
    localStorage.setItem('cleanframe.home.v1', JSON.stringify({ dock: ['stremio', 'netflix', 'disneyplus', 'youtube', 'appletv', 'primevideo', 'abciview', 'sbs', 'applemusic'], wallpaper: 'misty', mode: 'fixed', lastApp: 'netflix', theme: 'shore', ...(look ? { look: { layout: look } } : {}) }));
    if (paired) localStorage.setItem('lobby.server.v1', JSON.stringify({ url: 'http://cf.test:8096', token: 'tok_0123456789abcdefghijklmnop', tvName: 'Living Room' }));
    if (cacheHome) localStorage.setItem('lobby.home.cache.v1', JSON.stringify({ at: 1, data: cacheHome }));
    sessionStorage.setItem('__seeded', '1');
  }
  window.ShoreQA = { minuteMs: 60000, pollMs: 40, staleMs: staleMs || 30000, featureMs: featureMs || 600000 };
  window.__tv = { calls: [], launchFailure: false };
  window.tizen = {
    ApplicationControlData: function (key, value) { this.key = key; this.value = value; },
    ApplicationControl: function (operation, uri, mime, category, data) { Object.assign(this, { operation, uri, mime, category, data }); },
    application: {
      getCurrentApplication() { return { appInfo: { id: 'nClrkHome1.FrameHome' } }; },
      getAppsInfo(ok) { setTimeout(() => ok([]), 0); },
      launch(id, ok, fail) { window.__tv.calls.push({ method: 'launch', id }); setTimeout(() => window.__tv.launchFailure ? fail({ name: 'NotFoundError', message: 'missing' }) : ok(), 0); },
      launchAppControl(control, id, ok) { window.__tv.calls.push({ method: 'launchAppControl', id }); setTimeout(ok, 0); }
    }
  };
}
async function page(options = {}) {
  current = await preview.browser.newPage({ viewport: options.viewport || { width: 1920, height: 1080 } });
  current.setDefaultTimeout(10000);
  current.on('pageerror', error => report.pageErrors.push({ section, error: error.message }));
  current.on('requestfailed', request => {
    const allowed = api.unreachable && request.url().startsWith(SERVER);
    if (!current || current.__closing || allowed || request.failure()?.errorText === 'net::ERR_ABORTED') return;
    report.pageErrors.push({ section, url: request.url(), error: request.failure()?.errorText });
  });
  await current.route(SERVER + '/**', handle);
  await current.route('https://image.tmdb.org/**', route => route.fulfill({ status: 200, headers: { 'Content-Type': 'image/svg+xml', 'Access-Control-Allow-Origin': '*' }, body: art('Poster', 200, 300) }));
  // Registered last, so it is matched first.
  await current.route('https://image.tmdb.org/t/p/w1280/**', route => route.fulfill({ status: 200, headers: { 'Content-Type': 'image/jpeg', 'Access-Control-Allow-Origin': '*' }, body: readFileSync(root + 'app/assets/wallpapers/amber.jpg') }));
  await current.addInitScript(installHarness, { paired: !!options.paired, cacheHome: options.cacheHome || null, staleMs: options.staleMs || 0, look: options.look || null, featureMs: options.featureMs || 0 });
  await current.goto(preview.baseURL + '/home/');
  await current.waitForFunction(() => window.ShoreQA && ShoreQA.ready && ShoreQA.ready());
  if (options.paired && options.waitRows !== false) await current.waitForFunction(() => ShoreQA.state().media.rows.length > 0 && !ShoreQA.state().media.loading);
  await current.waitForTimeout(250);
  return current;
}
const state = () => current.evaluate(() => ShoreQA.state());
const where = () => current.evaluate(() => ShoreQA.where());
const act = action => current.evaluate(a => ShoreQA.activate(a), action);
const scene = name => current.evaluate(n => ShoreQA.scene(n), name);
async function nav(...keys) { for (const k of keys) await current.evaluate(key => ShoreQA.nav(key), k); }
const shot = name => current.screenshot({ path: out + name + '.png' });
const until = (fn, arg) => current.waitForFunction(fn, arg, { timeout: 8000 });
const settle = () => current.waitForTimeout(220);
async function untilNode(fn, ms = 8000) { const end = Date.now() + ms; while (!fn()) { if (Date.now() > end) throw Error('Timed out waiting for the mock server'); await new Promise(r => setTimeout(r, 30)); } }
/* Settings lines outside the visible list are not rendered as targets; move down until one is. */
async function reach(action) {
  for (let i = 0; i < 24; i++) {
    if ((await current.evaluate(() => ShoreQA.actions())).includes(action)) return act(action);
    await nav('down');
  }
  throw Error('Could not reach ' + action);
}

async function main() {
  preview = await openPreview({ publicFonts: true });
  try {
    await run('Unpaired Home is unchanged and contacts no server', async () => {
      api = freshApi(); await page();
      const s = await state();
      check('no media state', !s.media.paired && s.media.rows.length === 0);
      check('no request to any server', api.calls.length === 0, api.calls);
      check('rows hidden', await current.locator('#media').evaluate(el => getComputedStyle(el).display) === 'none');
      await nav('up'); check('Up from the dock still reaches the Home controls', (await where()).zone === 'util');
      await scene('settings');
      check('Settings lists Emby & Seerr, not connected', (await current.locator('[data-action="settings.group.media"]').textContent()).includes('Not connected'));
      const box = await current.locator('.settings-group').evaluateAll(els => els.map(e => e.getBoundingClientRect().bottom));
      const hint = await current.locator('.settings-overview-hint').evaluate(e => e.getBoundingClientRect().top);
      check('seven groups fit above the hint', Math.max(...box) < hint, { box, hint });
      await shot('settings-overview');
    });

    await run('Pairing with keypad, remote digits and approval', async () => {
      api = freshApi(); await page();
      await scene('settings'); await act('settings.group.media');
      check('Connect opens', (await state()).view === 'connect');
      await current.keyboard.type('cftest');
      check('letters are ignored by the keypad', (await current.locator('.cn-field').textContent()).trim() === '192.168.1.10', await current.locator('.cn-field').textContent());
      await act('connect.key.go');
      check('empty address explains the format', (await current.locator('.s-sub').textContent()).includes('like 192.168.1.10'));
      await current.keyboard.type('10.0.0.5');
      check('focus stays on Connect after using it', (await where()).focusAction === 'connect.key.go');
      await nav('up', 'ok');                                     // ':' sits above Connect
      for (const d of '8096') await current.keyboard.press(d);
      check('address typed with keys and keypad', (await current.locator('.cn-field').textContent()).trim() === '10.0.0.5:8096');
      await nav('down', 'left', 'ok');                           // Delete
      check('Delete removes one character', (await current.locator('.cn-field').textContent()).trim() === '10.0.0.5:809');
      await current.keyboard.press('6'); await shot('connect-keypad');
      for (let i = 0; i < 13; i++) await current.keyboard.press('Backspace');
      check('Backspace deletes rather than closing', (await state()).view === 'connect' && (await current.locator('.cn-field').textContent()).trim() === '192.168.1.10');
      // The mocked server has a hostname, which the keypad can't type.
      await current.evaluate(() => ShoreQA.typeAddress('cf.test:8096'));
      await act('connect.key.go');
      await until(() => ShoreQA.state().media.step === 'code');
      check('pairing code shown', (await current.locator('.cn-code').textContent()).trim() === 'K7QX');
      check('pairing sent a device name', api.pairs[0] && api.pairs[0].DeviceName === 'Samsung TV', api.pairs);
      await shot('connect-code');
      await until(() => ShoreQA.state().media.step === 'done');
      const saved = await current.evaluate(() => JSON.parse(localStorage.getItem('lobby.server.v1')));
      check('token saved for the server', saved.url === SERVER && saved.token === TOKEN && saved.tvName === 'Living Room', saved);
      check('token never shown on screen', !(await current.locator('body').textContent()).includes(TOKEN));
      check('a newly paired TV switches Home to Watch', (await state()).layout === 'watch');
      await nav('ok');
      await until(() => ShoreQA.state().view === 'mediaset' && !!ShoreQA.state().media.settings);
      await nav('back', 'back');
      await until(() => ShoreQA.state().view === 'home' && ShoreQA.state().media.rows.length > 0);
      check('every authenticated call used the Lobby scheme', api.calls.filter(c => c.path.startsWith('/emby/Lobby/') && !c.path.includes('/Pair')).every(c => c.auth === 'Lobby ' + TOKEN));
      check('token never sent in a URL', api.calls.every(c => !c.search.includes(TOKEN)));
    });

    await run('Pairing failures explain themselves', async () => {
      api = freshApi({ unreachable: true }); await page();
      await scene('settings'); await act('settings.group.media');
      await current.evaluate(() => ShoreQA.typeAddress('cf.test'));
      await act('connect.key.go');
      await until(() => ShoreQA.state().media.step === 'error');
      check('unreachable server', (await current.locator('.s-sub').textContent()).includes('Couldn’t reach cf.test:8096'));
      check('retry and change address offered', (await current.evaluate(() => ShoreQA.actions())).join(',') === 'act.retry,act.edit');
      api.unreachable = false; api.noPlugin = true;
      await act('act.retry');
      await until(() => ShoreQA.state().media.step === 'error' && document.querySelector('.s-sub').textContent.includes('plugin'));
      check('missing plugin named', (await current.locator('.s-sub').textContent()).includes('Lobby plugin isn’t installed'));
      await act('act.edit');
      check('Change address returns to the keypad with the address kept', (await state()).media.step === 'address' && (await current.locator('.cn-field').textContent()).trim() === 'cf.test');
      await nav('back');
      check('Back closes Connect', (await state()).view === 'settings');
    });

    await run('Rows on Home: Dock navigation', async () => {
      api = freshApi(); await page({ paired: true });
      const s = await state();
      check('rows in server order, empty rows dropped', s.media.rows.map(r => r.id).join() === 'continue,nextup,livenow,sports,seerr-trending,seerr-movies', s.media.rows.map(r => r.id));
      check('Home starts on the apps', (await where()).zone === 'apps');
      await shot('dock-apps');
      await nav('up');
      check('Up from the dock enters the first row', (await where()).focusAction === 'media.continue.101', await where());
      await nav('left');
      check('Left at the first card stays', (await where()).focusAction === 'media.continue.101');
      await nav('right', 'right', 'right');
      check('Right stops at the last card', (await where()).focusAction === 'media.continue.103');
      check('item titles are escaped', await current.evaluate(() => !window.__mediaXss && !document.querySelector('#media img[src="x"]')));
      await nav('down');
      check('Down moves to the next row', (await where()).focusAction === 'media.nextup.200');
      await nav('right', 'right', 'right', 'right', 'right', 'right');
      await settle(); await shot('dock-row-scroll');
      check('long rows scroll to keep focus visible', await current.evaluate(() => { const f = document.querySelector('.mc.is-focus').getBoundingClientRect(), v = document.querySelector('#mediaView').getBoundingClientRect(); return f.left >= v.left - 1 && f.right <= v.right + 1; }));
      await nav('up');
      check('rows remember their own card', (await where()).focusAction === 'media.continue.103');
      await nav('up');
      check('Up from the first row reaches the Home controls', (await where()).zone === 'util');
      await nav('down');
      check('Down from the controls enters the first row', (await where()).focusAction === 'media.continue.103');
      await nav('down', 'down', 'down', 'down', 'down', 'down');
      check('Down from the last row returns to the apps', (await where()).zone === 'apps');
      await nav('up');
      check('Up returns to the last used row', (await state()).media.row === 'seerr-movies');
      await nav('back');
      check('Back from the rows returns to the apps', (await where()).zone === 'apps');
      check('exactly one focused control', (await where()).focusCount === 1);
      await nav('up'); await settle(); await shot('dock-rows');
      const overlap = await current.evaluate(() => { const v = document.querySelector('#mediaView').getBoundingClientRect(), d = document.querySelector('#dock').getBoundingClientRect(); return v.bottom <= d.top; });
      check('rows end above the dock', overlap);
      await nav('down', 'down', 'down', 'down', 'down'); await settle();
      const titles = await current.evaluate(() => { const v = document.querySelector('#mediaView').getBoundingClientRect(); return Array.from(document.querySelectorAll('.mr-title')).filter(t => { const r = t.getBoundingClientRect(); return r.bottom > v.top && r.top < v.bottom; }).map(t => t.getBoundingClientRect().top >= v.top - 1); });
      check('the last rows scroll to whole rows, titles included', titles.length > 0 && titles.every(Boolean), titles);
    });

    await run('Rows on Home: List layout and other backgrounds', async () => {
      api = freshApi(); await page({ paired: true });
      await current.evaluate(() => ShoreQA.look({ layout: 'list' })); await until(() => ShoreQA.ready());
      await settle(); await shot('list-apps');
      const plates = await current.evaluate(() => ({ list: document.querySelector('#phList').getBoundingClientRect().right, media: document.querySelector('#mediaView').getBoundingClientRect().left }));
      check('List leaves room for the rows column', plates.list <= plates.media, plates);
      await nav('right');
      check('Right from the List enters the rows', (await where()).focusAction === 'media.continue.101');
      await nav('left');
      check('Left from the first card returns to the List', (await where()).zone === 'apps');
      await nav('right', 'down', 'down', 'down', 'down', 'down', 'down');
      check('Down from the last row reaches the controls', (await where()).zone === 'util');
      await nav('up');
      check('Up from the first control returns to the last row', (await state()).media.row === 'seerr-movies');
      await settle(); await shot('list-rows');
      for (const bg of ['dark', 'light']) {
        await current.evaluate(b => ShoreQA.look({ bg: b }), bg); await until(() => ShoreQA.ready()); await settle();
        await shot('list-rows-' + bg);
      }
      await current.evaluate(() => ShoreQA.look({ layout: 'dock', bg: 'light' })); await until(() => ShoreQA.ready());
      await nav('back', 'up'); await settle(); await shot('dock-rows-light');
    });

    await run('Rows fit at 720p', async () => {
      api = freshApi(); await page({ paired: true, viewport: { width: 1280, height: 720 } });
      await nav('up'); await settle(); await shot('dock-rows-720');
      const fits = await current.evaluate(() => { const v = document.querySelector('#mediaView').getBoundingClientRect(), d = document.querySelector('#dock').getBoundingClientRect(); return v.bottom <= d.top + 1 && v.right <= window.innerWidth; });
      check('rows stay above the dock and inside the screen', fits);
    });

    await run('Play and resume ask the server first, then open Emby', async () => {
      api = freshApi(); await page({ paired: true });
      await nav('up', 'ok');
      await until(() => window.__tv.calls.length === 1);
      check('resume sent before launch', api.plays.length === 1 && api.plays[0].ItemId === '101' && api.plays[0].Action === 'resume', api.plays);
      check('Emby app launched', (await current.evaluate(() => window.__tv.calls))[0].id === EMBY_APP);
      await nav('down', 'down', 'ok');
      await until(() => window.__tv.calls.length === 2);
      check('Live TV plays the channel', api.plays[1].ItemId === '901' && api.plays[1].Action === 'play', api.plays);
      api.playStatus = 'failed';
      await nav('ok'); await until(() => ShoreQA.state().view === 'message');
      check('server refusal is shown and Emby is not opened', (await current.locator('.msg-line').textContent()).includes('no Emby app device') && (await current.evaluate(() => window.__tv.calls.length)) === 2);
      await nav('back');
      api.playStatus = 'queued';
      await current.evaluate(() => { window.__tv.launchFailure = true; });
      await nav('ok'); await until(() => ShoreQA.state().view === 'message');
      check('missing Emby app explained', (await current.locator('.msg-line').textContent()).includes('Emby isn’t installed'));
    });

    await run('Seerr request with a shared account', async () => {
      api = freshApi(); await page({ paired: true });
      await nav('up', 'down', 'down', 'down', 'down', 'right', 'right', 'ok');
      await until(() => ShoreQA.state().view === 'request' && document.querySelectorAll('.rq-acts .btn').length === 3);
      check('one button per person, last requester first in focus', (await where()).focusAction === 'act.as.2', await where());
      check('people named', (await current.locator('.rq-acts').textContent()).includes('Request as Steve'));
      check('Home rows stay hidden behind the sheet', await current.evaluate(() => Array.from(document.querySelectorAll('#media .mc, #media .mr-title')).every(el => getComputedStyle(el).visibility === 'hidden')));
      await shot('request-choose');
      await nav('left', 'ok');
      await until(() => ShoreQA.state().view === 'request' && document.querySelector('.rq-status').textContent.includes('approved'));
      check('request sent as the chosen person', api.requests[0].SeerrUserId === 1 && api.requests[0].TmdbId === 502 && api.requests[0].MediaType === 'movie', api.requests);
      await shot('request-done');
      await nav('ok');
      check('Done returns to Home', (await state()).view === 'home');
      await untilNode(() => api.calls.filter(c => c.path.endsWith('/Home')).length >= 2);
      await nav('left', 'left');
      check('an available title offers Resume', (await current.locator('.mi-ok').textContent()).includes('Resume'));
      await nav('ok');
      await until(() => window.__tv.calls.length === 1);
      check('OK on an available title resumes it in Emby, no request screen', api.plays[0].ItemId === '701' && api.plays[0].Action === 'resume' && (await state()).view === 'home', api.plays);
      await nav('right', 'ok');
      await until(() => ShoreQA.state().view === 'request');
      check('already requested titles only explain their status', (await current.locator('.rq-status').textContent()).includes('already been requested') && api.calls.filter(c => c.path.endsWith('/Requesters')).length === 1);
      await nav('back', 'left');
      api.requestStatus = 0;
      await nav('right', 'right', 'right', 'ok');
      await until(() => document.querySelectorAll('.rq-acts .btn').length === 3);
      await nav('ok'); await until(() => document.querySelector('.rq-status .setting-error'));
      check('Seerr’s refusal is shown with Try again', (await current.locator('.rq-status').textContent()).includes('already exists') && (await current.evaluate(() => ShoreQA.actions())).includes('act.retry'));
    });

    await run('Partly available titles offer Play and Request', async () => {
      api = freshApi(); await page({ paired: true });
      await nav('up', 'down', 'down', 'down', 'down', 'right', 'right', 'right', 'right');
      check('the info line says both', (await current.locator('.mi-ok').textContent()).includes('Play or request'));
      await nav('ok');
      await until(() => ShoreQA.state().view === 'request' && document.querySelectorAll('.rq-acts .btn').length === 4);
      check('Play first and focused, then one Request per person', (await current.evaluate(() => ShoreQA.actions())).filter(a => a.startsWith('act.')).join() === 'act.play,act.as.1,act.as.2,act.close' && (await where()).focusAction === 'act.play');
      check('explains what is available', (await current.locator('.rq-status').textContent()).includes('Some of this is in your Emby library'));
      await shot('request-partial');
      await nav('ok');
      await until(() => window.__tv.calls.length === 1);
      check('Play starts the library copy', api.plays[0].ItemId === '702' && api.plays[0].Action === 'play' && (await state()).view === 'home', api.plays);
      await nav('ok');
      await until(() => document.querySelectorAll('.rq-acts .btn').length === 4);
      await nav('right', 'ok');
      await until(() => document.querySelector('.rq-status').textContent.includes('approved'));
      check('Request still works for the rest', api.requests[0].TmdbId === 504 && api.requests[0].SeerrUserId === 1, api.requests);
    });

    await run('Single requester and unlinked accounts', async () => {
      api = freshApi({ requesters: [{ Id: 1, Name: 'Steve' }], lastRequester: null }); await page({ paired: true });
      await nav('up', 'down', 'down', 'down', 'down', 'right', 'right', 'ok');
      await until(() => document.querySelectorAll('.rq-acts .btn').length === 2);
      check('one person: a plain Request button', (await current.locator('.rq-acts .btn').first().textContent()).trim() === 'Request');
      await nav('back');
      api.requesters = [];
      await nav('right', 'ok');
      await until(() => document.querySelector('.rq-status').textContent.length > 0);
      check('unlinked account explained', (await current.locator('.rq-status').textContent()).includes('isn’t linked to a Seerr account'));
    });

    await run('Emby & Seerr settings change this TV', async () => {
      api = freshApi(); await page({ paired: true });
      await scene('settings'); await act('settings.group.media');
      await until(() => !!ShoreQA.state().media.settings);
      await shot('mediaset');
      await act('list.row.nextup.toggle');
      await until(() => !ShoreQA.state().media.saving);
      check('Hide sends HiddenRows', JSON.stringify(api.saves[0]) === JSON.stringify({ HiddenRows: ['nextup'] }), api.saves);
      await act('list.row.seerr-trending.up');
      await until(() => !ShoreQA.state().media.saving);
      check('Move up sends the new order', api.saves[1].RowOrder.indexOf('seerr-trending') === 3, api.saves[1]);
      check('focus follows the moved row', (await where()).focusAction === 'list.row.seerr-trending.up');
      await reach('list.limit.RowLimit:8');
      await until(() => !ShoreQA.state().media.saving);
      check('items per row', api.saves[2].RowLimit === 8);
      await reach('list.delay.PlayDelayMs:3000');
      await until(() => !ShoreQA.state().media.saving);
      check('play delay', api.saves[3].PlayDelayMs === 3000);
      await reach('list.teams.teams');
      await until(() => ShoreQA.state().view === 'teams' && document.querySelectorAll('.ls-line').length > 3);
      await shot('teams');
      await act('list.team.Toronto Blue Jays.toggle');
      await until(() => !ShoreQA.state().media.saving);
      check('following a team saves the list', JSON.stringify(api.saves[4].SportsTeams) === JSON.stringify(['Toronto Maple Leafs', 'Toronto Blue Jays']), api.saves[4]);
      await act('list.done.done');
      check('Done returns to settings', (await state()).view === 'mediaset');
      await nav('back', 'back');
      await until(() => ShoreQA.state().media.rows.map(r => r.id).join() === 'continue,livenow,seerr-trending,sports,seerr-movies');
      check('Home follows the new order and hidden rows', true);
      await scene('settings'); await act('settings.group.media'); await until(() => !!ShoreQA.state().media.settings);
      await reach('list.reset.reset'); await until(() => !ShoreQA.state().media.saving && ShoreQA.state().media.note);
      check('Use defaults resets', !api.customized && (await state()).media.note.includes('server’s defaults'));
    });

    await run('Disconnect and a removed TV', async () => {
      api = freshApi(); await page({ paired: true });
      await scene('settings'); await act('settings.group.media'); await until(() => !!ShoreQA.state().media.settings);
      await reach('list.disconnect.disconnect');
      check('asks first', (await state()).view === 'message');
      await nav('right', 'ok');
      const s = await state();
      check('back on Settings, forgotten', s.view === 'settings' && !s.media.paired && s.media.rows.length === 0);
      check('storage cleared', await current.evaluate(() => !localStorage.getItem('lobby.server.v1') && !localStorage.getItem('lobby.home.cache.v1')));
      await nav('back');
      check('Home rows gone', await current.locator('#media').evaluate(el => getComputedStyle(el).display) === 'none');
      await current.close(); current = null;

      api = freshApi({ unauthorized: true });
      await page({ paired: true, waitRows: false, cacheHome: homeJson() });
      await until(() => ShoreQA.state().media.unauthorized);
      check('rows hidden when the server no longer knows this TV', (await state()).media.rows.length === 0);
      await scene('settings');
      check('Settings says to connect again', (await current.locator('[data-action="settings.group.media"]').textContent()).includes('Connect again'));
      await act('settings.group.media');
      check('opens Connect with the old address', (await state()).view === 'connect' && (await current.locator('.cn-field').textContent()).trim() === 'cf.test:8096');
    });

    await run('Watch layout: featured titles, bands and navigation', async () => {
      api = freshApi(); await page({ paired: true, look: 'watch' });
      let s = await state();
      const rect = sel => current.locator(sel).first().evaluate(el => { const r = el.getBoundingClientRect(); return { x: r.left, y: r.top, r: r.right, b: r.bottom }; });
      check('Watch is in use', s.media.layout === 'watch' && await current.locator('#watch').evaluate(el => getComputedStyle(el).display) === 'block');
      check('the Dock and rows of the other layouts are hidden', await current.locator('#dock').evaluate(el => getComputedStyle(el).display) === 'none');
      check('featured titles in server order', s.media.featured.join() === 'f1,movie:880,f3,g1', s.media.featured);
      check('one row per band; Continue Watching and Next Up merge, Your Teams stands alone', s.media.bands.join() === 'continue,livenow,sports,seerr-trending,seerr-movies', s.media.bands);
      const dock = await rect('#wDock');
      check('the apps dock shows at the bottom from the start', (await current.locator('#wDock .wapp').count()) === 9 && dock.b <= 1080 && dock.y >= 900, dock);
      check('Home starts on the featured title', (await where()).focusAction === 'watch.featured.f1', await where());
      check('the tagline heads the featured title', (await current.locator('.wi-head').textContent()) === 'Twelve ships, one language, and plenty of questions — why are they really here, anyway?');
      // Letters below the line (g, j, p, q, y) need room inside the two-line clamp.
      const clip = await current.evaluate(() => {
        const head = document.querySelector('.wi-head'), size = parseFloat(getComputedStyle(head).fontSize), range = document.createRange();
        range.selectNodeContents(head);
        const box = head.getBoundingClientRect(), line = parseFloat(getComputedStyle(head).lineHeight);
        // Text past the two-line clamp still has line boxes; only the shown lines matter.
        // A line's text box spans its full ascent and descent, so it must sit inside the headline.
        const shown = Array.prototype.filter.call(range.getClientRects(), r => (r.top + r.bottom) / 2 < box.top + 2 * line);
        return { room: box.bottom - Math.max.apply(null, shown.map(r => r.bottom)), size, lines: new Set(shown.map(r => Math.round(r.top))).size };
      });
      check('a long tagline takes two lines and leaves room for descenders', clip.lines === 2 && clip.room >= 0, clip);
      const info = await rect('.w-info'), firstRow = await rect('.wr-title');
      check('the featured text stays above the first row', info.b <= firstRow.y, { info, firstRow });
      check('the button names the action', (await current.locator('.wi-btn').textContent()) === 'Resume');
      check('reason and details beside the button', (await current.locator('.wi-meta').textContent()) === 'Continue watching · 2016 · PG-13 · 1h 10m left');
      check('the title shows as art when there is no logo', (await current.locator('.wa-title').textContent()) === 'Arrival');
      check('date and time at the top', /^[A-Z][a-z]{2}, [A-Z][a-z]{2} \d{1,2}\d{1,2}:\d{2} [AP]M$/.test(await current.locator('#wTop').textContent()), await current.locator('#wTop').textContent());
      check('four dots, the first current', (await current.locator('.wd').count()) === 4 && (await current.locator('.wd.is-on').count()) === 1);
      await until(() => document.querySelector('.w-backdrop.is-on'));
      await current.waitForTimeout(450); await shot('watch-home');
      await nav('right');
      s = await state();
      check('Right shows the next featured title', s.media.featuredIndex === 1 && (await current.locator('.wi-head').textContent()) === 'The Long Walk');
      check('a Seerr title offers Request', (await current.locator('.wi-btn').textContent()) === 'Request');
      await nav('right');
      check('a logo replaces the title art', await current.locator('.wa-logo').count() === 1 && await current.locator('.wa-title').count() === 0);
      await nav('right');
      check('a game leads with its matchup', (await current.locator('.wi-head').textContent()) === 'Maple Leafs at Bruins' && (await current.locator('.wi-btn').textContent()) === 'Watch live');
      check('the game names the team reason, channel and time', (await current.locator('.wi-meta').textContent()) === 'Your team · Sportsnet · On now', await current.locator('.wi-meta').textContent());
      check('a game without artwork shows no backdrop', await current.locator('.w-backdrop.is-on').count() === 0);
      await nav('right');
      check('Right from the last title wraps to the first', (await state()).media.featuredIndex === 0);
      await nav('left');
      check('Left from the first title reaches the rail', (await where()).focusAction === 'util.art');
      check('the rail names the focused control', await current.locator('.wr-btn.is-focus .wr-label').evaluate(el => getComputedStyle(el).opacity) !== '0');
      await nav('down', 'down');
      check('the rail holds Art Mode, All apps and Settings', (await where()).focusAction === 'util.settings');
      await nav('down');
      check('Down from Settings reaches the app dock', (await where()).focusAction === 'home.app.stremio', await where());
      await nav('left');
      check('Left from the first app returns to the rail where it left', (await where()).focusAction === 'util.settings', await where());
      await nav('ok');
      check('Settings opens from the rail', (await state()).view === 'settings');
      await nav('back');
      await nav('right');
      check('Right from the rail returns to the featured title', (await where()).zone === 'whero');
      await nav('down');
      check('Down enters the first band', (await where()).focusAction === 'media.continue.101');
      check('the top describes the focused title', (await current.locator('.wi-head').textContent()) === 'Arrival' && await current.locator('.wi-head.is-title').count() === 1);
      check('a library card carries its title and detail', (await current.locator('.wc.is-focus .wc-title').textContent()) === 'Arrival');
      check('the merged row is titled Continue Watching', (await current.locator('.wrow.is-active .wr-title').textContent()) === 'Continue Watching');
      await nav('right', 'right', 'right');
      check('next episodes follow the titles in progress in the same row', (await where()).focusAction === 'media.continue.200', await where());
      await nav('left');
      check('Left steps back along the row', (await where()).focusAction === 'media.continue.103');
      await nav('down', 'down');
      check('Your Teams has its own row', (await where()).focusAction === 'media.sports.p2', await where());
      await nav('down');
      check('Trending follows', (await where()).focusAction === 'media.seerr-trending.movie:500', await where());
      check('Seerr tiles carry no labels', await current.locator('.wc.is-focus .wc-label').count() === 0);
      await current.waitForTimeout(450); await shot('watch-rows');
      await nav('down', 'down');
      check('Down from the last band reaches the dock', (await where()).focusAction === 'home.app.stremio', await where());
      check('the dock names its apps', (await current.locator('.wapp.is-focus .wapp-name').textContent()) === 'Stremio');
      await current.waitForTimeout(400);
      const app = await rect('.wapp.is-focus');
      check('the dock stays in place', app.b <= 1080 && app.y >= 900, app);
      await nav('up');
      check('Up from the dock returns to the band last used', (await where()).focusAction === 'media.seerr-movies.movie:603', await where());
      await nav('down');
      await current.waitForTimeout(450); await shot('watch-apps');
      await nav('right', 'ok');
      await current.waitForTimeout(80);
      check('OK on an app launches it', (await current.evaluate(() => window.__tv.calls)).some(c => c.id === 'org.tizen.netflix-app'));
      await current.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); });
      await nav('back');
      check('Back returns to the featured title', (await where()).zone === 'whero');
      await nav('ok');
      await untilNode(() => api.plays.length === 1);
      check('OK on the featured title resumes it', api.plays[0].ItemId === '101' && api.plays[0].Action === 'resume', api.plays);
    });

    await run('Watch layout: rotation, fallback and missing titles', async () => {
      let s2;
      api = freshApi(); await page({ paired: true, look: 'watch', featureMs: 300 });
      await until(() => ShoreQA.state().media.featuredIndex === 1);
      check('featured titles change on their own', true);
      await nav('down');
      const at = (await state()).media.featuredIndex;
      await until(n => ShoreQA.state().media.featuredIndex !== n, at);
      check('they keep changing behind a focused card', (await state()).media.featuredIndex !== at);
      check('while the top keeps describing the card', (await current.locator('.wi-head').textContent()) === 'Arrival' && (await where()).focusAction === 'media.continue.101');
      await current.close(); current = null;

      api = freshApi({ featured: undefined }); await page({ paired: true, look: 'watch' });
      s2 = await state();
      check('without featured titles from the server, the first of Continue Watching and Next Up are featured', s2.media.featured.join() === '101,200', s2.media.featured);
      check('they leave their rows', !s2.media.rows.find(r => r.id === 'continue').items.includes('101') && !s2.media.rows.find(r => r.id === 'nextup').items.includes('200'));
      check('the headline is the title, with the reason beside the button', (await current.locator('.wi-head').textContent()) === 'Arrival' && (await current.locator('.wi-meta').textContent()).startsWith('Continue watching'));
      await current.close(); current = null;

      api = freshApi({ featured: [] });
      ITEMS.continue.forEach(i => { i.__l = i.LandscapeUrl; i.LandscapeUrl = ''; }); ITEMS.nextup.forEach(i => { i.__l = i.LandscapeUrl; i.LandscapeUrl = ''; });
      try { await page({ paired: true, look: 'watch' }); }
      finally { ITEMS.continue.concat(ITEMS.nextup).forEach(i => { i.LandscapeUrl = i.__l; delete i.__l; }); }
      check('with nothing to feature, Home starts in the first band', (await where()).focusAction === 'media.continue.101', await where());
      await nav('left');
      check('Left from the first card reaches the rail', (await where()).zone === 'wrail');
      await current.close(); current = null;

      api = freshApi(); await page({ look: 'watch' });
      check('an unpaired TV uses Dock', (await state()).media.layout === 'dock' && await current.locator('#watch').evaluate(el => getComputedStyle(el).display) === 'none');
      await scene('appearance');
      check('Layout offers Watch', (await current.evaluate(() => ShoreQA.actions())).length > 0);
    });

    await run('Watch layout follows Background', async () => {
      api = freshApi(); await page({ paired: true, look: 'watch' });
      const bgOf = sel => current.locator(sel).evaluate(el => getComputedStyle(el).backgroundColor);
      await current.evaluate(() => ShoreQA.look({ bg: 'dark' }));
      await until(() => ShoreQA.ready());
      check('Dark: a dark page', await bgOf('#watch') === 'rgb(11, 13, 17)');
      await current.waitForTimeout(400); await shot('watch-dark');
      check('only the focused card shows its name', await current.locator('.wc .wc-label').evaluateAll(els => els.filter(e => getComputedStyle(e).opacity !== '0').length) === 0);
      await nav('down');
      await current.waitForTimeout(300);
      check('the focused card names itself', await current.locator('.wc.is-focus .wc-label').evaluate(e => getComputedStyle(e).opacity) === '1');
      check('the focus ring is a light outline', /0px 0px 0px 2px/.test(await current.locator('.wc.is-focus .mc-art').evaluate(e => getComputedStyle(e).boxShadow)));
      await nav('up');
      await current.evaluate(() => ShoreQA.look({ bg: 'light' }));
      await until(() => ShoreQA.ready());
      await current.waitForTimeout(300);
      check('Light: a light page with dark text', await bgOf('#watch') === 'rgb(238, 234, 226)' && await current.locator('.wi-head').evaluate(e => getComputedStyle(e).color) === 'rgb(28, 26, 23)');
      await shot('watch-light');
      await current.evaluate(() => ShoreQA.look({ bg: 'photo' }));
      await until(() => ShoreQA.ready());
      await current.waitForTimeout(500);
      check('Photo: the wallpaper shows behind Watch', await bgOf('#watch') === 'rgba(0, 0, 0, 0)' && await current.locator('#photo').evaluate(e => getComputedStyle(e).display !== 'none' && getComputedStyle(e.parentNode).display !== 'none'));
      check('Photo: the Dock layout’s shading stays hidden', await current.locator('#bg .scrim').evaluate(e => getComputedStyle(e).display) === 'none');
      await shot('watch-photo');
    });

    await run('Cached rows show at once and refresh on return', async () => {
      api = freshApi(); api.hidden = ['nextup'];
      await page({ paired: true, cacheHome: { Rows: [{ Id: 'cached', Title: 'From cache', Layout: 'landscape', Items: [{ Id: '1', Kind: 'movie', Title: 'Old', PlayItemId: '1' }] }] }, staleMs: 50, waitRows: false });
      await until(() => ShoreQA.state().media.rows.some(r => r.id === 'continue'));
      check('fresh rows replace the cache', !(await state()).media.rows.some(r => r.id === 'cached'));
      const before = api.calls.filter(c => c.path.endsWith('/Home')).length;
      api.hidden = [];
      await current.waitForTimeout(80);
      await current.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); });
      await until(n => ShoreQA.state().media.rows.some(r => r.id === 'nextup'), before);
      check('returning to Home refreshes the rows', api.calls.filter(c => c.path.endsWith('/Home')).length === before + 1);
    });
  } finally {
    await preview.close();
  }
  writeFileSync(out + 'checks.json', JSON.stringify(report, null, 2));
  const passed = report.checks.filter(c => c.passed).length;
  console.log(passed + ' checks passed; ' + report.failures.length + ' failure(s); ' + report.pageErrors.length + ' page error(s). Report: .local/qa/media/checks.json');
  if (report.pageErrors.length) console.error(JSON.stringify(report.pageErrors.slice(0, 10), null, 1));
  if (report.failures.length || report.pageErrors.length) process.exit(1);
}
main().catch(error => { console.error(error); process.exit(1); });
