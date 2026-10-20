// Draws the Google Play store listing's 512 × 512 icon from branding/ into android/play/. Run it again after the brand
// changes: node android/scripts/play-graphics.mjs. The feature graphic is drawn with the screenshots, by
// node branding/store/store-art.mjs android.
// (Chromium from the web app's Playwright draws the PNGs, as in branding/build.mjs.)
import { chromium } from '../../src/web/node_modules/playwright/index.mjs'
import { readFileSync } from 'node:fs'

const repo = new URL('../..', import.meta.url).pathname.replace(/\/$/, '')
const out = `${repo}/android/play`
const brand = (name) => readFileSync(`${repo}/branding/${name}`)

// Play rounds the icon's corners itself, so it is the full-bleed iOS icon, as the launcher icon is.
const icon = `<img src="data:image/png;base64,${brand('spindle-app-icon-1024.png').toString('base64')}" style="width:512px;height:512px">`

const browser = await chromium.launch()
const page = await browser.newPage({ deviceScaleFactor: 1 })
for (const [name, html, width, height] of [['icon-512.png', icon, 512, 512]]) {
  await page.setViewportSize({ width, height })
  await page.setContent(`<body style="margin:0">${html}</body>`)
  await page.screenshot({ path: `${out}/${name}`, omitBackground: false })
  console.log(`wrote android/play/${name}`)
}
await browser.close()
