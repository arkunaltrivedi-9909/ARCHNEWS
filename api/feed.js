// ARCHNEWS — Feed API v7
// One pipeline for ARCHNEWS and ARCHSHORTS.
// Each source lists strategies in order of quality. The first one that returns stories wins:
//   rss    — the publisher's own RSS/Atom feed
//   wp     — the publisher's own WordPress REST API (Metropolis publishes an empty RSS feed)
//   domus  — the publisher's own section pages (Domus has no RSS feed)
//   bing   — Bing News search, used only when the publisher blocks server requests (Arch Paper)
// Every story carries a real publisher link, a clean excerpt, a 60-word short and, where one exists, the story's own image.

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36';
const PER_SOURCE = 12;

const SOURCES = [
  { id:'archdaily',  name:'ArchDaily',   color:'#bf2d0a', domain:'archdaily.com',
    plan:[['rss','https://www.archdaily.com/feed'], ['rss','https://feeds.feedburner.com/Archdaily'], ['bing','archdaily.com']] },
  { id:'dezeen',     name:'Dezeen',      color:'#0a4fb8', domain:'dezeen.com',
    plan:[['rss','https://www.dezeen.com/feed/'], ['rss','https://feeds.feedburner.com/dezeen'], ['bing','dezeen.com']] },
  { id:'archinect',  name:'Archinect',   color:'#0a8a35', domain:'archinect.com',
    plan:[['rss','https://archinect.com/feed/1/news'], ['rss','https://archinect.com/feed/news'], ['bing','archinect.com']] },
  { id:'metropolis', name:'Metropolis',  color:'#6b08a8', domain:'metropolismag.com',
    plan:[['wp','https://metropolismag.com/wp-json/wp/v2/', ['metro_project','metro_viewpoint','metro_profile','metro_product']], ['bing','metropolismag.com']] },
  { id:'archrecord', name:'Arch Record', color:'#b87008', domain:'architecturalrecord.com',
    plan:[['rss','https://www.architecturalrecord.com/rss/articles'], ['bing','architecturalrecord.com']] },
  { id:'designboom', name:'Designboom',  color:'#a80875', domain:'designboom.com',
    plan:[['rss','https://www.designboom.com/architecture/rss/'], ['rss','https://www.designboom.com/rss/'], ['bing','designboom.com']] },
  { id:'archpaper',  name:'Arch Paper',  color:'#087ab8', domain:'archpaper.com',
    plan:[['rss','https://www.archpaper.com/feed/'], ['bing','archpaper.com']] },
  { id:'domus',      name:'Domus',       color:'#5a5a3a', domain:'domusweb.it',
    plan:[['domus',['https://www.domusweb.it/en/architecture.html','https://www.domusweb.it/en/news.html']], ['bing','domusweb.it']] }
];

// ───────────────────────── text helpers ─────────────────────────
const NAMED = { amp:'&', lt:'<', gt:'>', quot:'"', apos:"'", nbsp:' ', ndash:'–', mdash:'—', hellip:'…',
  lsquo:'‘', rsquo:'’', ldquo:'“', rdquo:'”', laquo:'«', raquo:'»', copy:'©', reg:'®', trade:'™', deg:'°',
  eacute:'é', egrave:'è', ecirc:'ê', euml:'ë', aacute:'á', agrave:'à', acirc:'â', auml:'ä', atilde:'ã', aring:'å',
  iacute:'í', igrave:'ì', icirc:'î', iuml:'ï', oacute:'ó', ograve:'ò', ocirc:'ô', ouml:'ö', otilde:'õ', oslash:'ø',
  uacute:'ú', ugrave:'ù', ucirc:'û', uuml:'ü', ntilde:'ñ', ccedil:'ç', szlig:'ß',
  Eacute:'É', Aacute:'Á', Oacute:'Ó', Uuml:'Ü', Ouml:'Ö', Auml:'Ä', Ccedil:'Ç', Ntilde:'Ñ', times:'×', middot:'·', bull:'•' };

function decodeEntities(s) {
  return (s || '')
    .replace(/&#x([0-9a-f]+);/gi, (_, h) => safeChar(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (_, n) => safeChar(parseInt(n, 10)))
    .replace(/&([a-zA-Z]+);/g, (m, n) => (n in NAMED ? NAMED[n] : m));
}
function safeChar(n) { try { return String.fromCodePoint(n); } catch (e) { return ''; } }

// Feeds routinely double-encode. Decode until stable (max 3 passes).
function deepDecode(s) {
  let out = s || '';
  for (let i = 0; i < 3; i++) { const next = decodeEntities(out); if (next === out) break; out = next; }
  return out;
}

function unCDATA(s) { return (s || '').replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, '$1'); }

function toText(html) {
  return deepDecode(unCDATA(html || ''))
    .replace(/<(script|style|figure|figcaption|iframe|h[1-6])\b[\s\S]*?<\/\1>/gi, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/\s+/g, ' ')
    .replace(/\s+([,.;:!?…])/g, '$1')
    .replace(/([(“‘])\s+/g, '$1')
    .trim();
}

function cleanExcerpt(html) {
  let t = toText(html)
    .replace(/The post .{0,220}? appeared first on .*$/i, '')
    .replace(/\s*(Read more|Read the full story|Continue reading|Continued here)\b.*$/i, '')
    .replace(/\s*\[(…|\.\.\.|&hellip;)\]\s*$/, '…')
    .trim();
  if (t.length > 700) t = t.slice(0, 700).replace(/\s+\S*$/, '') + '…';
  return t;
}

// The 60-word short: whole sentences up to 60 words; if the first sentence alone runs long, cut on a word.
function makeShort(text, limit) {
  limit = limit || 60;
  const clean = (text || '').replace(/\s+/g, ' ').trim();
  if (!clean) return '';
  const words = clean.split(' ');
  if (words.length <= limit) return clean;
  const sentences = clean.match(/[^.!?…]+[.!?…]+["”’)]*\s*|[^.!?…]+$/g) || [clean];
  let out = '', count = 0;
  for (const s of sentences) {
    const n = s.trim().split(' ').length;
    if (count + n > limit) break;
    out += s; count += n;
  }
  out = out.trim();
  if (count >= 18) return out;
  return words.slice(0, limit).join(' ').replace(/[,;:\-–—]$/, '') + '…';
}

function absUrl(u, base) {
  if (!u) return null;
  u = deepDecode(u.trim());
  if (u.startsWith('//')) return 'https:' + u;
  if (u.startsWith('http://')) return 'https://' + u.slice(7);
  if (u.startsWith('https://')) return u;
  if (u.startsWith('/') && base) return base + u;
  return null;
}

const BAD_IMG = [/googleusercontent\.com/i, /news\.google\.com/i, /gravatar\.com/i, /feedburner/i, /pixel|tracking|1x1|spacer|blank\.gif/i,
  /\/emoji\//i, /s\.w\.org/i, /favicon/i, /rsslogo/i, /-411x411/i];
function goodImg(u) { return !!u && /^https:\/\//.test(u) && !BAD_IMG.some(rx => rx.test(u)); }

// ───────────────────────── fetch ─────────────────────────
async function get(url, ms, accept) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), ms || 6000);
  try {
    const r = await fetch(url, { signal: ctrl.signal, redirect: 'follow',
      headers: { 'User-Agent': UA, 'Accept': accept || 'application/rss+xml,application/atom+xml,application/xml,text/xml,*/*;q=0.8', 'Accept-Language': 'en-US,en;q=0.9' } });
    if (!r.ok) return null;
    return await r.text();
  } catch (e) { return null; } finally { clearTimeout(t); }
}

// ───────────────────────── strategy: RSS / Atom ─────────────────────────
function tag(xml, name) {
  const m = xml.match(new RegExp('<' + name + '(?:\\s[^>]*)?>([\\s\\S]*?)<\\/' + name + '>', 'i'));
  return m ? unCDATA(m[1]).trim() : '';
}

function rssImage(item, html) {
  const cands = [];
  let m;
  const mediaRx = /<media:(?:content|thumbnail)\b[^>]*?\burl=["']([^"']+)["'][^>]*>/gi;
  while ((m = mediaRx.exec(item)) !== null) {
    if (!/medium=["'](?:video|audio)["']/i.test(m[0]) && !/type=["'](?:video|audio)/i.test(m[0])) cands.push(m[1]);
  }
  const enc = item.match(/<enclosure\b[^>]*?\burl=["']([^"']+)["'][^>]*>/i);
  if (enc && !/type=["'](?:video|audio)/i.test(enc[0])) cands.push(enc[1]);
  const imgRx = /<img\b[^>]*?\bsrc=["']([^"']+)["']/gi;
  while ((m = imgRx.exec(html)) !== null) cands.push(m[1]);
  for (const c of cands) { const u = absUrl(c); if (goodImg(u)) return u; }
  return null;
}

function parseFeed(xml, source) {
  const chunks = [];
  let m;
  const itemRx = /<item[\s>]([\s\S]*?)<\/item>/gi, entryRx = /<entry[\s>]([\s\S]*?)<\/entry>/gi;
  while ((m = itemRx.exec(xml)) !== null) chunks.push(m[1]);
  while ((m = entryRx.exec(xml)) !== null) chunks.push(m[1]);

  return chunks.slice(0, PER_SOURCE + 6).map((item, i) => {
    const title = toText(tag(item, 'title'));
    let link = deepDecode(tag(item, 'link')).trim();
    if (!/^https?:\/\//.test(link)) {
      const alt = item.match(/<link\b[^>]*rel=["']alternate["'][^>]*href=["']([^"']+)["']/i) || item.match(/<link\b[^>]*href=["']([^"']+)["']/i);
      link = alt ? deepDecode(alt[1]) : '';
    }
    // Entity-encoded HTML (Atom content, Google-style descriptions) becomes real HTML after one decode.
    const rawBody = [tag(item, 'description'), tag(item, 'summary'), tag(item, 'content:encoded'), tag(item, 'content')];
    const html = rawBody.map(b => (/<[a-z]/i.test(b) ? b : decodeEntities(b))).join(' \n ');
    // Prefer the publisher's summary. If it runs under 45 words and the feed carries the article body, use the body so the short has room to say something.
    const asHtml = b => (/<[a-z]/i.test(b) ? b : decodeEntities(b));
    const wc = b => { const t = cleanExcerpt(asHtml(b)); return t ? t.split(' ').length : 0; };
    let firstBody = rawBody.find(b => wc(b) >= 8) || '';
    if (wc(firstBody) < 45) { const richer = rawBody.find(b => wc(b) >= 45); if (richer) firstBody = richer; }
    const pub = tag(item, 'pubDate') || tag(item, 'published') || tag(item, 'dc:date') || tag(item, 'updated').replace(/^>/, '');
    const ts = Date.parse(pub);
    return story(source, { title, link, excerptHtml: asHtml(firstBody),
      image: rssImage(item, html), ts, order: i, via: 'rss' });
  });
}

async function viaRss(source, url) {
  const xml = await get(url, 6000);
  if (!xml || !/<(item|entry)[\s>]/i.test(xml)) return [];
  return parseFeed(xml, source);
}

// ───────────────────────── strategy: WordPress REST ─────────────────────────
async function viaWp(source, base, types) {
  const fields = '_fields=title,link,date_gmt,excerpt,yoast_head_json.og_image,yoast_head_json.description,_links.wp:featuredmedia,_embedded.wp:featuredmedia';
  const lists = await Promise.all(types.map(async t => {
    const body = await get(base + t + '?per_page=6&_embed=wp:featuredmedia&' + fields, 6500, 'application/json');
    if (!body) return [];
    try { const j = JSON.parse(body); return Array.isArray(j) ? j : []; } catch (e) { return []; }
  }));
  return [].concat(...lists).map((p, i) => {
    let image = null;
    try {
      const media = p._embedded['wp:featuredmedia'][0];
      const sizes = (media.media_details && media.media_details.sizes) || {};
      image = (sizes.large || sizes.medium_large || sizes.full || {}).source_url || media.source_url || null;
    } catch (e) {}
    if (!image) { try { image = p.yoast_head_json.og_image[0].url; } catch (e) {} }
    const excerptHtml = (p.excerpt && p.excerpt.rendered) || (p.yoast_head_json && p.yoast_head_json.description) || '';
    return story(source, { title: toText(p.title && p.title.rendered), link: p.link, excerptHtml,
      image: absUrl(image), ts: Date.parse((p.date_gmt || '') + 'Z'), order: i, via: 'wp' });
  });
}

// ───────────────────────── strategy: Domus section pages ─────────────────────────
async function viaDomus(source, urls) {
  const pages = await Promise.all(urls.map(u => get(u, 6500, 'text/html,*/*')));
  const out = [], seen = new Set();
  pages.forEach(html => {
    if (!html) return;
    const blocks = html.match(/<article\b[^>]*class="[^"]*c-article-prev[\s\S]*?<\/article>/gi) || [];
    blocks.forEach(b => {
      const href = (b.match(/href="(\/en\/[a-z0-9\-\/]+\/(20\d\d)\/(\d\d)\/(\d\d)\/[^"]+\.html)"/i) || []);
      if (!href[1] || seen.has(href[1])) return;
      if (/\/(speciali|shop|sponsored-content|advertorial)\//.test(href[1])) return;
      seen.add(href[1]);
      const title = toText((b.match(/inject-title[^>]*>([\s\S]*?)<\/span>/i) || [])[1]);
      const desc = (b.match(/<p[^>]*inject-desc[^>]*>([\s\S]*?)<\/p>/i) || [])[1] || '';
      const img = (b.match(/<img[^>]*data-src="([^"]+)"/i) || b.match(/<img[^>]*src="([^"]+)"/i) || [])[1];
      // Dated from the URL; position in the page keeps same-day stories in the publisher's order.
      const ts = Date.UTC(+href[2], +href[3] - 1, +href[4], 12, 0, 0) - out.length * 60000;
      out.push(story(source, { title, link: 'https://www.domusweb.it' + href[1], excerptHtml: desc,
        image: absUrl(img && img.replace('.foto.rsmall.', '.foto.rmedium.'), 'https://www.domusweb.it'), ts, order: out.length, via: 'page' }));
    });
  });
  return out;
}

// ───────────────────────── strategy: Bing News (last resort) ─────────────────────────
async function viaBing(source, domain) {
  const xml = await get('https://www.bing.com/news/search?q=site%3A' + domain + '&format=rss&count=20', 6000);
  if (!xml || !/<item[\s>]/i.test(xml)) return [];
  const chunks = xml.match(/<item[\s>][\s\S]*?<\/item>/gi) || [];
  return chunks.map((item, i) => {
    const raw = deepDecode(tag(item, 'link'));
    const real = (raw.match(/[?&]url=([^&]+)/) || [])[1];
    let link = '';
    try { link = real ? decodeURIComponent(real) : ''; } catch (e) {}
    if (!link.includes(domain)) link = '';
    let image = deepDecode(tag(item, 'News:Image'));
    image = image ? absUrl(image) + '&w=900&h=600&c=14' : null;
    return story(source, { title: toText(tag(item, 'title')), link, excerptHtml: decodeEntities(tag(item, 'description')),
      image, ts: Date.parse(tag(item, 'pubDate')), order: i, via: 'bing' });
  });
}

// ───────────────────────── story shape ─────────────────────────
function hash(s) { let h = 5381; for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0; return h.toString(36); }

function story(source, f) {
  const link = (f.link || '').trim();
  let title = (f.title || '').replace(/\s+/g, ' ').trim();
  // Search-engine titles append the publication name. Publisher feeds do not.
  title = title.replace(new RegExp('\\s+[-–|]\\s+(' + source.name + '|ArchDaily|Dezeen|Archinect|Metropolis( Magazine)?|Architectural Record|designboom|The Architect[’\']s Newspaper|Domus( Web)?)\\s*$', 'i'), '');
  const excerpt = cleanExcerpt(f.excerptHtml);
  const ts = Number.isFinite(f.ts) ? f.ts : Date.now() - (f.order || 0) * 3600000 - 86400000;
  return {
    id: source.id + '-' + hash(link || title),
    sourceId: source.id, sourceName: source.name, sourceColor: source.color, sourceDomain: source.domain,
    title, link, excerpt, short: makeShort(excerpt, 60),
    image: goodImg(f.image) ? f.image : null,
    pubDate: new Date(ts).toISOString(), timestamp: ts, dated: Number.isFinite(f.ts), via: f.via
  };
}

const JUNK = [/^(log ?in|sign ?in|subscribe|create account|register to|login to|one moment|just a moment|access denied|page not found)/i];
function valid(s) {
  return s.title && s.title.length > 3 && /^https:\/\//.test(s.link) && !/news\.google\.com|bing\.com/.test(s.link) && !JUNK.some(rx => rx.test(s.title));
}

// ───────────────────────── orchestration ─────────────────────────
async function fetchSource(source) {
  const tried = [];
  for (const step of source.plan) {
    let items = [];
    try {
      if (step[0] === 'rss') items = await viaRss(source, step[1]);
      else if (step[0] === 'wp') items = await viaWp(source, step[1], step[2]);
      else if (step[0] === 'domus') items = await viaDomus(source, step[1]);
      else if (step[0] === 'bing') items = await viaBing(source, step[1]);
    } catch (e) { items = []; }
    items = items.filter(valid);
    tried.push(step[0] + ':' + items.length);
    if (items.length > 0) {
      items.sort((a, b) => b.timestamp - a.timestamp);
      return { items: items.slice(0, PER_SOURCE), via: step[0], tried };
    }
  }
  return { items: [], via: null, tried };
}

// Last pass: stories still missing an image get their article's og:image.
async function enrichWithOG(stories) {
  const missing = stories.filter(s => !s.image).slice(0, 16);
  await Promise.allSettled(missing.map(async s => {
    const html = await get(s.link, 3000, 'text/html,*/*');
    if (!html) return;
    const og = html.match(/<meta[^>]+(?:property|name)=["'](?:og:image|twitter:image)["'][^>]+content=["']([^"']+)["']/i)
            || html.match(/<meta[^>]+content=["']([^"']+)["'][^>]+(?:property|name)=["'](?:og:image|twitter:image)["']/i);
    const u = og && absUrl(og[1]);
    if (goodImg(u) && !/\/og\/flat\/|default|placeholder|logo/i.test(u)) { s.image = u; s.imageVia = 'og'; }
  }));
}

async function buildFeed() {
  const results = await Promise.all(SOURCES.map(s => fetchSource(s).catch(() => ({ items: [], via: null, tried: ['error'] }))));
  let all = [];
  const sources = {}, health = {};
  results.forEach((r, i) => { all = all.concat(r.items); });

  const seen = new Set();
  all = all.filter(s => {
    const k = s.link.replace(/[?#].*$/, '').replace(/\/$/, '').toLowerCase();
    const t = s.sourceId + '|' + s.title.slice(0, 60).toLowerCase();
    if (seen.has(k) || seen.has(t)) return false;
    seen.add(k); seen.add(t); return true;
  });

  const cutoff = Date.now() - 90 * 86400000;
  all = all.filter(s => s.timestamp >= cutoff && s.timestamp <= Date.now() + 86400000);
  all.sort((a, b) => b.timestamp - a.timestamp);

  await enrichWithOG(all);

  SOURCES.forEach((src, i) => {
    const mine = all.filter(s => s.sourceId === src.id);
    sources[src.id] = mine.length;
    health[src.id] = { via: results[i].via, tried: results[i].tried, stories: mine.length,
      withImage: mine.filter(s => s.image).length, withShort: mine.filter(s => s.short).length };
  });
  return { success: true, version: 7, count: all.length, sources, health, updatedAt: new Date().toISOString(), stories: all };
}

module.exports = async function handler(req, res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  if (req.method === 'OPTIONS') { res.status(200).end(); return; }
  try {
    const feed = await buildFeed();
    // Never let the CDN keep an empty feed for ten minutes.
    res.setHeader('Cache-Control', feed.count >= 20 ? 's-maxage=600, stale-while-revalidate=3600' : 'no-store');
    if (req.query && req.query.health) { res.status(200).json({ version: 7, count: feed.count, updatedAt: feed.updatedAt, health: feed.health }); return; }
    res.status(200).json(feed);
  } catch (error) {
    res.setHeader('Cache-Control', 'no-store');
    res.status(500).json({ success: false, error: error.message, stories: [] });
  }
};

module.exports._test = { parseFeed, viaDomus, viaBing, viaWp, makeShort, cleanExcerpt, story, SOURCES, buildFeed };
