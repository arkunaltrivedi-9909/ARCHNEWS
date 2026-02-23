// ARCHNEWS — Live RSS Feed API
// Vercel Serverless Function
// Fetches real stories from 8 architecture publications on every request

const SOURCES = [
  { id: 'archdaily',  name: 'ArchDaily',   color: '#bf2d0a', urls: ['https://feeds.feedburner.com/Archdaily', 'https://www.archdaily.com/feed'] },
  { id: 'dezeen',     name: 'Dezeen',      color: '#0a4fb8', urls: ['https://feeds.feedburner.com/dezeen', 'https://www.dezeen.com/feed/'] },
  { id: 'archinect',  name: 'Archinect',   color: '#0a8a35', urls: ['https://archinect.com/feed/news', 'https://archinect.com/feed'] },
  { id: 'metropolis', name: 'Metropolis',  color: '#6b08a8', urls: ['https://metropolismag.com/feed/'] },
  { id: 'archrecord', name: 'Arch Record', color: '#b87008', urls: ['https://www.architecturalrecord.com/rss/articles'] },
  { id: 'designboom', name: 'Designboom',  color: '#a80875', urls: ['https://www.designboom.com/architecture/rss/', 'https://www.designboom.com/rss/'] },
  { id: 'archpaper',  name: 'Arch Paper',  color: '#087ab8', urls: ['https://www.archpaper.com/feed/'] },
  { id: 'domus',      name: 'Domus',       color: '#333322', urls: ['https://www.domusweb.it/en/rss.xml'] }
];

function extractTag(xml, tag) {
  const cdataRx = new RegExp('<' + tag + '[^>]*><!\\[CDATA\\[([\\s\\S]*?)\\]\\]></' + tag + '>', 'i');
  const m1 = xml.match(cdataRx);
  if (m1) return m1[1].trim();
  const plainRx = new RegExp('<' + tag + '[^>]*>([\\s\\S]*?)</' + tag + '>', 'i');
  const m2 = xml.match(plainRx);
  if (m2) return m2[1].trim();
  return '';
}

function extractImage(item, desc) {
  const m1 = item.match(/<media:(?:content|thumbnail)[^>]+url=["']([^"']+)["']/i);
  if (m1) return m1[1];
  const m2 = item.match(/<enclosure[^>]+url=["']([^"']+)["']/i);
  if (m2) return m2[1];
  const m3 = (desc || '').match(/<img[^>]+src=["']([^"']+)["']/i);
  if (m3) return m3[1];
  return null;
}

function stripHtml(str) {
  return (str || '')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"').replace(/&#8217;/g, "'").replace(/&#8216;/g, "'")
    .replace(/&#8220;/g, '"').replace(/&#8221;/g, '"').replace(/&#8230;/g, '...')
    .replace(/\s+/g, ' ').trim();
}

function parseRSS(xml, source) {
  const rawItems = [];
  let m;
  const itemRx = /<item[\s>]([\s\S]*?)<\/item>/gi;
  const entryRx = /<entry[\s>]([\s\S]*?)<\/entry>/gi;
  while ((m = itemRx.exec(xml)) !== null) rawItems.push(m[1]);
  while ((m = entryRx.exec(xml)) !== null) rawItems.push(m[1]);

  return rawItems.slice(0, 15).map((item, i) => {
    const title = stripHtml(extractTag(item, 'title'));
    let link = extractTag(item, 'link');
    if (!link) {
      const lm = item.match(/<link[^>]+href=["']([^"']+)["']/i);
      if (lm) link = lm[1];
    }
    link = stripHtml(link);
    const description = extractTag(item, 'description') || extractTag(item, 'summary') || extractTag(item, 'content:encoded') || '';
    const pubDate = extractTag(item, 'pubDate') || extractTag(item, 'published') || extractTag(item, 'updated') || new Date().toISOString();
    const image = extractImage(item, description);
    const excerpt = stripHtml(description).replace(/Read more.*$/i, '').slice(0, 300);
    const ts = new Date(pubDate).getTime() || (Date.now() - i * 3600000);

    return {
      id: source.id + '-' + i + '-' + ts,
      sourceId: source.id,
      sourceName: source.name,
      sourceColor: source.color,
      title: title || '',
      link: link || '',
      excerpt,
      image: image || null,
      pubDate: new Date(pubDate).toISOString(),
      timestamp: ts
    };
  }).filter(s => s.title && s.link && s.link.startsWith('http'));
}

async function fetchSource(source) {
  for (const url of source.urls) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 8000);
    try {
      const res = await fetch(url, {
        signal: controller.signal,
        headers: {
          'User-Agent': 'ARCHNEWS/2.0 (+https://archnews.vercel.app)',
          'Accept': 'application/rss+xml, application/xml, text/xml, */*'
        }
      });
      clearTimeout(timer);
      if (!res.ok) continue;
      const text = await res.text();
      const items = parseRSS(text, source);
      if (items.length > 0) return items;
    } catch (e) {
      clearTimeout(timer);
    }
  }
  return [];
}

module.exports = async function handler(req, res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, OPTIONS');
  res.setHeader('Content-Type', 'application/json');
  res.setHeader('Cache-Control', 's-maxage=900, stale-while-revalidate=1800');

  if (req.method === 'OPTIONS') { res.status(200).end(); return; }

  try {
    const results = await Promise.allSettled(SOURCES.map(s => fetchSource(s)));
    let allStories = [];
    const sourceStats = {};

    results.forEach((result, i) => {
      const source = SOURCES[i];
      if (result.status === 'fulfilled' && result.value.length > 0) {
        allStories = allStories.concat(result.value);
        sourceStats[source.id] = result.value.length;
      } else {
        sourceStats[source.id] = 0;
      }
    });

    allStories.sort((a, b) => b.timestamp - a.timestamp);

    res.status(200).json({
      success: true,
      count: allStories.length,
      sources: sourceStats,
      updatedAt: new Date().toISOString(),
      stories: allStories
    });

  } catch (error) {
    res.status(500).json({ success: false, error: error.message, stories: [] });
  }
};
