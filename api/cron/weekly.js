// ARCHNEWS — Weekly Email Cron
// Runs every Monday at 8am IST (2:30 UTC)
// Fetches top 5 stories, emails all subscribers via Resend

const RESEND_KEY = process.env.RESEND_API_KEY;
const UPSTASH_URL = process.env.UPSTASH_REDIS_REST_URL;
const UPSTASH_TOKEN = process.env.UPSTASH_REDIS_REST_TOKEN;

async function redisCmd(cmd, ...args) {
  const r = await fetch(`${UPSTASH_URL}/${[cmd, ...args].map(encodeURIComponent).join('/')}`, {
    headers: { Authorization: `Bearer ${UPSTASH_TOKEN}` }
  });
  return r.json();
}

async function getStories() {
  try {
    const r = await fetch('https://archnews.vercel.app/api/feed');
    const d = await r.json();
    return (d.stories || []).slice(0, 5);
  } catch(e) { return []; }
}

function buildEmail(stories, weekDate) {
  const srcColors = {
    archdaily:'#bf2d0a', dezeen:'#0a4fb8', archinect:'#0a8a35',
    metropolis:'#6b08a8', archrecord:'#b87008', designboom:'#a80875',
    archpaper:'#087ab8', domus:'#333322'
  };

  const storyRows = stories.map((s, i) => {
    const color = srcColors[s.sourceId] || '#555';
    const excerpt = (s.excerpt || '').slice(0, 140) + '...';
    return `
      <tr>
        <td style="padding:20px 0;border-bottom:1px solid #cec8bb;">
          <div style="margin-bottom:6px;">
            <span style="background:${color};color:#fff;font-family:monospace;font-size:9px;letter-spacing:2px;padding:2px 8px;text-transform:uppercase;">${s.sourceName}</span>
          </div>
          <div style="font-family:Arial Black,sans-serif;font-size:11px;color:#908a82;letter-spacing:2px;margin-bottom:6px;">0${i+1}</div>
          <h3 style="font-family:Georgia,serif;font-size:18px;font-weight:700;color:#0c0b09;margin:0 0 8px;line-height:1.3;">${s.title}</h3>
          <p style="font-size:13px;color:#46413b;line-height:1.7;margin:0 0 10px;">${excerpt}</p>
          <a href="${s.link}" style="font-family:monospace;font-size:9px;color:#c0300a;letter-spacing:2px;text-transform:uppercase;text-decoration:none;">READ FULL STORY →</a>
        </td>
      </tr>`;
  }).join('');

  return `
    <div style="max-width:600px;margin:0 auto;background:#f5f1ea;font-family:Georgia,serif;">
      
      <!-- HEADER -->
      <div style="background:#0c0b09;padding:24px 32px;border-bottom:3px solid #c0300a;">
        <div style="margin-bottom:8px;">
          <span style="font-family:Arial Black,sans-serif;font-size:36px;letter-spacing:4px;color:#fdfaf4;">ARCH</span><span style="font-family:Arial Black,sans-serif;font-size:36px;letter-spacing:4px;color:#c0300a;">NEWS</span>
        </div>
        <div style="font-family:monospace;font-size:9px;color:#555;letter-spacing:3px;text-transform:uppercase;">Architecture Intelligence · Weekly Edition</div>
        <div style="font-family:monospace;font-size:9px;color:#3a3a34;letter-spacing:2px;margin-top:4px;">${weekDate}</div>
      </div>

      <!-- INTRO -->
      <div style="padding:28px 32px 0;border-bottom:2px solid #0c0b09;">
        <p style="font-size:14px;color:#46413b;line-height:1.8;margin:0 0 20px;">
          This week's 5 most important architecture stories — curated from ArchDaily, Dezeen, Archinect, Metropolis, Architectural Record, Designboom, Arch Paper, and Domus.
        </p>
      </div>

      <!-- STORIES -->
      <div style="padding:0 32px;">
        <table width="100%" cellpadding="0" cellspacing="0">
          ${storyRows}
        </table>
      </div>

      <!-- CTA -->
      <div style="padding:28px 32px;background:#0c0b09;margin-top:32px;text-align:center;">
        <p style="font-family:monospace;font-size:10px;color:#555;letter-spacing:2px;margin-bottom:14px;">MORE STORIES UPDATED LIVE</p>
        <a href="https://archnews.vercel.app" style="background:#c0300a;color:#fff;font-family:monospace;font-size:9px;letter-spacing:2px;padding:12px 24px;text-decoration:none;text-transform:uppercase;">Visit ARCHNEWS →</a>
      </div>

      <!-- FOOTER -->
      <div style="padding:20px 32px;border-top:1px solid #1a1a14;background:#0c0b09;">
        <p style="font-family:monospace;font-size:8px;color:#333;letter-spacing:1px;margin:0;line-height:1.8;">
          ARCHNEWS Weekly · Curated by Kunal Trivedi, Licensed Architect<br>
          archnews.vercel.app · architectonworks@gmail.com<br>
          You're receiving this because you subscribed at archnews.vercel.app
        </p>
      </div>

    </div>`;
}

module.exports = async function handler(req, res) {
  // Security: only Vercel cron can call this
  if (req.headers['authorization'] !== `Bearer ${process.env.CRON_SECRET}`) {
    return res.status(401).json({ error: 'Unauthorized' });
  }

  try {
    // Get all subscribers
    const subResult = await redisCmd('SMEMBERS', 'archnews:subscribers');
    const subscribers = subResult.result || [];
    if (!subscribers.length) {
      return res.status(200).json({ success: true, message: 'No subscribers yet' });
    }

    // Get top 5 stories
    const stories = await getStories();
    if (!stories.length) {
      return res.status(200).json({ success: false, message: 'No stories fetched' });
    }

    const weekDate = new Date().toLocaleDateString('en-US', {
      weekday: 'long', year: 'numeric', month: 'long', day: 'numeric'
    }).toUpperCase();

    const html = buildEmail(stories, weekDate);

    // Send to all subscribers in batches of 50
    let sent = 0;
    for (let i = 0; i < subscribers.length; i += 50) {
      const batch = subscribers.slice(i, i + 50);
      await fetch('https://api.resend.com/emails/batch', {
        method: 'POST',
        headers: { Authorization: `Bearer ${RESEND_KEY}`, 'Content-Type': 'application/json' },
        body: JSON.stringify(batch.map(email => ({
          from: 'ARCHNEWS Weekly <onboarding@resend.dev>',
          to: email,
          subject: `ARCHNEWS Weekly — ${new Date().toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })}`,
          html
        })))
      });
      sent += batch.length;
    }

    // Log send
    await redisCmd('SET', 'archnews:last_sent', new Date().toISOString());
    await redisCmd('INCR', 'archnews:emails_sent');

    res.status(200).json({ success: true, sent, subscribers: subscribers.length });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
};
