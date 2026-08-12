// Smoke test for index.html — boots the real page in a real browser against a
// real corpus directory and asserts the trust-critical surfaces render.
//
// Run:  node tools/test_ui.js
// Requires: npm install --no-save playwright

const http = require('http');
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const ROOT = path.join(__dirname, '..');
const TYPES = { '.html': 'text/html', '.json': 'application/json', '.js': 'text/javascript' };

let pass = 0, fail = 0;
const check = (label, cond, detail) => {
  if (cond) { pass++; console.log(`  ok    ${label}`); }
  else { fail++; console.log(`  FAIL  ${label}${detail ? `\n        ${detail}` : ''}`); }
};

function serve(port) {
  return new Promise(resolve => {
    const srv = http.createServer((req, res) => {
      const url = decodeURIComponent(req.url.split('?')[0]);
      const file = path.join(ROOT, url === '/' ? 'index.html' : url);
      if (!file.startsWith(ROOT) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
        res.writeHead(404); return res.end('not found');
      }
      res.writeHead(200, { 'Content-Type': TYPES[path.extname(file)] || 'text/plain' });
      res.end(fs.readFileSync(file));
    }).listen(port, () => resolve(srv));
  });
}

// The shipped corpus contains only real documents. The fixture used to exercise
// the clause browser is built and ingested here, then removed again, so test
// data can never reach a deployment.
const { execFileSync } = require('child_process');
const FIXTURE_ID = 'fixture-uitest';
const FIXTURE_AMD_ID = 'fixture-uitest-amd';
function setupFixture() {
  execFileSync('python3', [path.join(__dirname, 'fixtures', 'make_fixture.py')], { cwd: ROOT });
  execFileSync('python3', [path.join(__dirname, 'ingest.py'),
    '--doc', FIXTURE_ID,
    '--pdf', path.join(__dirname, 'fixtures', 'sample-regulations-FIXTURE.pdf'),
    '--title', 'FIXTURE — Sample Regulations (NOT LAW)',
    '--authority', 'Synthetic test data', '--edition', 'fixture'], { cwd: ROOT });
  // A synthetic amendment, so amendment handling is covered without depending
  // on copyright material being present in the working tree.
  execFileSync('python3', [path.join(__dirname, 'ingest_amendment.py'),
    '--doc', FIXTURE_AMD_ID,
    '--pdf', path.join(__dirname, 'fixtures', 'sample-amendment-FIXTURE.pdf'),
    '--amends', FIXTURE_ID,
    '--authority', 'Synthetic test data'], { cwd: ROOT });
}
function teardownFixture() {
  for (const id of [FIXTURE_ID, FIXTURE_AMD_ID]) {
    fs.rmSync(path.join(ROOT, 'corpus', 'docs', id), { recursive: true, force: true });
  }
  for (const f of ['sample-regulations-FIXTURE.pdf', 'sample-amendment-FIXTURE.pdf']) {
    fs.rmSync(path.join(__dirname, 'fixtures', f), { force: true });
  }
  execFileSync('python3', [path.join(__dirname, 'ingest.py'), '--reindex'], { cwd: ROOT });
}

(async () => {
  const PORT = 8099;
  setupFixture();
  process.on('exit', teardownFixture);
  const srv = await serve(PORT);
  // Use the pre-installed browser rather than downloading one; the bundled
  // Playwright build number may not match what is on disk.
  const preinstalled = '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';
  const browser = await chromium.launch(
    fs.existsSync(preinstalled) ? { executablePath: preinstalled } : {});
  const page = await browser.newPage();

  // Only real script exceptions count. This sandbox has no outbound network, so
  // webfonts and favicon requests fail; those are environment noise, not bugs.
  const errors = [];
  page.on('pageerror', e => errors.push('pageerror: ' + e.message));
  page.on('console', m => {
    if (m.type() !== 'error') return;
    const t = m.text();
    if (/Failed to load resource/i.test(t)) return;
    errors.push(t);
  });

  await page.goto(`http://127.0.0.1:${PORT}/index.html`, { waitUntil: 'networkidle' });

  check('page loads with no JS errors', errors.length === 0, errors.join(' | '));

  // Fixture corpus is present, so the warning strip must be visible.
  check('fixture warning strip is shown when fixture data is loaded',
    await page.isVisible('#fixtureStrip'));

  // Real documents may also be loaded and would open by default, so select the
  // fixture explicitly before asserting on its clauses.
  await page.selectOption('#docSelect', FIXTURE_ID);
  await page.waitForTimeout(300);

  // Tree renders clauses from the ingested corpus.
  const nodes = await page.$$eval('.tnode .tw', els => els.map(e => e.textContent.trim()));
  check('clause tree renders ingested clauses', nodes.includes('2.1'), nodes.join(','));

  // Section view shows verbatim text and a page citation.
  await page.click('.tnode:has-text("Base Floor Space Index")');
  const body = await page.textContent('.sec-body');
  check('clause body shows verbatim text', body.includes('1.8'), body.slice(0, 120));

  const cite = await page.textContent('.cite');
  check('citation bar names the printed page', /p\. 2/.test(cite), cite.replace(/\s+/g, ' ').slice(0, 200));
  check('citation bar names the PDF page', /PDF p\. 4/.test(cite));
  check('citation bar carries source hash', /Source SHA-256/.test(cite));

  const note = await page.textContent('.verbatim-note');
  check('reader states the text is verbatim, not paraphrased', /verbatim/i.test(note));

  // Search is deterministic and finds the clause.
  await page.click('.htab[data-pane="search"]');
  await page.fill('#q', 'chargeable FSI');
  await page.click('#qBtn');
  await page.waitForSelector('.res', { timeout: 5000 });
  const hits = await page.$$eval('.res-t', e => e.map(x => x.textContent));
  check('search finds the governing clause', hits.some(h => /Chargeable/i.test(h)), hits.join(','));
  const meta = await page.textContent('#searchMeta');
  check('search states it is deterministic', /deterministic/i.test(meta), meta);

  // Clause-number search jumps straight to that clause.
  await page.fill('#q', '3.1');
  await page.click('#qBtn');
  await page.waitForTimeout(400);
  const first = await page.textContent('.res .res-n');
  check('exact clause-number search ranks that clause first', first.trim() === '3.1', first);

  // Feasibility must refuse, not guess, with no rules loaded.
  await page.click('.htab[data-pane="feas"]');
  await page.waitForSelector('.out-row', { timeout: 5000 });
  const outs = await page.$$eval('.out-val', e => e.map(x => x.textContent.trim()));
  check('every feasibility output refuses when no rules are loaded',
    outs.length > 0 && outs.every(o => /Not derivable/i.test(o)), outs.join(','));
  const feasText = (await page.textContent('#feasOut')).replace(/\s+/g, ' ');
  check('feasibility explains that not-derivable is not zero',
    /never that the requirement is zero/i.test(feasText), feasText.slice(0, 200));

  // ---- the other half: with rules loaded, values appear WITH citations, and
  // a rule lacking a citation is rejected rather than quietly applied.
  const rulesPath = path.join(ROOT, 'corpus', 'rules.json');
  const rulesBackup = fs.readFileSync(rulesPath, 'utf8');
  try {
    fs.writeFileSync(rulesPath, JSON.stringify({
      rules: [
        {
          output: 'base_fsi', value: '1.8', unit: '',
          when: { city: 'ahmedabad', zone: 'R1', min_road_width: 9 },
          citation: { doc_id: FIXTURE_ID, number: '2.1', page_label: 'p. 2',
                      quote: 'The base FSI for a plot in Zone T1 shall be 1.8' },
          verified: true, note: 'Fixture rule for testing.'
        },
        { // deliberately uncited — must never be applied
          output: 'front_margin', value: '3.0', unit: 'm',
          when: { city: 'ahmedabad' }, verified: true
        },
        { // unverified but cited — must apply, with a visible warning
          output: 'side_margin', value: '1.5', unit: 'm',
          when: { city: 'ahmedabad' },
          citation: { doc_id: FIXTURE_ID, number: '3.2', page_label: 'p. 3' },
          verified: false
        }
      ]
    }, null, 2));

    await page.reload({ waitUntil: 'networkidle' });
    await page.selectOption('#docSelect', FIXTURE_ID);
    await page.click('.htab[data-pane="feas"]');
    await page.waitForSelector('.out-row', { timeout: 5000 });
    const rows = await page.$$eval('.out-row', els => els.map(e => e.textContent.replace(/\s+/g, ' ')));

    const fsiRow = rows.find(r => /BASE FSI/i.test(r)) || '';
    check('a cited rule produces its value', /1\.8/.test(fsiRow), fsiRow.slice(0, 160));
    check('the produced value carries its clause and page',
      /§2\.1/.test(fsiRow) && /p\. 2/.test(fsiRow), fsiRow.slice(0, 160));

    const frontRow = rows.find(r => /FRONT MARGIN/i.test(r)) || '';
    check('an uncited rule is rejected, not applied',
      /Not derivable/i.test(frontRow) && !/3\.0/.test(frontRow), frontRow.slice(0, 200));
    check('the rejection says a rule was discarded for missing a citation',
      /rejected for missing a citation/i.test(frontRow), frontRow.slice(0, 200));

    const sideRow = rows.find(r => /SIDE MARGIN/i.test(r)) || '';
    check('an unverified rule applies but is flagged',
      /1\.5/.test(sideRow) && /UNVERIFIED/i.test(sideRow), sideRow.slice(0, 200));

    // The citation chip must navigate to the actual clause.
    await page.click('.out-row:has-text("BASE FSI") .cite-chip');
    await page.waitForSelector('.sec-title', { timeout: 5000 });
    const landed = await page.textContent('.sec-title');
    check('clicking a citation opens the cited clause',
      /Base Floor Space Index/i.test(landed), landed);
  } finally {
    fs.writeFileSync(rulesPath, rulesBackup);
  }

  await page.reload({ waitUntil: 'networkidle' });

  // Ask must refuse locally when retrieval finds nothing — no model call at all.
  await page.click('.htab[data-pane="ask"]');
  await page.fill('#askInput', 'xyzzy zzzqqq plugh');
  await page.click('#askBtn');
  await page.waitForSelector('.refusal', { timeout: 8000 });
  const refusal = await page.textContent('.refusal');
  check('unanswerable question is refused, not answered',
    /Not answerable from the loaded corpus/i.test(refusal), refusal.slice(0, 160));

  // Fixture content must never be used to answer once a genuine document is
  // loaded. That condition only exists if a real document is present locally,
  // so the check adapts rather than assuming one is.
  const hasReal = await page.evaluate(() => state.docs.some(d => !d.fixture));
  const realDir = path.join(ROOT, 'corpus', 'docs', 'nbc-2016-amd1-2021');
  const stashed = path.join(ROOT, 'corpus', '_stashed-nbc');

  if (hasReal) {
    await page.fill('#askInput', 'chargeable FSI');
    await page.click('#askBtn');
    await page.waitForTimeout(1200);
    const withReal = await page.textContent('.ask-log');
    check('fixture content is excluded from answers once a real document is loaded',
      /Not answerable from the loaded corpus/i.test(withReal), withReal.slice(-220));
  } else {
    console.log('  skip  no real document present, cannot test fixture exclusion');
  }

  // When the answer service is unreachable, the tool must degrade to showing the
  // retrieved clauses — never to answering from the model's own memory. This
  // needs retrieval to return something, so any real document is moved aside.
  const needStash = hasReal && fs.existsSync(realDir);
  try {
    if (needStash) {
      fs.renameSync(realDir, stashed);
      execFileSync('python3', [path.join(__dirname, 'ingest.py'), '--reindex'], { cwd: ROOT });
      await page.reload({ waitUntil: 'networkidle' });
    }
    await page.click('.htab[data-pane="ask"]');
    await page.fill('#askInput', 'chargeable FSI');
    await page.click('#askBtn');
    await page.waitForSelector('.sources', { timeout: 8000 });
    const degraded = await page.textContent('.ask-log');
    check('unreachable answer service degrades to source clauses, not a guess',
      /No answer produced/i.test(degraded) && /Source clauses/i.test(degraded));
    check('degraded mode still shows the real clause text',
      /Chargeable/i.test(degraded));
  } finally {
    if (fs.existsSync(stashed)) {
      fs.renameSync(stashed, realDir);
      execFileSync('python3', [path.join(__dirname, 'ingest.py'), '--reindex'], { cwd: ROOT });
    }
    await page.reload({ waitUntil: 'networkidle' });
  }

  // ---- amendments (synthetic fixture) ------------------------------------
  await page.click('.htab[data-pane="amd"]');
  await page.waitForSelector('.amd', { timeout: 5000 });
  // Scope the count to the fixture's own block: a real notification may also be
  // present locally and would otherwise inflate the total.
  const fixtureEdits = await page.$$eval('.amd-doc', (blocks, id) => {
    const b = blocks.find(x => x.textContent.includes(id));
    return b ? b.querySelectorAll('.amd').length : -1;
  }, FIXTURE_ID);   // the block names its target document
  check('amendments view lists every parsed edit', fixtureEdits === 4,
    `got ${fixtureEdits}`);

  const amdText = (await page.textContent('#amdOut')).replace(/\s+/g, ' ');
  check('an amendment shows the superseded value', /1\.8/.test(amdText), amdText.slice(0, 200));
  check('an amendment shows the replacement value', /2\.25/.test(amdText));
  check('an amendment names the base document page',
    /base p\. 2/.test(amdText), amdText.slice(0, 200));
  check('a deletion shows the removed words',
    /subject to prior approval/.test(amdText));

  // ---- amendment overlay on the base clause it changes --------------------
  await page.click('.htab[data-pane="browse"]');
  await page.selectOption('#docSelect', FIXTURE_ID);
  await page.click('.tnode:has-text("Base Floor Space Index")');
  await page.waitForSelector('.amd-alert', { timeout: 5000 });
  const alert = (await page.textContent('.amd-alert')).replace(/\s+/g, ' ');
  check('an amended clause warns before the reader reads the stale text',
    /has been amended/i.test(alert), alert.slice(0, 160));
  check('the overlay shows what the value was', /1\.8/.test(alert));
  check('the overlay shows what the value is now', /2\.25/.test(alert));

  const staleBody = await page.textContent('.sec-body');
  check('the base clause text is still shown verbatim, not silently rewritten',
    /1\.8/.test(staleBody), staleBody.slice(0, 120));

  // A clause with no amendment against it must NOT show the warning.
  // (3.1 is amended via "clause 3.1 (b)", so use one the notification never touches.)
  await page.click('.tnode:has-text("Short Title")');
  await page.waitForTimeout(200);
  check('an unamended clause shows no amendment warning',
    (await page.$$('.amd-alert')).length === 0);

  // ---- the real BIS notification, only if it is present locally -----------
  // It is copyright BIS and excluded from version control, so a fresh clone
  // skips this block rather than failing.
  const realAmd = path.join(ROOT, 'corpus', 'docs', 'nbc-2016-amd1-2021', 'amendments.json');
  if (fs.existsSync(realAmd)) {
    const items = JSON.parse(fs.readFileSync(realAmd, 'utf8')).items;
    check('real BIS notification parsed 19 edits', items.length === 19, `got ${items.length}`);
    check('real notification captures the 115 mm to 90 mm change',
      items.some(i => i.old_text === '115 mm' && i.new_text === '90 mm'));
  } else {
    console.log('  skip  real BIS notification not present locally (copyright, not committed)');
  }

  check('no JS errors after full interaction', errors.length === 0, errors.join(' | '));

  await browser.close();
  srv.close();
  console.log(`\n${pass} passed, ${fail} failed`);
  process.exit(fail ? 1 : 0);
})();
