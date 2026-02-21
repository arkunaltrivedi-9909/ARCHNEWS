// ARCHNEWS — Live RSS Feed API
// Vercel Serverless Function — runs server-side, no CORS problems
// Every request fetches fresh stories from all 8 architecture sources

const SOURCES = [
  {
    id: 'archdaily',
    name: 'ArchDaily',
    url: 'https://feeds.feedburner.com/Archdaily',
    color: '#bf2d0a',
    fallback: 'https://www.archdaily.com/feed'
  },
  {
    id: 'dezeen',
    name: 'Dezeen',
    url: 'https://feeds.feedburner.com/dezeen',
    color: '#0a4fb8',
    fallback: 'https://www.dezeen.com/feed/'
  },
  {
    id: 'archinect',
    name: 'Archinect',
    url: 'https://archinect.com/feed/news',
    color: '#0a8a35',
    fallback: 'https://archinect.com/feed'
  },
  {
    id: 'metropolis',
    name: 'Metropolis',
    url: 'https://metropolismag.com/feed/',
    color: '#6b08a8',
    fallback: null
  },
  {
    id: 'archrecord',
    name: 'Arch Record',
    url: 'https://www.architecturalrecord.com/rss/articles',
    color: '#b87008',
    fallback: null
  },
  {
    id: 'designboom',
    name: 'Designboom',
    url: 'https://www.designboom.com/architecture/rss/',
    color: '#a80875',
    fallback: 'https://www.designboom.com/rss/'
  },
  {
    id: 'archpaper',
    name: "Arch Paper",
    url: 'https://www.archpaper.com/feed/',
    color: '#087ab8',
    fallback: null
  },
  {
    id: 'domus',
    name: 'Domus',
    url: 'https://www.domusweb.it/en/rss.xml',
    color: '#1a1a14',
    fallback: null
  }
];

// ── XML PARSER (no npm needed) ──
function extractTag(xml, tag) {
  // Try CDATA first
  const cdataRx = new RegExp(`<${tag}[^>]*><!\\[CDATA\\[([\\s\\S]*?)\\]\\]></${tag}>`, 'i');
  const cdataMatch = xml.match(cdataRx);
  if (cdataMatch) return cdataMatch[1].trim();
  
  // Then plain text
  const plainRx = new RegExp(`<${tag}[^>]*>([\\s\\S]*?)</${tag}>`, 'i');
  const plainMatch = xml.match(plainRx);
  if (plainMatch) return plainMatch[1].trim();
  
  // Self-closing with href/url attr
  const attrRx = new RegExp(`<${tag}[^>]+(?:href|url)=["']([^"']+)["']`, 'i');
  const attrMatch = xml.match(attrRx);
  if (attrMatch) return attrMatch[1].trim();
  
  return '';
}

function extractImage(item, description) {
  // Try media:content
  const mediaRx = /<media:(?:content|thumbnail)[^>]+url=["']([^"']+)["']/i;
  const mediaMatch = item.match(mediaRx);
  if (mediaMatch) return mediaMatch[1];
  
  // Try enclosure
  const encRx = /<enclosure[^>]+url=["']([^"']+)["']/i;
  const encMatch = item.match(encRx);
  if (encMatch) return encMatch[1];
  
  // Try og:image or img in description
  const imgRx = /<img[^>]+src=["']([^"']+)["']/i;
  const imgMatch = (description || '').match(imgRx);
  if (imgMatch) return imgMatch[1];
  
  return null;
}

function stripHtml(str) {
  return (str || '')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#8217;/g, "'")
    .replace(/&#8216;/g, "'")
    .replace(/&#8220;/g, '"')
    .replace(/&#8221;/g, '"')
    .replace(/&#8230;/g, '...')
    .replace(/\s+/g, ' ')
    .trim();
}

function parseRSS(xml, source) {
  // Split into items
  const itemRx = /<item[\s>]([\s\S]*?)<\/item>/gi;
  const entryRx = /<entry[\s>]([\s\S]*?)<\/entry>/gi;
  
  const rawItems = [];
  let match;
  
  while ((match = itemRx.exec(xml)) !== null) rawItems.push(match[1]);
  while ((match = entryRx.exec(xml)) !== null) rawItems.push(match[1]);
  
  return rawItems.slice(0, 15).map((item, i) => {
    const title = stripHtml(extractTag(item, 'title'));
    
    // Link extraction (handle both <link>url</link> and <link href="url"/>)
    let link = extractTag(item, 'link');
    if (!link) {
      const linkAttr = item.match(/<link[^>]+href=["']([^"']+)["']/i);
      if (linkAttr) link = linkAttr[1];
    }
    link = stripHtml(link);
    
    const description = extractTag(item, 'description') || 
                        extractTag(item, 'summary') || 
                        extractTag(item, 'content:encoded') || '';
    
    const pubDate = extractTag(item, 'pubDate') || 
                    extractTag(item, 'published') || 
                    extractTag(item, 'updated') || 
                    new Date().toISOString();
    
    const image = extractImage(item, description);
    const excerpt = stripHtml(description).replace(/Read more.*$/i, '').slice(0, 280);
    
    return {
      id: `${source.id}-${i}-${Date.now()}`,
      sourceId: source.id,
      sourceName: source.name,
      sourceColor: source.color,
      title: title || 'Untitled',
      link: link || source.fallback || '#',
      excerpt: excerpt || '',
      image: image || null,
      pubDate: new Date(pubDate).toISOString(),
      timestamp: new Date(pubDate).getTime() || Date.now() - (i * 3600000)
    };
  }).filter(item => item.title && item.title !== 'Untitled' && item.link && item.link !== '#');
}

async function fetchSource(source) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 7000);
  
  try {
    const res = await fetch(source.url, {
      signal: controller.signal,
      headers: {
        'User-Agent': 'ARCHNEWS/1.0 RSS Aggregator (+https://archnews.vercel.app)',
        'Accept': 'application/rss+xml, application/xml, text/xml, */*'
      }
    });
    clearTimeout(timeout);
    
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const text = await res.text();
    const items = parseRSS(text, source);
    if (items.length > 0) return items;
    
    // Try fallback URL
    if (source.fallback) {
      const res2 = await fetch(source.fallback, {
        headers: { 'User-Agent': 'ARCHNEWS/1.0' },
        signal: AbortSignal.timeout ? AbortSignal.timeout(6000) : undefined
      });
      if (res2.ok) {
        const text2 = await res2.text();
        return parseRSS(text2, source);
      }
    }
    return [];
  } catch (e) {
    clearTimeout(timeout);
    return [];
  }
}

export default async function handler(req, res) {
  // CORS headers
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET');
  res.setHeader('Content-Type', 'application/json');
  
  // Cache for 15 minutes on Vercel edge
  res.setHeader('Cache-Control', 's-maxage=900, stale-while-revalidate=1800');

  try {
    const results = await Promise.allSettled(
      SOURCES.map(source => fetchSource(source))
    );
    
    let allStories = [];
    const sourceStats = {};
    
    results.forEach((result, i) => {
      const source = SOURCES[i];
      if (result.status === 'fulfilled' && result.value.length > 0) {
        allStories = [...allStories, ...result.value];
        sourceStats[source.id] = result.value.length;
      } else {
        sourceStats[source.id] = 0;
      }
    });
    
    // Sort by timestamp descending (newest first)
    allStories.sort((a, b) => b.timestamp - a.timestamp);
    
    res.status(200).json({
      success: true,
      count: allStories.length,
      sources: sourceStats,
      updatedAt: new Date().toISOString(),
      stories: allStories
    });
    
  } catch (error) {
    res.status(500).json({
      success: false,
      error: 'Failed to fetch feeds',
      stories: []
    });
  }
}
