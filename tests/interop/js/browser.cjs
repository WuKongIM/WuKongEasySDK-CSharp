const https = require('node:https');
const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');
const { chromium } = require('playwright');
const esbuild = require('esbuild');
const emit = value => process.stdout.write(`${JSON.stringify(value)}\n`);
let stage = 'bundle';

(async () => {
  const bundle = await esbuild.build({ entryPoints: [path.join(__dirname, 'browser-entry.js')],
    bundle: true, write: false, platform: 'browser', format: 'iife',
    alias: { easyjssdk: process.env.INTEROP_JS_ENTRY }, logLevel: 'silent' });
  const server = https.createServer({ cert: fs.readFileSync(process.env.INTEROP_CERT),
    key: fs.readFileSync(process.env.INTEROP_KEY) }, (request, response) => {
    if (request.url === '/bundle.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end(bundle.outputFiles[0].contents);
    } else if (request.url === '/') {
      response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
      response.end('<!doctype html><html lang="en"><title>SDK WSS Interoperability</title>' +
        '<h1>C# / Browser WSS Interoperability</h1><p id="state">Ready</p>' +
        '<script src="/bundle.js"></script></html>');
    } else { response.writeHead(404); response.end(); }
  });
  let browser;
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    stage = 'browser launch';
    browser = await chromium.launch({ headless: true,
      // Chromium's process sandbox needs a non-root host configuration in production.
      // The owned CI fixture has no untrusted pages; TLS verification stays enabled.
      args: ['--no-sandbox'], env: { ...process.env, HOME: process.env.INTEROP_BROWSER_HOME } });
    const context = await browser.newContext({ ignoreHTTPSErrors: false, serviceWorkers: 'block' });
    const page = await context.newPage();
    await page.exposeFunction('reportInterop', emit);
    stage = 'HTTPS page';
    const response = await page.goto(`https://127.0.0.1:${server.address().port}/`);
    if (response.status() !== 200 || !await page.evaluate(() => window.isSecureContext)) {
      throw new Error('HTTPS page failed normal certificate validation');
    }
    emit({ kind: 'runtime', browser: browser.version(), playwright: require('playwright/package.json').version,
      secureContext: true, certificateValidation: true });
    await page.evaluate(config => window.startInterop(config), {
      url: process.env.INTEROP_URL, uid: process.env.INTEROP_UID, token: process.env.INTEROP_TOKEN,
    });
    stage = 'SDK commands';
    for await (const line of readline.createInterface({ input: process.stdin })) {
      const command = JSON.parse(line);
      emit(await page.evaluate(command => window.interopCommand(command), command));
      if (command.op === 'exit') break;
    }
  } finally {
    if (browser) await browser.close();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => {
  emit({ kind: 'fixtureFailure', category: error.name, stage,
    networkCode: error.message?.match(/net::ERR_[A-Z_]+/)?.[0] || null });
  process.exitCode = 1;
});
