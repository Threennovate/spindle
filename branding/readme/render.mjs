// Renders the README header in light and dark to out/<name>-<theme>.png: its .frame at 2x (1280 wide, as tall as the
// page makes it), with transparent corners.
// Playwright comes from the web app: cd src/web && node ../../branding/readme/render.mjs
import { createRequire } from 'node:module';
import { mkdirSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, 'out');
mkdirSync(out, { recursive: true });
const pages = readdirSync(here).filter(f => f.endsWith('.html'));

// Playwright is resolved from the folder the script runs in (src/web), not from here.
const { chromium } = createRequire(join(process.cwd(), 'package.json'))('playwright');
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 2 });
for (const file of pages) {
  for (const theme of ['light', 'dark']) {
    await page.goto(`${pathToFileURL(join(here, file)).href}?theme=${theme}`);
    await page.evaluate(() => document.fonts.ready);
    const name = `${file.replace(/\.html$/, '')}-${theme}.png`;
    await page.locator('.frame').screenshot({ path: join(out, name), omitBackground: true });
    console.log(name);
  }
}
await browser.close();
