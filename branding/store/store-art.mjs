// Draws the branded store pictures of the iPhone and Android apps from their raw screenshots:
//   node branding/store/store-art.mjs ios       apple/appstore/branded/: five screenshots (6.9" and, in 6.3/, the
//                                               medium size App Store Connect asks for), header.png, header-universal.png,
//                                               and with --video header.mp4 (needs ffmpeg)
//   node branding/store/store-art.mjs android   android/play/branded/: five 9:16 screenshots, and
//                                               android/play/feature-graphic.png
// The five screenshots are cut from one wide panorama, so the project threads behind the phones (threads.js, run in the
// page) go on from one picture to the next as the stores show them side by side. The header and feature graphic are
// one moment of the same story: three threads running into one entry's split bar and out again.
// Chromium from the web app's Playwright draws the PNGs, as in branding/build.mjs.
import { chromium } from '../../src/web/node_modules/playwright/index.mjs'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'

const repo = new URL('../..', import.meta.url).pathname.replace(/\/$/, '')
const file = (path) => `file://${path}`
const geist = file(`${repo}/src/web/node_modules/@fontsource-variable/geist/files/geist-latin-wght-normal.woff2`)
const logoSvg = readFileSync(`${repo}/branding/spindle-logo-light.svg`, 'utf8')
const icon = file(`${repo}/branding/spindle-app-icon-1024.png`)
const colors = { blue: '#2D6BE0', pink: '#D0477F', green: '#2F9E44', marigold: '#F0B21B', ink: '#0E1322' }
const { blue, pink, green } = colors // as in the demo data (android/scripts/play-demo-data.py)
const names = ['1-timer', '2-calendar', '3-locked', '4-reports', '5-projects']

const captions = (third) => [
  { title: 'Time tracking for parallel work', sub: 'One entry can split between projects.' },
  { title: 'Your day, block by block', sub: 'A split entry shows each project’s share.' },
  third,
  { title: 'Each part counts on its own project', sub: 'Summary, detailed and weekly reports.' },
  { title: 'Works offline', sub: 'Changes sync with the Mac and the web.' },
]

// What differs between the stores. Sizes are in each picture's pixels; the copy and the threads scale with k.
const platforms = {
  ios: {
    W: 1320,
    H: 2868,
    raw: 'apple/appstore/screenshots',
    rawSize: [1320, 2868],
    shots: { '1-timer': '1-timer', '2-calendar': '2-calendar', '4-reports': '4-reports', '5-projects': '5-projects' },
    out: 'apple/appstore/branded',
    files: { '3-locked': '3-lock-screen' },
    phone: { screenW: 960, top: 640, bezel: 20, radius: 144, screenRadius: 124, cutout: 'island' },
    callout: { left: 56, width: 1060, top: 1760, font: "-apple-system, 'SF Pro Text', system-ui, sans-serif" },
    third: { title: 'The timer on your Lock Screen', sub: 'See it running, and stop it from there.' },
    locked: iosLockScreen,
    medium: [1206, 2622], // App Store Connect's "iPhone with Dynamic Island (medium display)"
    headers: [['header', 3840, 1646], ['header-universal', 5244, 2950]],
    video: 'header', // with --video: header.mp4 (needs ffmpeg)
  },
  android: {
    W: 1080,
    H: 1920,
    raw: 'android/play/screenshots',
    rawSize: [1080, 2400],
    shots: { '1-timer': '1-timer', '2-calendar': '2-calendar', '3-locked': '3-split', '4-reports': '4-reports', '5-projects': '5-projects' },
    out: 'android/play/branded',
    files: { '3-locked': '3-split' },
    // Taller than the picture: the phone runs off the bottom edge, so its screen stays large enough to read.
    phone: { screenW: 760, top: 560, bezel: 16, radius: 92, screenRadius: 78, cutout: 'hole' },
    callout: { left: 40, width: 880, top: 1440, font: "Roboto, 'Google Sans', system-ui, sans-serif" },
    // The split entry in the editor, with the bar whose divider sets the shares.
    third: { title: 'One entry, two projects', sub: 'Drag the divider to set each share.' },
    headers: [['../feature-graphic', 1024, 500]],
  },
}

function screenshots(p) {
  const { W, H, phone: ph } = p
  const k = W / 1320
  const frames = 5
  const screenH = Math.round((ph.screenW * p.rawSize[1]) / p.rawSize[0])
  const phoneLeft = (W - ph.screenW - 2 * ph.bezel) / 2
  const screenTop = ph.top + ph.bezel
  const ctx = { ...p, k, screenH, phoneLeft, screenTop }
  const shot = (name) => `<img src="${file(`${repo}/${p.raw}/${name}.png`)}" style="width:${ph.screenW}px;height:${screenH}px;display:block">`
  const phone = (screen) => `
    <div class="phone" style="left:${phoneLeft}px;top:${ph.top}px">
      <div class="screen">${screen}</div>
      <div class="${ph.cutout}"></div>
    </div>`
  const callout = `
    <div class="callout">
      <div class="callout-row"><div class="callout-title">Retry-After handling and checkout tests</div><div class="callout-duration">1:20:00</div></div>
      <div class="callout-meta"><span class="dot" style="background:${blue}"></span><span style="color:${blue}">Acme API</span>&nbsp;55% <span class="sep">·</span> <span class="dot" style="background:${pink}"></span><span style="color:${pink}">Acme Web</span>&nbsp;45%</div>
      <div class="callout-bar"><span style="flex:55;background:${blue}"></span><span style="flex:45;background:${pink}"></span></div>
    </div>`
  const logo = logoSvg.replace('<svg ', `<svg width="${300 * k}" `)
  const pictures = captions(p.third).map((caption, i) => ({
    ...caption,
    dark: i === 2,
    top: i === 0 ? `<div class="logo">${logo}</div>` : '',
    body: i === 2 && p.locked ? phone(p.locked(ctx)) : phone(shot(p.shots[names[i]])) + (i === 0 ? callout : ''),
  }))

  return `<!doctype html>
<html><head><meta charset="utf-8"><style>
  @font-face { font-family: Geist; src: url('${geist}') format('woff2'); font-weight: 100 900; }
  * { box-sizing: border-box; margin: 0; }
  body { width: ${W * frames}px; height: ${H}px; position: relative; overflow: hidden; background: #F7F6F3;
    font-family: Geist, system-ui, sans-serif; -webkit-font-smoothing: antialiased; }
  .frame { position: absolute; top: 0; width: ${W}px; height: ${H}px; }
  .frame.dark { background: #0E1322; }
  .threads { position: absolute; inset: 0; width: ${W * frames}px; height: ${H}px; overflow: visible; }
  .copy { position: absolute; left: ${110 * k}px; right: ${110 * k}px; top: ${(p === platforms.android ? 130 : 170) * k}px; }
  .logo { margin-bottom: ${64 * k}px; height: ${76 * k}px; }
  .title { font-size: ${104 * k}px; line-height: 1.04; font-weight: 640; letter-spacing: -0.035em; color: #0E1322; text-wrap: balance; }
  .sub { margin-top: ${30 * k}px; font-size: ${48 * k}px; line-height: 1.25; font-weight: 420; letter-spacing: -0.01em; color: #5B6170; }
  .dark .title { color: #F2F4F8; }
  .dark .sub { color: #A9B1C2; }
  .phone { position: absolute; padding: ${ph.bezel}px; border-radius: ${ph.radius}px; background: #1B1C20;
    box-shadow: 0 0 0 ${3 * k}px #3A3C42 inset, 0 ${60 * k}px ${120 * k}px rgba(14, 19, 34, .22), 0 ${12 * k}px ${30 * k}px rgba(14, 19, 34, .14); }
  .dark .phone { background: #050608; box-shadow: 0 0 0 ${3 * k}px #2A2E3A inset, 0 ${60 * k}px ${140 * k}px rgba(0, 0, 0, .5); }
  .screen { width: ${ph.screenW}px; height: ${screenH}px; border-radius: ${ph.screenRadius}px; overflow: hidden; position: relative; background: #fff; }
  .island { position: absolute; left: 50%; top: ${ph.bezel + 26}px; width: 278px; height: 82px; margin-left: -139px; background: #000; border-radius: 41px; }
  .hole { position: absolute; left: 50%; top: ${ph.bezel + 22}px; width: 30px; height: 30px; margin-left: -15px; background: #000; border-radius: 50%;
    box-shadow: 0 0 0 3px rgba(255, 255, 255, .06); }
  .dot { display: inline-block; width: .52em; height: .52em; border-radius: 50%; margin-right: .38em; vertical-align: .06em; }
  .sep { margin: 0 .35em; }

  .callout { position: absolute; left: ${p.callout.left}px; width: ${p.callout.width}px; top: ${p.callout.top}px;
    padding: ${52 * k}px ${56 * k}px ${58 * k}px; background: #fff; border-radius: ${48 * k}px; font-family: ${p.callout.font};
    box-shadow: 0 ${50 * k}px ${110 * k}px rgba(14, 19, 34, .22), 0 ${10 * k}px ${26 * k}px rgba(14, 19, 34, .10), 0 0 0 1.5px rgba(14, 19, 34, .05); }
  .callout-row { display: flex; justify-content: space-between; align-items: baseline; gap: ${40 * k}px; }
  .callout-title { font-size: ${50 * k}px; line-height: 1.2; color: #000; letter-spacing: -0.01em; }
  .callout-duration { font-size: ${50 * k}px; font-weight: 600; font-variant-numeric: tabular-nums; color: #000; }
  .callout-meta { margin-top: ${20 * k}px; font-size: ${40 * k}px; color: #8A8A8E; }
  .callout-bar { display: flex; gap: ${8 * k}px; margin-top: ${40 * k}px; height: ${16 * k}px; }
  .callout-bar span { border-radius: ${8 * k}px; }
  .lock-threads { position: absolute; inset: 0; width: 100%; height: 100%; }
  ${p.locked?.css(ctx) ?? ''}
</style></head><body>
  ${pictures.map((pic, i) => (pic.dark ? `<div class="frame dark" style="left:${i * W}px"></div>` : '')).join('')}
  <svg class="threads" id="under" viewBox="0 0 ${W * frames} ${H}"></svg>
  ${pictures
    .map(
      (pic, i) => `
    <div class="frame${pic.dark ? ' dark' : ''}" style="left:${i * W}px;background:none">
      <div class="copy">${pic.top}<div class="title">${pic.title}</div><div class="sub">${pic.sub}</div></div>
      ${pic.body}
    </div>`,
    )
    .join('')}
  <svg class="threads" id="over" viewBox="0 0 ${W * frames} ${H}"></svg>
  <script>const W = ${W}, H = ${H}, colors = ${JSON.stringify(colors)}, layout = ${JSON.stringify({ k, screenTop, screenH })};
${readFileSync(new URL('./threads.js', import.meta.url), 'utf8')}</script>
</body></html>`
}

// The iPhone's Lock Screen, drawn rather than captured: the simulator shows the timer's seconds as dashes and the
// wrong weekday.
function iosLockScreen({ W, phoneLeft, screenTop, raw }) {
  // The raw screenshots' made-up now and day (appstore-screenshots.sh, play-demo-data.py): 16:30 on the day they
  // were taken, the running timer started at 15:43.
  const day = captureDay(raw).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' }).replace(',', '')
  return `
  <div class="lock">
    <svg class="lock-threads" data-origin="${2 * W + phoneLeft + 20} ${screenTop}"></svg>
    <svg class="lock-status" viewBox="0 0 84 16">
      <rect x="0" y="10" width="3" height="5" rx="1"/><rect x="4.6" y="7.5" width="3" height="7.5" rx="1"/>
      <rect x="9.2" y="5" width="3" height="10" rx="1"/><rect x="13.8" y="2.5" width="3" height="12.5" rx="1"/>
      <path d="M31 15.2 l-2.4-2.6 a3.4 3.4 0 0 1 4.8 0z M26.6 10.6 a6.4 6.4 0 0 1 8.8 0 l-1.4 1.5 a4.4 4.4 0 0 0-6 0z M24.2 8.1 a9.8 9.8 0 0 1 13.6 0 l-1.4 1.5 a7.8 7.8 0 0 0-10.8 0z"/>
      <rect x="45" y="2.5" width="25" height="12" rx="3.6" fill="none" stroke="#fff" stroke-opacity=".45" stroke-width="1.2"/>
      <rect x="47" y="4.5" width="21" height="8" rx="2"/><path d="M71.6 6.4 v4.2 a2 2 0 0 0 0-4.2z" fill-opacity=".45"/>
    </svg>
    <div class="lock-date">${day}</div>
    <div class="lock-time">16:30</div>
    <div class="activity">
      <img src="${icon}" class="activity-icon">
      <div class="activity-text">
        <div class="activity-title">Webhook signatures</div>
        <div class="activity-project"><span class="dot" style="background:${blue}"></span>Acme API</div>
        <div class="activity-time">0:47:06</div>
      </div>
      <div class="activity-stop"><span></span></div>
    </div>
    <div class="lock-button" style="left:70px"><svg viewBox="0 0 24 24"><path d="M7.5 2h9v4l-2.5 3.5V20a2 2 0 0 1-4 0V9.5L7.5 6z" fill="none" stroke="#fff" stroke-width="1.8" stroke-linejoin="round"/></svg></div>
    <div class="lock-button" style="right:70px"><svg viewBox="0 0 24 24"><path d="M4 8h3l2-3h6l2 3h3v11H4z" fill="none" stroke="#fff" stroke-width="1.8" stroke-linejoin="round"/><circle cx="12" cy="13" r="3.4" fill="none" stroke="#fff" stroke-width="1.8"/></svg></div>
    <div class="home-indicator"></div>
  </div>`
}
iosLockScreen.css = () => `
  .lock { position: absolute; inset: 0; color: #fff; font-family: -apple-system, 'SF Pro Display', system-ui, sans-serif;
    background: radial-gradient(120% 70% at 30% 0%, #26355E 0%, #131B33 45%, #0A0F1E 100%); }
  .lock-status { position: absolute; top: 46px; right: 68px; width: 168px; height: 32px; fill: #fff; }
  .lock-date { position: absolute; top: 205px; width: 100%; text-align: center; font-size: 46px; font-weight: 600; opacity: .82; }
  .lock-time { position: absolute; top: 250px; width: 100%; text-align: center; font-size: 300px; line-height: 1;
    font-weight: 700; letter-spacing: -0.02em; opacity: .9; font-family: 'SF Pro Rounded', -apple-system, system-ui, sans-serif; }
  .activity { position: absolute; left: 36px; right: 36px; top: 1330px; height: 250px; border-radius: 64px;
    display: flex; align-items: center; padding: 0 46px 0 40px; gap: 34px;
    background: rgba(28, 30, 38, .72); backdrop-filter: blur(30px); box-shadow: 0 0 0 1.5px rgba(255, 255, 255, .08) inset; }
  .activity-icon { width: 92px; height: 92px; border-radius: 22px; flex: none; }
  .activity-text { flex: 1; font-family: -apple-system, 'SF Pro Text', system-ui, sans-serif; }
  .activity-title { font-size: 40px; font-weight: 600; letter-spacing: -0.01em; }
  .activity-project { margin-top: 6px; font-size: 32px; color: rgba(235, 235, 245, .6); }
  .activity-time { margin-top: 6px; font-size: 58px; font-weight: 600; color: #F7BE2B; font-variant-numeric: tabular-nums; }
  .activity-stop { width: 112px; height: 112px; border-radius: 50%; background: #F04438; display: grid; place-items: center; flex: none; }
  .activity-stop span { width: 40px; height: 40px; border-radius: 8px; background: #fff; }
  .lock-button { position: absolute; bottom: 120px; width: 112px; height: 112px; border-radius: 50%;
    background: rgba(255, 255, 255, .14); backdrop-filter: blur(20px); display: grid; place-items: center; }
  .lock-button svg { width: 50px; height: 50px; }
  .home-indicator { position: absolute; bottom: 22px; left: 50%; width: 300px; margin-left: -150px; height: 10px;
    border-radius: 5px; background: rgba(255, 255, 255, .85); }`

// The day the raw screenshots were taken, from the first one's file (today if there is none).
function captureDay(raw) {
  const first = `${repo}/${raw}/${names[0]}.png`
  return existsSync(first) ? statSync(first).mtime : new Date()
}

// The header (App Store) or feature graphic (Google Play): laid out on the App Store header's height (1646) and scaled
// by u, so every size looks the same; the threads' points are fractions of the width and height.
function header(W, H) {
  const u = H / 1646
  return `<!doctype html>
<html><head><meta charset="utf-8"><style>
  * { box-sizing: border-box; margin: 0; }
  body { width: ${W}px; height: ${H}px; overflow: hidden; position: relative;
    background: radial-gradient(55% 85% at 50% 45%, #1C2848 0%, #121A30 50%, #0E1322 100%);
    font-family: -apple-system, 'SF Pro Text', system-ui, sans-serif; -webkit-font-smoothing: antialiased; }
  svg { position: absolute; inset: 0; width: ${W}px; height: ${H}px; }
  .card { position: absolute; left: 50%; top: 50%; width: ${1760 * u}px; margin-left: ${-880 * u}px; transform: translateY(-50%);
    padding: ${84 * u}px ${100 * u}px ${100 * u}px; border-radius: ${84 * u}px; background: #1A2033;
    box-shadow: 0 0 0 ${2 * u}px rgba(255, 255, 255, .07) inset, 0 ${80 * u}px ${160 * u}px rgba(0, 0, 0, .45), 0 ${20 * u}px ${50 * u}px rgba(0, 0, 0, .3); }
  .row { display: flex; justify-content: space-between; align-items: baseline; gap: ${60 * u}px; }
  .title { font-size: ${96 * u}px; line-height: 1.18; color: #F2F4F8; letter-spacing: -0.01em; }
  .duration { font-size: ${96 * u}px; font-weight: 600; color: #F2F4F8; font-variant-numeric: tabular-nums; }
  .meta { margin-top: ${28 * u}px; font-size: ${60 * u}px; color: #8E95A6; }
  .dot { display: inline-block; width: .5em; height: .5em; border-radius: 50%; margin-right: .38em; vertical-align: .06em; }
  .sep { margin: 0 .35em; }
  .bar { display: flex; gap: ${14 * u}px; margin-top: ${64 * u}px; height: ${30 * u}px; }
  .bar span { border-radius: ${15 * u}px; }
</style></head><body>
  <svg id="under" viewBox="0 0 ${W} ${H}"></svg>
  <div class="card">
    <div class="row"><div class="title">Checkout and webhooks</div><div class="duration">1:20:00</div></div>
    <div class="meta"><span class="dot" style="background:${blue}"></span><span style="color:#6C9BF2">Acme API</span>&nbsp;50%<span class="sep">·</span><span class="dot" style="background:${pink}"></span><span style="color:#E57FA8">Acme Web</span>&nbsp;30%<span class="sep">·</span><span class="dot" style="background:${green}"></span><span style="color:#5CC071">Birch</span>&nbsp;20%</div>
    <div class="bar"><span data-part="blue" style="flex:50;background:${blue}"></span><span data-part="pink" style="flex:30;background:${pink}"></span><span data-part="green" style="flex:20;background:${green}"></span></div>
  </div>
  <svg id="over" viewBox="0 0 ${W} ${H}"></svg>
  <script>const W = ${W}, H = ${H}, u = ${u}, colors = ${JSON.stringify(colors)};\n(${headerThreads})()</script>
</body></html>`
}

// Runs in the header's page, after layout.
function headerThreads() {
  const { blue, pink, green, marigold, ink } = colors
  const NS = 'http://www.w3.org/2000/svg'
  const el = (parent, name, attrs) => {
    const node = document.createElementNS(NS, name)
    for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v)
    parent.appendChild(node)
    return node
  }
  // Centripetal Catmull–Rom, as in threads.js.
  function smooth(points) {
    const dist = (a, b) => Math.max(Math.hypot(b[0] - a[0], b[1] - a[1]) ** 0.5, 1e-3)
    let d = `M ${points[0][0]} ${points[0][1]}`
    for (let i = 0; i < points.length - 1; i++) {
      const p1 = points[i]
      const p2 = points[i + 1]
      const p0 = points[i - 1] ?? [2 * p1[0] - p2[0], 2 * p1[1] - p2[1]]
      const p3 = points[i + 2] ?? [2 * p2[0] - p1[0], 2 * p2[1] - p1[1]]
      const d1 = dist(p0, p1)
      const d2 = dist(p1, p2)
      const d3 = dist(p2, p3)
      const c1 = [0, 1].map((k) => (d1 * d1 * p2[k] - d2 * d2 * p0[k] + (2 * d1 * d1 + 3 * d1 * d2 + d2 * d2) * p1[k]) / (3 * d1 * (d1 + d2)))
      const c2 = [0, 1].map((k) => (d3 * d3 * p1[k] - d2 * d2 * p3[k] + (2 * d3 * d3 + 3 * d3 * d2 + d2 * d2) * p2[k]) / (3 * d3 * (d3 + d2)))
      d += ` C ${c1} ${c2} ${p2}`
    }
    return d
  }
  const width = 10 * u
  const stroke = (parent, d, color, extra = {}) =>
    el(parent, 'path', { d, fill: 'none', stroke: color, 'stroke-width': width, 'stroke-linecap': 'round', ...extra })
  const under = document.getElementById('under')
  const over = document.getElementById('over')
  const card = document.querySelector('.card').getBoundingClientRect()
  const bar = document.querySelector('.bar').getBoundingClientRect()
  const y = bar.top + bar.height / 2
  const L = card.left
  const R = card.right
  const g = 100 * u // how far the threads run level beside the card before they reach the bar
  const left = (f) => f * L // a point across the space left of the card
  const right = (f) => R + f * (W - R)

  // From the left, three threads of parallel work run into the bar, one entry split three ways.
  // Each makes one slow turn, always moving right.
  const blueIn = smooth([[-80, 0.36 * H], [left(0.35), 0.4 * H], [left(0.7), 0.5 * H], [L - g, y], [bar.left + 10, y]])
  const pinkIn = smooth([[-80, 0.8 * H], [left(0.35), 0.76 * H], [left(0.7), 0.66 * H], [L - g, y], [bar.left + 10, y]])
  const greenIn = smooth([[-80, 0.55 * H], [left(0.35), 0.62 * H], [left(0.7), 0.6 * H], [L - g, y], [bar.left + 10, y]])
  // To the right, out of the bar's end, each part goes its own way.
  const blueOut = smooth([[bar.right - 10, y], [R + g, y], [right(0.4), 0.48 * H], [right(0.75), 0.38 * H], [W + 80, 0.34 * H]])
  const pinkOut = smooth([[bar.right - 10, y], [R + g, y], [right(0.4), 0.68 * H], [right(0.75), 0.76 * H], [W + 80, 0.74 * H]])
  const greenOut = smooth([[bar.right - 10, y], [R + g, y], [right(0.4), 0.6 * H], [right(0.75), 0.55 * H], [W + 80, 0.58 * H]])

  const pairs = [[greenIn, green], [greenOut, green], [blueIn, blue], [pinkIn, pink], [blueOut, blue], [pinkOut, pink]]
  for (const [d, color] of pairs) stroke(under, d, color)
  // Over the card's padding, so the threads visibly reach the bar.
  const clip = el(el(over, 'defs', {}), 'clipPath', { id: 'edges' })
  el(clip, 'rect', { x: L - 4, y: 0, width: bar.left - L + 14, height: H })
  el(clip, 'rect', { x: bar.right - 10, y: 0, width: R - bar.right + 14, height: H })
  for (const [d, color] of pairs) stroke(over, d, color, { 'clip-path': 'url(#edges)' })

  // The dots: an agent's prompts along the green thread on both sides, a dot on each of the others, and the running
  // timer as a marigold dot on the way out. In the video they travel left to right, a whole thread in each loop, so a
  // dot that reaches the end of its thread is off the picture or behind the card when it starts again: the loop has no
  // seam. A dot fades under the card as it reaches it and lights its part of the bar for a moment. At t = 0 they stand
  // where the still header has them.
  const movers = []
  const along = (d, fractions, color, r) => {
    const path = stroke(under, d, 'none')
    const length = path.getTotalLength()
    for (const f of fractions) {
      const ring = el(under, 'circle', { r: (r + 8) * u, fill: ink })
      const dot = el(under, 'circle', { r: r * u, fill: color })
      movers.push({ path, length, f, ring, dot, color, incoming: d.startsWith('M -80') })
    }
  }
  along(greenIn, [0.2, 0.27, 0.34], green, 10)
  along(blueIn, [0.42], blue, 16)
  along(pinkIn, [0.12], pink, 16)
  along(pinkOut, [0.62], pink, 16)
  along(greenOut, [0.66, 0.73, 0.8], green, 10)
  along(blueOut, [0.78], marigold, 18)
  along(blueOut, [0.3], blue, 12)

  const parts = Object.fromEntries([...document.querySelectorAll('.bar span')].map((s) => [s.dataset.part, s]))
  const hue = { [blue]: 'blue', [pink]: 'pink', [green]: 'green' }
  window.render = (t) => {
    const glow = { blue: 0, pink: 0, green: 0 }
    for (const m of movers) {
      const f = (m.f + t) % 1
      const p = m.path.getPointAtLength(m.length * f)
      // How far the dot is from the card: it fades as it slips under it, and fades in as it comes out the other side.
      const gap = m.incoming ? L - p.x : p.x - R
      const opacity = Math.min(1, Math.max(0, gap / (140 * u)))
      for (const c of [m.ring, m.dot]) {
        c.setAttribute('cx', p.x)
        c.setAttribute('cy', p.y)
        c.setAttribute('opacity', opacity)
      }
      // A dot reaching the card lights its part of the bar, which fades again as the dot goes on under the card.
      const part = hue[m.color]
      if (m.incoming && part) glow[part] = Math.max(glow[part], Math.exp(-((gap / (170 * u)) ** 2)))
    }
    for (const [part, span] of Object.entries(parts)) {
      const k = glow[part]
      span.style.boxShadow = k > 0.01 ? `0 0 ${30 * u * k}px ${4 * u * k}px ${getComputedStyle(span).backgroundColor}` : 'none'
      span.style.filter = `brightness(${1 + 0.22 * k})`
    }
  }
  window.render(0)
}

const which = process.argv[2]
const video = process.argv.includes('--video')
const p = platforms[which]
if (!p) {
  console.error('Usage: node branding/store/store-art.mjs ios [--video] | android')
  process.exit(2)
}
const out = `${repo}/${p.out}`
const work = `${repo}/branding/store/build`
mkdirSync(out, { recursive: true })
mkdirSync(work, { recursive: true })

const browser = await chromium.launch()
const page = await browser.newPage({ viewport: { width: p.W * 5, height: p.H }, deviceScaleFactor: 1 })
writeFileSync(`${work}/${which}-screenshots.html`, screenshots(p))
await page.goto(file(`${work}/${which}-screenshots.html`))
await page.evaluate(() => document.fonts.ready)
await page.waitForTimeout(300)
for (const [i, name] of names.entries()) {
  const target = p.files[name] ?? name
  await page.screenshot({ path: `${out}/${target}.png`, clip: { x: i * p.W, y: 0, width: p.W, height: p.H } })
  console.log(`wrote ${p.out}/${target}.png`)
}
await page.screenshot({ path: `${work}/${which}-panorama.png` })
console.log(`wrote branding/store/build/${which}-panorama.png (a preview of all five side by side)`)

// App Store Connect asks for the medium iPhone size too; the aspect ratio is the same within a pixel, so each picture
// is scaled down to it.
if (p.medium) {
  const [w, h] = p.medium
  mkdirSync(`${out}/6.3`, { recursive: true })
  await page.setViewportSize({ width: w, height: h })
  for (const name of names) {
    const target = p.files[name] ?? name
    const png = readFileSync(`${out}/${target}.png`).toString('base64')
    await page.setContent(`<body style="margin:0"><img src="data:image/png;base64,${png}" style="width:${w}px;height:${h}px;display:block"></body>`)
    await page.screenshot({ path: `${out}/6.3/${target}.png` })
  }
  console.log(`wrote ${p.out}/6.3/`)
}

for (const [name, W, H] of p.headers) {
  writeFileSync(`${work}/${which}-header.html`, header(W, H))
  await page.setViewportSize({ width: W, height: H })
  await page.goto(file(`${work}/${which}-header.html`))
  await page.waitForTimeout(300)
  await page.screenshot({ path: `${out}/${name}.png` })
  console.log(`wrote ${p.out}/${name}.png`)
  if (video && name === p.video) await renderVideo(page, name)
}
await browser.close()

// The App Store header video (3840 × 1646, 21:9): the still header, alive. Ten calm seconds that loop without a seam,
// no sound; the first frame is the still header, so it is also the poster frame. Frames are drawn one by one and
// joined by ffmpeg into H.264, which App Store Connect takes.
async function renderVideo(tab, name) {
  const seconds = 10
  const fps = 30
  const frames = `${work}/${which}-header-frames`
  rmSync(frames, { recursive: true, force: true })
  mkdirSync(frames, { recursive: true })
  for (let i = 0; i < seconds * fps; i++) {
    await tab.evaluate((t) => window.render(t), i / (seconds * fps))
    await tab.screenshot({ path: `${frames}/${String(i).padStart(4, '0')}.png` })
    if (i % 60 === 0) console.log(`frame ${i} of ${seconds * fps}`)
  }
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-framerate', String(fps), '-i', `${frames}/%04d.png`,
    '-c:v', 'libx264', '-preset', 'slow', '-crf', '14', '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
    '-r', String(fps), `${out}/${name}.mp4`])
  console.log(`wrote ${p.out}/${name}.mp4`)
}
