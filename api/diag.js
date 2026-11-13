// Temporary diagnostic: which upstream URLs answer from Vercel's IPs. Removed after the feed fix ships.
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36';
const FEED_UA = 'Feedly/1.0 (+http://www.feedly.com/fetcher.html; like FeedFetcher-Google)';

const TESTS = {
  archdaily: [
    'https://www.archdaily.com/feed',
    'https://feeds.feedburner.com/Archdaily',
    'https://www.archdaily.com/search/api/v1/us/all?q=&page=1',
    'https://www.archdaily.com/',
    'https://www.bing.com/news/search?q=site%3Aarchdaily.com&format=rss',
    'https://r.jina.ai/https://www.archdaily.com/feed',
    'https://api.allorigins.win/raw?url=' + encodeURIComponent('https://www.archdaily.com/feed'),
    'https://api.codetabs.com/v1/proxy?quest=' + encodeURIComponent('https://www.archdaily.com/feed'),
  ],
  metropolis: [
    'https://metropolismag.com/feed/',
    'https://metropolismag.com/wp-json/wp/v2/posts?per_page=3&_embed=wp:featuredmedia',
    'https://metropolismag.com/',
    'https://www.bing.com/news/search?q=site%3Ametropolismag.com&format=rss',
    'https://r.jina.ai/https://metropolismag.com/feed/',
    'https://api.allorigins.win/raw?url=' + encodeURIComponent('https://metropolismag.com/feed/'),
    'https://api.codetabs.com/v1/proxy?quest=' + encodeURIComponent('https://metropolismag.com/feed/'),
  ],
  archpaper: [
    'https://www.archpaper.com/feed/',
    'https://www.archpaper.com/wp-json/wp/v2/posts?per_page=3&_embed=wp:featuredmedia',
    'https://www.archpaper.com/',
    'https://www.bing.com/news/search?q=site%3Aarchpaper.com&format=rss',
    'https://r.jina.ai/https://www.archpaper.com/feed/',
    'https://api.allorigins.win/raw?url=' + encodeURIComponent('https://www.archpaper.com/feed/'),
    'https://api.codetabs.com/v1/proxy?quest=' + encodeURIComponent('https://www.archpaper.com/feed/'),
  ],
  domus: [
    'https://www.domusweb.it/en/rss.xml',
    'https://www.domusweb.it/en.rss.xml',
    'https://www.domusweb.it/content/domusweb/en.rss.xml',
    'https://www.domusweb.it/en/architecture.rss.xml',
    'https://www.domusweb.it/en/architecture.html',
    'https://www.domusweb.it/en.html',
    'https://www.bing.com/news/search?q=site%3Adomusweb.it&format=rss',
    'https://r.jina.ai/https://www.domusweb.it/en/architecture.html',
  ],
  archinect: [
    'https://archinect.com/feed/news',
    'https://archinect.com/feed/1/news',
    'https://archinect.com/news',
    'https://www.bing.com/news/search?q=site%3Aarchinect.com&format=rss',
  ],
  dezeen: ['https://www.dezeen.com/feed/', 'https://www.dezeen.com/architecture/feed/'],
  archrecord: ['https://www.architecturalrecord.com/rss/articles'],
  designboom: ['https://www.designboom.com/architecture/rss/'],
};

async function probe(url, ua) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), 7000);
  const started = Date.now();
  try {
    const r = await fetch(url, {
      signal: ctrl.signal, redirect: 'follow',
      headers: { 'User-Agent': ua, 'Accept': 'application/rss+xml,application/xml,text/xml,application/json,text/html;q=0.9,*/*;q=0.8', 'Accept-Language': 'en-US,en;q=0.9' }
    });
    const text = await r.text();
    clearTimeout(t);
    const items = (text.match(/<item[\s>]/gi) || []).length + (text.match(/<entry[\s>]/gi) || []).length;
    const firstItem = (text.match(/<item[\s>][\s\S]*?<\/item>/i) || text.match(/<entry[\s>][\s\S]*?<\/entry>/i) || [''])[0];
    const og = (text.match(/<meta[^>]+property=["']og:image["'][^>]+content=["']([^"']+)["']/i) || [])[1] || null;
    return {
      url, status: r.status, ms: Date.now() - started, type: r.headers.get('content-type'),
      bytes: text.length, items, og,
      hasMedia: /<media:(content|thumbnail)|<enclosure|<img|&lt;img/i.test(firstItem),
      sample: (firstItem || text).slice(0, 1400)
    };
  } catch (e) {
    clearTimeout(t);
    return { url, error: String(e && e.name || e), ms: Date.now() - started };
  }
}

module.exports = async function handler(req, res) {
  res.setHeader('Content-Type', 'application/json');
  res.setHeader('Cache-Control', 'no-store');
  const only = req.query && req.query.s;
  const ua = (req.query && req.query.ua) === 'feed' ? FEED_UA : UA;
  const custom = req.query && req.query.u;
  if (custom) { res.status(200).json(await probe(custom, ua)); return; }
  const out = {};
  const keys = only ? only.split(',') : Object.keys(TESTS);
  await Promise.all(keys.map(async k => {
    out[k] = await Promise.all((TESTS[k] || []).map(u => probe(u, ua)));
  }));
  res.status(200).json(out);
};
