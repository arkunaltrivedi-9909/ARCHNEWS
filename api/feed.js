// ARCHNEWS — Feed API v5
// Primary: direct RSS | Fallback: Google News RSS (bypasses all blocks)

const SOURCES = [
  {
    id: 'archdaily', name: 'ArchDaily', color: '#bf2d0a',
    direct: ['https://www.archdaily.com/feed', 'https://feeds.feedburner.com/Archdaily'],
    gnews: 'https://news.google.com/rss/search?q=site:archdaily.com+architecture&hl=en-US&gl=US&ceid=US:en'
  },
  {
    id: 'dezeen', name: 'Dezeen', color: '#0a4fb8',
    direct: ['https://www.dezeen.com/feed/', 'https://feeds.feedburner.com/dezeen'],
    gnews: 'https://news.google.com/rss/search?q=site:dezeen.com+architecture&hl=en-US&gl=US&ceid=US:en'
  },
  {
    id: 'archinect', name: 'Archinect', color: '#0a8a35',
    direct: ['https://archinect.com/feed/news', 'https://archinect.com/feed'],
    gnews: 'https://news.google.com/rss/search?q=site:archinect.com&hl=en-US&gl=US&ceid=US:en'
  },
  {
    id: 'metropolis', name: 'Metropolis', color: '#6b08a8',
    direct: ['https://metropolismag.com/feed/', 'https://www.metropolismag.com/feed/'],
    gnews: 'https://news.google.com/rss/search?q=site:metropolismag.com+architecture&hl=en-US&gl=US&ceid=US:en'
  },
  {
    id: 'archrecord', name: 'Arch Record', color: '#b87008',
    direct: ['https://www.architecturalrecord.com/rss/articles', 'https://www.architecturalrecord.com/rss'],
    gnews: 'https://news.google.com/rss/search?q=site:architecturalrecord.com&hl=en-US&gl=US&ceid=US:en'
  },
  {
    id: 'designboom', name: 'Designboom', color: '#a80875',
    direct: ['https://www.designboom.com/architecture/rss/', 'https://www.designboom.com/rss/'],
    gnews: 'https://news.google.com/rss/search?q=site:designboom.com+architecture&hl=en-US&gl=US&ceid=US:en'
  },
  {
    id: 'archpaper', name: 'Arch Paper', color: '#087ab8',
    direct: ['https://www.archpaper.com/feed/', 'https://archpaper.com/feed/'],
    gnews: 'https://news.google.com/rss/search?q=site:archpaper.com&hl=en-US&gl=US&ceid=US:en'
  },
  {
    id: 'domus', name: 'Domus', color: '#5a5a3a',
    direct: ['https://www.domusweb.it/en/rss.xml', 'https://www.domusweb.it/rss.xml'],
    gnews: 'https://news.google.com/rss/search?q=site:domusweb.it+architecture&hl=en-US&gl=US&ceid=US:en'
  }
];

function extractTag(xml, tag) {
  const cd = xml.match(new RegExp('<' + tag + '[^>]*><!\\[CDATA\\[([\\s\\S]*?)\\]\\]><\\/' + tag + '>', 'i'));
  if (cd) return cd[1].trim();
  const pl = xml.match(new RegExp('<' + tag + '[^>]*>([\\s\\S]*?)<\\/' + tag + '>', 'i'));
  return pl ? pl[1].replace(/<[^>]+>/g, '').trim() : '';
}

function extractImage(chunk) {
  const patterns = [
    /<media:(?:content|thumbnail)[^>]+url=["']([^"']+)["']/i,
    /<enclosure[^>]+url=["']([^"']+\.(?:jpg|jpeg|png|webp))["']/i,
    /<img[^>]+src=["']([^"']+)["']/i,
  ];
  for (const rx of patterns) {
    const m = chunk.match(rx);
    if (m && m[1] && m[1].startsWith('http')) return m[1];
  }
  return null;
}

function clean(s) {
  return (s || '').replace(/<[^>]+>/g, ' ')
    .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"').replace(/&#8217;/g, "'").replace(/&#8216;/g, "'")
    .replace(/&#8220;/g, '"').replace(/&#8221;/g, '"').replace(/&#8230;/g, '...')
    .replace(/&nbsp;/g, ' ').replace(/\s+/g, ' ').trim();
}

function parseXML(xml, source) {
  const items = [];
  let m;
  const itemRx = /<item[\s>]([\s\S]*?)<\/item>/gi;
  const entryRx = /<entry[\s>]([\s\S]*?)<\/entry>/gi;
  while ((m = itemRx.exec(xml)) !== null) items.push(m[1]);
  while ((m = entryRx.exec(xml)) !== null) items.push(m[1]);

  return items.slice(0, 12).map((item, i) => {
    const title = clean(extractTag(item, 'title'));
    let link = clean(extractTag(item, 'link')).replace(/&amp;/g, '&');
    if (!link || !link.startsWith('http')) {
      const lm = item.match(/<link[^>]+href=["']([^"']+)["']/i);
      if (lm) link = lm[1];
    }
    // For Google News items, extract original URL from the link
    if (link && link.includes('news.google.com')) {
      const origMatch = item.match(/<feedburner:origLink>([^<]+)<\/feedburner:origLink>/i)
        || item.match(/url=([^&"]+)/i);
      if (origMatch) link = decodeURIComponent(origMatch[1]);
    }
    const desc = extractTag(item, 'description') || extractTag(item, 'summary') || '';
    const pub = extractTag(item, 'pubDate') || extractTag(item, 'published') || '';
    const ts = pub ? (new Date(pub).getTime() || Date.now() - i * 3600000) : Date.now() - i * 3600000;
    return {
      id: source.id + '-' + i + '-' + ts,
      sourceId: source.id, sourceName: source.name, sourceColor: source.color,
      title, link, excerpt: clean(desc).slice(0, 280),
      image: extractImage(item) || null,
      pubDate: pub ? new Date(pub).toISOString() : new Date().toISOString(),
      timestamp: ts
    };
  }).filter(s => s.title && s.title.length > 3 && s.link && s.link.startsWith('http'));
}

async function tryFetch(url, timeoutMs) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const r = await fetch(url, {
      signal: ctrl.signal,
      headers: {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36',
        'Accept': 'application/rss+xml, application/xml, text/xml, */*',
        'Accept-Language': 'en-US,en;q=0.9',
        'Cache-Control': 'no-cache'
      }
    });
    clearTimeout(t);
    if (!r.ok) return null;
    return await r.text();
  } catch (e) {
    clearTimeout(t);
    return null;
  }
}

async function fetchSource(source) {
  // Step 1: try direct RSS URLs
  for (const url of source.direct) {
    const xml = await tryFetch(url, 5000);
    if (xml && (xml.includes('<item') || xml.includes('<entry'))) {
      const items = parseXML(xml, source);
      if (items.length > 0) return { items, method: 'direct' };
    }
  }
  // Step 2: Google News RSS fallback — works for ALL sources
  const gnews = await tryFetch(source.gnews, 6000);
  if (gnews && (gnews.includes('<item') || gnews.includes('<entry'))) {
    const items = parseXML(gnews, source);
    if (items.length > 0) return { items, method: 'gnews' };
  }
  return { items: [], method: 'failed' };
}

module.exports = async function handler(req, res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Content-Type', 'application/json');
  res.setHeader('Cache-Control', 's-maxage=600, stale-while-revalidate=1800');
  if (req.method === 'OPTIONS') { res.status(200).end(); return; }

  try {
    const results = await Promise.allSettled(SOURCES.map(s => fetchSource(s)));
    let allStories = [];
    const sourceStats = {};

    results.forEach((result, i) => {
      const source = SOURCES[i];
      if (result.status === 'fulfilled' && result.value.items.length > 0) {
        allStories = allStories.concat(result.value.items);
        sourceStats[source.id] = result.value.items.length;
      } else {
        sourceStats[source.id] = 0;
      }
    });

    allStories.sort((a, b) => b.timestamp - a.timestamp);

    const seen = new Set();
    allStories = allStories.filter(s => {
      const key = s.title.slice(0, 50).toLowerCase();
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });

    res.status(200).json({
      success: true, count: allStories.length,
      sources: sourceStats, updatedAt: new Date().toISOString(),
      stories: allStories
    });
  } catch (error) {
    res.status(500).json({ success: false, error: error.message, stories: [] });
  }
};
