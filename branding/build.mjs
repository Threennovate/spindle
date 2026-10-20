// Draws every Spindle logo file and icon from the one design below.
// Run from this folder: npm install --no-save opentype.js@1.3.4 && node build.mjs
// (Chromium from the web app's Playwright draws the PNGs.)
import { chromium } from '../src/web/node_modules/playwright/index.mjs'
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import opentype from 'opentype.js'

const repo = new URL('..', import.meta.url).pathname.replace(/\/$/, '')
const brandDir = `${repo}/branding`
const publicDir = `${repo}/src/web/public`
mkdirSync(brandDir, { recursive: true })

const colors = { midnight: '#0E1322', track: '#E9ECF2', marigold: '#F7BE2B', marigoldDeep: '#E79D00' }

// The spindle on a 64 × 64 grid: a shaft at the top and bottom and bars of time wound between them. The longest bar,
// in marigold, is your hour. The compact version keeps three bars, for 24 px and smaller. Same as components/logo.tsx.
const fullBars = [[2.3, 4.6, 8.6], [14.1, 21], [21.9, 33], [29.7, 41, 4.6, true], [37.5, 33], [45.3, 21], [53.1, 4.6, 8.6]]
const compactBars = [[4.3, 7, 10], [17.9, 26, 7], [28.5, 44, 7, true], [39.1, 26, 7], [49.7, 7, 10]]
const bars = (fg, accent, compact = false) =>
  (compact ? compactBars : fullBars)
    .map(([y, w, h = 4.6, accented]) => `<rect x="${32 - w / 2}" y="${y}" width="${w}" height="${h}" rx="${Math.min(w, h) / 2}" fill="${accented ? accent : fg}"/>`)
    .join('')
const scaled = (scale, body) => `<g transform="translate(${32 - 32 * scale} ${32 - 32 * scale}) scale(${scale})">${body}</g>`

/** The rounded midnight tile, for the favicon and app icons, where the mark needs a ground of its own. */
const tile = (compact = false) => `
  <rect width="64" height="64" rx="15" fill="${colors.midnight}"/>
  <rect x="0.5" y="0.5" width="63" height="63" rx="14.5" fill="none" stroke="#FFFFFF" stroke-opacity="0.1"/>
  ${scaled(compact ? 0.8 : 0.72, bars(colors.track, colors.marigold, compact))}`

const svg = (viewBox, body, title = 'Spindle') =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}" role="img" aria-label="${title}"><title>${title}</title>${body}\n</svg>\n`

/** Full-bleed square for iOS and maskable icons: the system rounds the corners; the content keeps to the safe area. */
const fullBleed = (scale) => svg('0 0 64 64', `<rect width="64" height="64" fill="${colors.midnight}"/>${scaled(scale, bars(colors.track, colors.marigold))}`)

// Wordmark: Fraunces at weight 450, SOFT 100, WONK 0 and optical size 72, kept as a static instance in fonts/
// (opentype.js can't set variable axes). Made from the variable font with fontTools:
//   instancer.instantiateVariableFont(font, { wght: 450, SOFT: 100, WONK: 0, opsz: 72 })
const font = opentype.parse(readFileSync(`${brandDir}/fonts/Fraunces-Spindle.ttf`).buffer)
const size = 40
const tracking = -0.01 * size
function word(text, x, y) {
  let cursor = x
  const parts = []
  for (const glyph of font.stringToGlyphs(text)) {
    parts.push(glyph.getPath(cursor, y, size).toPathData(2))
    cursor += (glyph.advanceWidth / font.unitsPerEm) * size + tracking
  }
  return { d: parts.join(''), end: cursor }
}
// Proportions as approved in the wordmark preview: the mark's bars stand 1.1 × the type size tall, centred on the
// type's line box and nudged down 0.04 em; the gap to the name is 0.175 em. The bars span 11.5–52.5 and 2.3–61.7 on
// the mark's grid, so the lockup crops the empty space around them.
const unit = (1.1 * size) / 59.4
const markTop = 2.3 * unit
const ascent = font.tables.hhea.ascender / font.unitsPerEm
const descent = -font.tables.hhea.descender / font.unitsPerEm
const lineCenter = size / 2 + 0.04 * size // the mark's centre, measured from the top of the type's line box
const baselineInLine = (size - (ascent + descent) * size) / 2 + ascent * size
const top = lineCenter - 32 * unit // where the mark's grid starts, from the top of the line box
const shift = markTop + top // move everything up so the top bar touches the top of the drawing
const height = 59.4 * unit
const name = word('Spindle', 41 * unit + 0.175 * size, baselineInLine - shift)
const width = Math.ceil(name.end - tracking + 1)
// The descender of the p may reach below the mark, so the drawing is as tall as whichever reaches further.
const textBottom = baselineInLine - shift + descent * size
const logo = (fg, accent) =>
  svg(`0 0 ${width} ${Math.ceil(Math.max(height, textBottom))}`, `<g transform="translate(${-11.5 * unit} ${-markTop}) scale(${unit})">${bars(fg, accent)}</g><path fill="${fg}" d="${name.d}"/>`)

const tileSvg = svg('0 0 64 64', tile())
const faviconSvg = svg('0 0 64 64', tile(true))
const files = {
  [`${brandDir}/spindle-mark.svg`]: svg('0 0 64 64', bars(colors.midnight, colors.marigoldDeep)),
  [`${brandDir}/spindle-mark-dark.svg`]: svg('0 0 64 64', bars(colors.track, colors.marigold)),
  [`${brandDir}/spindle-mark-compact.svg`]: svg('0 0 64 64', bars(colors.midnight, colors.marigoldDeep, true)),
  [`${brandDir}/spindle-tile.svg`]: tileSvg,
  [`${brandDir}/spindle-logo-light.svg`]: logo(colors.midnight, colors.marigoldDeep),
  [`${brandDir}/spindle-logo-dark.svg`]: logo(colors.track, colors.marigold),
  [`${brandDir}/spindle-app-icon.svg`]: fullBleed(0.68),
  [`${publicDir}/favicon.svg`]: faviconSvg,
}
// The Apple apps draw the same vectors; the asset catalog picks the dark logo in dark mode.
const appleAssets = `${repo}/apple/Shared/Resources/Shared.xcassets`
files[`${appleAssets}/LogoMark.imageset/spindle-mark.svg`] = tileSvg
files[`${appleAssets}/Logo.imageset/spindle-logo-light.svg`] = files[`${brandDir}/spindle-logo-light.svg`]
files[`${appleAssets}/Logo.imageset/spindle-logo-dark.svg`] = files[`${brandDir}/spindle-logo-dark.svg`]
// The Mac's menu bar item: the compact mark, cropped to its bars, in one colour. macOS draws it as a template image,
// in the menu bar's own colour, as it does other apps' icons there.
// Its width and height give its size in points, 16 tall like the system's own menu bar symbols.
files[`${repo}/apple/Mac/Resources/Assets.xcassets/MenuBarMark.imageset/menubar-mark.svg`] = svg('10 4.3 44 55.4', bars('#000000', '#000000', true))
  .replace('<svg ', '<svg width="12.7" height="16" ')
for (const [path, text] of Object.entries(files)) {
  writeFileSync(path, text)
}

// Pixel versions, drawn by Chromium from the same SVG.
const browser = await chromium.launch()
async function png(svgText, pixels, out) {
  const page = await browser.newPage({ viewport: { width: pixels, height: pixels }, deviceScaleFactor: 1 })
  await page.setContent(`<html><body style="margin:0;background:transparent">${svgText.replace('<svg ', `<svg width="${pixels}" height="${pixels}" `)}</body></html>`)
  const buffer = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: pixels, height: pixels } })
  if (out) writeFileSync(out, buffer)
  await page.close()
  return buffer
}
await png(fullBleed(0.68), 1024, `${brandDir}/spindle-app-icon-1024.png`)
// The iPhone app's icon: the same square, which iOS rounds.
mkdirSync(`${repo}/apple/iOS/Resources/Assets.xcassets/AppIcon.appiconset`, { recursive: true })
copyFileSync(`${brandDir}/spindle-app-icon-1024.png`, `${repo}/apple/iOS/Resources/Assets.xcassets/AppIcon.appiconset/AppIcon.png`)
await png(tileSvg, 1024, `${brandDir}/spindle-tile-1024.png`)
await png(fullBleed(0.68), 180, `${publicDir}/apple-touch-icon.png`)
await png(tileSvg, 192, `${publicDir}/icon-192.png`)
await png(tileSvg, 512, `${publicDir}/icon-512.png`)
await png(fullBleed(0.56), 512, `${publicDir}/icon-maskable-512.png`)
// Favicons at 16–48 px use the compact mark, which stays readable there.
const small = await Promise.all([16, 32, 48].map((s) => png(faviconSvg, s)))

// The Mac app icon on Apple's macOS grid: an 824 px rounded square with a soft shadow, on a 1024 px canvas.
const macIcon = svg(
  '0 0 1024 1024',
  `<filter id="sp-shadow" x="-10%" y="-10%" width="120%" height="130%"><feDropShadow dx="0" dy="10" stdDeviation="12" flood-color="#000" flood-opacity="0.3"/></filter>
  <rect x="100" y="100" width="824" height="824" rx="185" fill="${colors.midnight}" filter="url(#sp-shadow)"/>
  <rect x="101" y="101" width="822" height="822" rx="184" fill="none" stroke="#FFFFFF" stroke-opacity="0.08" stroke-width="2"/>
  <g transform="translate(224 224) scale(9)">${bars(colors.track, colors.marigold)}</g>`,
)
writeFileSync(`${brandDir}/spindle-mac-icon.svg`, macIcon)
const iconSet = `${repo}/apple/Mac/Resources/Assets.xcassets/AppIcon.appiconset`
mkdirSync(iconSet, { recursive: true })
const macSizes = [16, 32, 128, 256, 512]
const images = []
for (const size of macSizes) {
  for (const scale of [1, 2]) {
    const name = `icon_${size}x${size}${scale === 2 ? '@2x' : ''}.png`
    await png(macIcon, size * scale, `${iconSet}/${name}`)
    images.push({ size: `${size}x${size}`, idiom: 'mac', filename: name, scale: `${scale}x` })
  }
}
writeFileSync(`${iconSet}/Contents.json`, JSON.stringify({ images, info: { version: 1, author: 'branding/build.mjs' } }, null, 2) + '\n')
await browser.close()

// favicon.ico: an icon directory holding PNG images, which every current browser reads.
const header = Buffer.alloc(6 + 16 * small.length)
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(small.length, 4)
let offset = header.length
small.forEach((image, i) => {
  const s = [16, 32, 48][i]
  const entry = 6 + 16 * i
  header.writeUInt8(s, entry); header.writeUInt8(s, entry + 1); header.writeUInt8(0, entry + 2); header.writeUInt8(0, entry + 3)
  header.writeUInt16LE(1, entry + 4); header.writeUInt16LE(32, entry + 6)
  header.writeUInt32LE(image.length, entry + 8); header.writeUInt32LE(offset, entry + 12)
  offset += image.length
})
writeFileSync(`${publicDir}/favicon.ico`, Buffer.concat([header, ...small]))
console.log('logo width', width)
