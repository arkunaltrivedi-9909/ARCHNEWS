// ARCHNEWS — Feed API v6
// Direct RSS first → Google News fallback with proper HTML-decoded image extraction

const SOURCES = [
  { id:'archdaily',  name:'ArchDaily',   color:'#bf2d0a',
    direct:['https://www.archdaily.com/feed','https://feeds.feedburner.com/Archdaily'],
    gnews:'https://news.google.com/rss/search?q=site:archdaily.com+architecture&hl=en-US&gl=US&ceid=US:en' },
  { id:'dezeen',     name:'Dezeen',      color:'#0a4fb8',
    direct:['https://www.dezeen.com/feed/','https://feeds.feedburner.com/dezeen'],
    gnews:'https://news.google.com/rss/search?q=site:dezeen.com+architecture&hl=en-US&gl=US&ceid=US:en' },
  { id:'archinect',  name:'Archinect',   color:'#0a8a35',
    direct:['https://archinect.com/feed/news','https://archinect.com/feed'],
    gnews:'https://news.google.com/rss/search?q=site:archinect.com&hl=en-US&gl=US&ceid=US:en' },
  { id:'metropolis', name:'Metropolis',  color:'#6b08a8',
    direct:['https://metropolismag.com/feed/','https://www.metropolismag.com/feed/'],
    gnews:'https://news.google.com/rss/search?q=site:metropolismag.com+architecture&hl=en-US&gl=US&ceid=US:en' },
  { id:'archrecord', name:'Arch Record', color:'#b87008',
    direct:['https://www.architecturalrecord.com/rss/articles'],
    gnews:'https://news.google.com/rss/search?q=site:architecturalrecord.com&hl=en-US&gl=US&ceid=US:en' },
  { id:'designboom', name:'Designboom',  color:'#a80875',
    direct:['https://www.designboom.com/architecture/rss/','https://www.designboom.com/rss/'],
    gnews:'https://news.google.com/rss/search?q=site:designboom.com+architecture&hl=en-US&gl=US&ceid=US:en' },
  { id:'archpaper',  name:'Arch Paper',  color:'#087ab8',
    direct:['https://www.archpaper.com/feed/'],
    gnews:'https://news.google.com/rss/search?q=site:archpaper.com&hl=en-US&gl=US&ceid=US:en' },
  { id:'domus',      name:'Domus',       color:'#5a5a3a',
    direct:['https://www.domusweb.it/en/rss.xml'],
    gnews:'https://news.google.com/rss/search?q=site:domusweb.it+architecture&hl=en-US&gl=US&ceid=US:en' }
];

// Decode HTML entities (critical for Google News encoded descriptions)
function decodeHTML(str) {
  return (str || '')
    .replace(/&lt;/g,'<').replace(/&gt;/g,'>')
    .replace(/&amp;/g,'&').replace(/&quot;/g,'"')
    .replace(/&#39;/g,"'").replace(/&nbsp;/g,' ')
    .replace(/&#(\d+);/g, (_,n) => String.fromCharCode(n));
}

function extractTag(xml, tag) {
  const cd = xml.match(new RegExp('<'+tag+'[^>]*><!\\[CDATA\\[([\\s\\S]*?)\\]\\]><\\/'+tag+'>','i'));
  if (cd) return cd[1].trim();
  const pl = xml.match(new RegExp('<'+tag+'[^>]*>([\\s\\S]*?)<\\/'+tag+'>','i'));
  return pl ? pl[1].trim() : '';
}

function extractImage(itemChunk, description) {
  // 1. media:content or media:thumbnail (direct RSS feeds)
  const media = itemChunk.match(/<media:(?:content|thumbnail)[^>]+url=["']([^"']+)["']/i);
  if (media && media[1].startsWith('http')) return media[1];

  // 2. enclosure tag
  const enc = itemChunk.match(/<enclosure[^>]+url=["']([^"']+\.(?:jpg|jpeg|png|webp))[^>]*/i);
  if (enc && enc[1].startsWith('http')) return enc[1];

  // 3. img tag in raw item chunk
  const imgRaw = itemChunk.match(/<img[^>]+src=["']([^"']+)["']/i);
  if (imgRaw && imgRaw[1].startsWith('http')) return imgRaw[1];

  // 4. CRITICAL: Google News encodes img in description as HTML entities — decode first
  const decoded = decodeHTML(description || '');
  const imgDecoded = decoded.match(/<img[^>]+src=["']([^"']+)["']/i);
  if (imgDecoded && imgDecoded[1].startsWith('http')) return imgDecoded[1];

  // 5. Any https image URL in the decoded description
  const anyImg = decoded.match(/https?:\/\/[^\s"'<>]+\.(?:jpg|jpeg|png|webp|gif)(?:\?[^\s"'<>]*)?/i);
  if (anyImg) return anyImg[0];

  return null;
}

function cleanText(s) {
  return (s||'').replace(/<[^>]+>/g,' ')
    .replace(/&amp;/g,'&').replace(/&lt;/g,'<').replace(/&gt;/g,'>')
    .replace(/&quot;/g,'"').replace(/&#8217;/g,"'").replace(/&#8216;/g,"'")
    .replace(/&#8220;/g,'"').replace(/&#8221;/g,'"').replace(/&#8230;/g,'...')
    .replace(/&nbsp;/g,' ').replace(/\s+/g,' ').trim();
}

function parseXML(xml, source) {
  const items = [];
  let m;
  const itemRx = /<item[\s>]([\s\S]*?)<\/item>/gi;
  const entryRx = /<entry[\s>]([\s\S]*?)<\/entry>/gi;
  while ((m = itemRx.exec(xml)) !== null) items.push(m[1]);
  while ((m = entryRx.exec(xml)) !== null) items.push(m[1]);

  return items.slice(0, 12).map((item, i) => {
    const title = cleanText(extractTag(item,'title'));
    let link = cleanText(extractTag(item,'link')).replace(/&amp;/g,'&');
    if (!link || !link.startsWith('http')) {
      const lm = item.match(/<link[^>]+href=["']([^"']+)["']/i);
      if (lm) link = lm[1];
    }
    const description = extractTag(item,'description') || extractTag(item,'summary') || '';
    const pub = extractTag(item,'pubDate') || extractTag(item,'published') || '';
    const ts = pub ? (new Date(pub).getTime() || Date.now()-i*3600000) : Date.now()-i*3600000;
    const image = extractImage(item, description);
    const excerpt = cleanText(description).replace(/Read more.*/i,'').slice(0,280);

    return {
      id: source.id+'-'+i+'-'+ts,
      sourceId: source.id, sourceName: source.name, sourceColor: source.color,
      title, link: link||'', excerpt, image: image||null,
      pubDate: pub ? new Date(pub).toISOString() : new Date().toISOString(),
      timestamp: ts
    };
  }).filter(s => s.title && s.title.length>3 && s.link && s.link.startsWith('http'));
}

async function tryFetch(url, ms) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), ms);
  try {
    const r = await fetch(url, {
      signal: ctrl.signal,
      headers: {
        'User-Agent':'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36',
        'Accept':'application/rss+xml,application/xml,text/xml,*/*',
        'Accept-Language':'en-US,en;q=0.9'
      }
    });
    clearTimeout(t);
    if (!r.ok) return null;
    return await r.text();
  } catch(e) { clearTimeout(t); return null; }
}

// Fetch OG image for stories that have no image (max 5 concurrent, 3s timeout each)
async function enrichWithOGImages(stories) {
  const noImg = stories.filter(s => !s.image).slice(0, 8);
  await Promise.allSettled(noImg.map(async story => {
    try {
      const ctrl = new AbortController();
      const t = setTimeout(() => ctrl.abort(), 3000);
      const r = await fetch(story.link, { signal: ctrl.signal, headers:{'User-Agent':'Mozilla/5.0'} });
      clearTimeout(t);
      if (!r.ok) return;
      const html = await r.text();
      const og = html.match(/<meta[^>]+property=["']og:image["'][^>]+content=["']([^"']+)["']/i)
                || html.match(/<meta[^>]+content=["']([^"']+)["'][^>]+property=["']og:image["']/i);
      if (og && og[1] && og[1].startsWith('http')) {
        story.image = og[1];
      }
    } catch(e) {}
  }));
  return stories;
}

async function fetchSource(source) {
  // Step 1: direct RSS
  for (const url of source.direct) {
    const xml = await tryFetch(url, 5000);
    if (xml && (xml.includes('<item') || xml.includes('<entry'))) {
      const items = parseXML(xml, source);
      if (items.length > 0) return items;
    }
  }
  // Step 2: Google News RSS
  const gxml = await tryFetch(source.gnews, 6000);
  if (gxml && (gxml.includes('<item') || gxml.includes('<entry'))) {
    const items = parseXML(gxml, source);
    if (items.length > 0) return items;
  }
  return [];
}

module.exports = async function handler(req, res) {
  res.setHeader('Access-Control-Allow-Origin','*');
  res.setHeader('Content-Type','application/json');
  res.setHeader('Cache-Control','s-maxage=600,stale-while-revalidate=1800');
  if (req.method==='OPTIONS') { res.status(200).end(); return; }

  try {
    const results = await Promise.allSettled(SOURCES.map(s => fetchSource(s)));
    let allStories = [];
    const sourceStats = {};

    results.forEach((result, i) => {
      const src = SOURCES[i];
      if (result.status==='fulfilled' && result.value.length>0) {
        allStories = allStories.concat(result.value);
        sourceStats[src.id] = result.value.length;
      } else { sourceStats[src.id] = 0; }
    });

    allStories.sort((a,b) => b.timestamp - a.timestamp);

    // Deduplicate
    const seen = new Set();
    allStories = allStories.filter(s => {
      const k = s.title.slice(0,50).toLowerCase();
      if (seen.has(k)) return false;
      seen.add(k); return true;
    });

    // Enrich missing images with OG scraping (fast, parallel, 3s timeout each)
    allStories = await enrichWithOGImages(allStories);

    res.status(200).json({
      success:true, count:allStories.length,
      sources:sourceStats, updatedAt:new Date().toISOString(),
      stories:allStories
    });
  } catch(error) {
    res.status(500).json({ success:false, error:error.message, stories:[] });
  }
};
