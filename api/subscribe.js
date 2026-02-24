// ARCHNEWS — Subscribe API
// Stores emails in Upstash Redis + sends welcome email via Resend

const RESEND_KEY = process.env.RESEND_API_KEY;
const UPSTASH_URL = process.env.UPSTASH_REDIS_REST_URL;
const UPSTASH_TOKEN = process.env.UPSTASH_REDIS_REST_TOKEN;

async function redisCmd(cmd, ...args) {
  const r = await fetch(`${UPSTASH_URL}/${[cmd, ...args].map(encodeURIComponent).join('/')}`, {
    headers: { Authorization: `Bearer ${UPSTASH_TOKEN}` }
  });
  return r.json();
}

async function sendWelcome(email) {
  await fetch('https://api.resend.com/emails', {
    method: 'POST',
    headers: { Authorization: `Bearer ${RESEND_KEY}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({
      from: 'ARCHNEWS Weekly <onboarding@resend.dev>',
      to: email,
      subject: 'Welcome to ARCHNEWS Weekly',
      html: `
        <div style="max-width:580px;margin:0 auto;font-family:Georgia,serif;background:#f5f1ea;padding:40px 32px;">
          <div style="border-bottom:3px solid #c0300a;padding-bottom:16px;margin-bottom:24px;">
            <span style="font-family:Arial Black,sans-serif;font-size:32px;letter-spacing:4px;color:#0c0b09;">ARCH</span><span style="font-family:Arial Black,sans-serif;font-size:32px;letter-spacing:4px;color:#c0300a;">NEWS</span>
          </div>
          <h2 style="font-size:22px;color:#0c0b09;margin-bottom:12px;">You're in.</h2>
          <p style="font-size:15px;color:#46413b;line-height:1.8;margin-bottom:20px;">
            Every Monday morning, you'll get the 5 most important architecture stories of the week — curated by a licensed architect, not an algorithm.
          </p>
          <p style="font-size:15px;color:#46413b;line-height:1.8;margin-bottom:28px;">
            Until then, the live feed is always on at <a href="https://archnews.vercel.app" style="color:#c0300a;">archnews.vercel.app</a>
          </p>
          <div style="border-top:1px solid #cec8bb;padding-top:20px;font-size:11px;color:#908a82;font-family:monospace;">
            ARCHNEWS Weekly · architectonworks@gmail.com · <a href="https://archnews.vercel.app" style="color:#908a82;">archnews.vercel.app</a>
          </div>
        </div>
      `
    })
  });
}

module.exports = async function handler(req, res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'POST, OPTIONS');
  res.setHeader('Content-Type', 'application/json');
  if (req.method === 'OPTIONS') { res.status(200).end(); return; }
  if (req.method !== 'POST') { res.status(405).json({ error: 'Method not allowed' }); return; }

  try {
    const body = typeof req.body === 'string' ? JSON.parse(req.body) : req.body;
    const email = (body?.email || '').trim().toLowerCase();
    if (!email || !email.includes('@')) {
      return res.status(400).json({ error: 'Invalid email' });
    }

    // Check if already subscribed
    const exists = await redisCmd('SISMEMBER', 'archnews:subscribers', email);
    if (exists.result === 1) {
      return res.status(200).json({ success: true, message: 'Already subscribed' });
    }

    // Store email
    await redisCmd('SADD', 'archnews:subscribers', email);
    await redisCmd('INCR', 'archnews:count');

    // Send welcome email
    await sendWelcome(email);

    res.status(200).json({ success: true, message: 'Subscribed successfully' });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
};
