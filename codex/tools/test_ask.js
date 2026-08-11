// Tests for api/ask.js — the citation enforcement layer.
//
// The property under test: an answer that cannot be traced to a supplied
// passage must never reach the user as regulation, no matter how confident or
// well-formed the model's output is. Everything else in this tool is a
// convenience; this is the safety guarantee.
//
// Run:  node tools/test_ask.js

const path = require('path');
const handler = require(path.join(__dirname, '..', 'api', 'ask.js'));

let pass = 0, fail = 0;
function check(label, cond, detail) {
  if (cond) { pass++; console.log(`  ok    ${label}`); }
  else { fail++; console.log(`  FAIL  ${label}${detail ? `\n        ${detail}` : ''}`); }
}

function mockRes() {
  return {
    _code: null, _json: null,
    status(c) { this._code = c; return this; },
    json(j) { this._json = j; return this; }
  };
}

const PASSAGES = [{
  id: 'doc::2.1', doc_title: 'Sample Regs', number: '2.1',
  title: 'Base FSI', pdf_page: 4, printed_page: 2,
  text: 'The base FSI for a plot in Zone T1 shall be 1.8.'
}];

// Stub the Anthropic call so we control exactly what the "model" returns.
let fetchCalls = 0;
function stubModel(replyText) {
  fetchCalls = 0;
  global.fetch = async () => {
    fetchCalls++;
    return {
      ok: true,
      json: async () => ({ content: [{ type: 'text', text: replyText }] })
    };
  };
}

async function run() {
  // 1 — no key configured
  delete process.env.ANTHROPIC_API_KEY;
  let res = mockRes();
  await handler({ method: 'POST', body: { question: 'q', passages: PASSAGES } }, res);
  check('without an API key it fails closed, not open',
    res._code === 503 && res._json.ok === false,
    `got ${res._code} ${JSON.stringify(res._json)}`);

  process.env.ANTHROPIC_API_KEY = 'test-key';

  // 2 — no passages: must refuse locally, without consulting the model at all
  stubModel('irrelevant');
  res = mockRes();
  await handler({ method: 'POST', body: { question: 'what is the FSI?', passages: [] } }, res);
  check('empty retrieval refuses without calling the model',
    res._json.ok === true && res._json.refused === true && fetchCalls === 0,
    `refused=${res._json.refused} fetchCalls=${fetchCalls}`);

  // 3 — model refuses explicitly
  stubModel('REFUSE: the passages do not state a parking requirement.');
  res = mockRes();
  await handler({ method: 'POST', body: { question: 'parking?', passages: PASSAGES } }, res);
  check('explicit model refusal is surfaced as a refusal',
    res._json.refused === true && /do not state a parking/.test(res._json.answer),
    JSON.stringify(res._json));

  // 4 — properly cited answer passes through
  stubModel('The base FSI is 1.8. [[cite:doc::2.1]]');
  res = mockRes();
  await handler({ method: 'POST', body: { question: 'base FSI?', passages: PASSAGES } }, res);
  check('a properly cited answer is returned',
    res._json.refused === false && res._json.cited_ids.includes('doc::2.1'),
    JSON.stringify(res._json));
  check('the cited figure survives verbatim',
    res._json.answer.includes('1.8'), res._json.answer);

  // 5 — THE CRITICAL CASE: confident answer, no citation at all
  stubModel('The base FSI in Ahmedabad residential zones is 1.8 and may be increased to 2.7.');
  res = mockRes();
  await handler({ method: 'POST', body: { question: 'base FSI?', passages: PASSAGES } }, res);
  check('an uncited assertion is withheld, however plausible it reads',
    res._json.refused === true && res._json.withheld === true &&
    !/2\.7/.test(res._json.answer),
    JSON.stringify(res._json));

  // 6 — fabricated citation id pointing at a passage we never supplied
  stubModel('Parking must be 1 ECS per 80 sq m. [[cite:doc::9.9]]');
  res = mockRes();
  await handler({ method: 'POST', body: { question: 'parking?', passages: PASSAGES } }, res);
  check('a fabricated citation id does not validate an answer',
    res._json.refused === true && res._json.withheld === true,
    JSON.stringify(res._json));

  // 7 — mixed: one real citation, one fabricated
  stubModel('FSI is 1.8 [[cite:doc::2.1]] and parking is 2 ECS [[cite:doc::9.9]].');
  res = mockRes();
  await handler({ method: 'POST', body: { question: 'both?', passages: PASSAGES } }, res);
  check('fabricated ids are stripped while real ones are kept',
    res._json.refused === false &&
    res._json.cited_ids.length === 1 &&
    !res._json.answer.includes('doc::9.9'),
    JSON.stringify(res._json));

  // 8 — upstream failure must not degrade into an uncited answer
  global.fetch = async () => ({ ok: false, status: 529, text: async () => 'overloaded' });
  res = mockRes();
  await handler({ method: 'POST', body: { question: 'q', passages: PASSAGES } }, res);
  check('upstream errors fail closed', res._json.ok === false && res._code === 502,
    `${res._code} ${JSON.stringify(res._json)}`);

  // 9 — wrong method
  res = mockRes();
  await handler({ method: 'GET' }, res);
  check('rejects non-POST', res._code === 405);

  console.log(`\n${pass} passed, ${fail} failed`);
  process.exit(fail ? 1 : 0);
}

run();
