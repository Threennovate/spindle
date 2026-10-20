// The project threads of the store screenshots' panorama, run in the page by store-art.mjs after layout. They tell the
// same story as the captions, left to right:
//   1. one ink thread runs into the lifted entry's split bar, and two project threads leave it;
//   2. a third project joins from below, with an agent's prompts as dots along it;
//   3. they cross over each other: parallel work;
//   4. behind the third phone they gather into the one running timer, and part again (on the iPhone, its Lock Screen
//      shows it: the threads go through the wallpaper into a marigold ring);
//   5. they fan out to lanes of their own: each part counts on its own project;
//   6. they come back together, one splitting for a moment as an agent runs beside it, and leave the last picture.
// The threads are hidden behind the phones; the points there only keep the curves smooth.
//
// The route is drawn once, for the iPhone's layout (1320 wide pictures, the screen from y 660, 2086 tall), and the
// page's `layout` maps it onto the platform's: x in a picture scales with its width, y follows the phone's screen.

const NS = 'http://www.w3.org/2000/svg'
const ink = '#0E1322'
const paper = '#F7F6F3'
const marigold = '#F0B21B'
const { blue, pink, green } = colors
const { k, screenTop, screenH } = layout

const under = document.getElementById('under')
const over = document.getElementById('over')
const lock = document.querySelector('.lock-threads')

const bar = document.querySelector('.callout-bar').getBoundingClientRect()
const barY = bar.top + bar.height / 2
const barStart = bar.left
const barEnd = bar.right
const F = (i) => i * W // where picture i starts

// A point of the route: dx from the start of picture f, and y as on the iPhone, or (B) dy from the split bar.
const Y = (y) => screenTop + ((y - 660) / 2086) * screenH
const P = (f, dx, y) => [F(f) + dx * k, Y(y)]
const B = (f, dx, dy) => [F(f) + dx * k, barY + dy * k]
const X = (f, dx) => F(f) + dx * k

// Centripetal Catmull–Rom through the points, as cubic Béziers: unlike the uniform kind, it doesn't kink or overshoot
// where the points are unevenly spaced, as they are here (far apart behind a phone, close together in a gap).
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
    d += ` C ${c1[0]} ${c1[1]}, ${c2[0]} ${c2[1]}, ${p2[0]} ${p2[1]}`
  }
  return d
}

const merge = [F(2) + W / 2, Y(1560)]

// The turns happen mostly behind the phones, so the threads cross each gap at a gentle slant.
const threads = [
  {
    color: blue,
    points: [
      [barEnd - 8, barY], B(1, -105, -30), B(1, 60, -250), B(1, 170, -360),
      P(1, 700, 1300), P(2, -170, 1360),
      P(2, -10, 1470), P(2, 190, 1560),
      P(2, 400, 1480), merge, P(2, 920, 1500), P(3, -170, 1440),
      P(3, -10, 1320), P(3, 170, 1190),
      P(3, 700, 1000), P(4, -170, 1220),
      P(4, -30, 1390), P(4, 170, 1470),
      P(4, 700, 1480), P(5, -160, 1500), P(5, -60, 1900), P(5, 60, 2460),
    ],
  },
  {
    color: pink,
    points: [
      [barEnd - 8, barY], B(1, -105, -10), B(1, 60, -130), B(1, 170, -190),
      P(1, 700, 1500), P(2, -170, 1480),
      P(2, -10, 1410), P(2, 190, 1500),
      P(2, 400, 1620), merge, P(2, 920, 1580), P(3, -170, 1560),
      P(3, -10, 1560), P(3, 170, 1560),
      P(3, 700, 1560), P(4, -170, 1560),
      P(4, -30, 1530), P(4, 170, 1520),
      P(4, 700, 1520), P(5, -160, 1540), P(5, -40, 1980), P(5, 60, 2540),
    ],
  },
  {
    color: green,
    points: [
      [X(1, -170), H + 60], P(1, 20, 2420), P(1, 170, 2250),
      P(1, 700, 1700), P(2, -170, 1600),
      P(2, -10, 1640), P(2, 190, 1460),
      P(2, 400, 1700), merge, P(2, 920, 1660), P(3, -170, 1700),
      P(3, -10, 1830), P(3, 170, 1950),
      P(3, 700, 2160), P(4, -170, 1880),
      P(4, -30, 1700), P(4, 170, 1590),
      P(4, 700, 1560), P(5, -160, 1580), P(5, -20, 2060), P(5, 60, 2620),
    ],
  },
]

function el(parent, name, attrs) {
  const node = document.createElementNS(NS, name)
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v)
  parent.appendChild(node)
  return node
}

function stroke(parent, d, color, width = 7, extra = {}) {
  return el(parent, 'path', { d, fill: 'none', stroke: color, 'stroke-width': width * k, 'stroke-linecap': 'round', ...extra })
}

// The point of a path at a given x, searching from the start (the first crossing).
function atX(path, x) {
  const total = path.getTotalLength()
  let best = null
  for (let l = 0; l <= total; l += 2) {
    const p = path.getPointAtLength(l)
    if (Math.abs(p.x - x) < 1.5) return p
    if (!best || Math.abs(p.x - x) < Math.abs(best.x - x)) best = p
  }
  return best
}

function dot(parent, p, color, r = 11) {
  const ring = p.x > F(2) && p.x < F(3) ? ink : paper // the third picture is midnight
  el(parent, 'circle', { cx: p.x, cy: p.y, r: (r + 6) * k, fill: ring })
  el(parent, 'circle', { cx: p.x, cy: p.y, r: r * k, fill: color })
}

const paths = threads.map(({ color, points }) => {
  const d = smooth(points)
  return { color, d, node: stroke(under, d, color) }
})
const [bluePath, pinkPath, greenPath] = paths.map((p) => p.node)

// 1. The entry: an ink thread from the edge into the bar, and the two threads leaving it, over the callout and the phone.
const clip = el(el(over, 'defs', {}), 'clipPath', { id: 'leaving' })
el(clip, 'rect', { x: barEnd - 10, y: 0, width: X(1, -140) - barEnd, height: H })
for (const { d, color } of paths.slice(0, 2)) stroke(over, d, color, 7, { 'clip-path': 'url(#leaving)' })
stroke(over, `M -10 ${barY} H ${barStart + 4}`, ink)

// 2. Prompts along the green thread as it joins, and one on each of the others in the first gap.
for (const dx of [-60, -20, 20]) dot(under, atX(greenPath, X(1, dx)), green, 8)
dot(under, atX(bluePath, X(1, 90)), blue)
dot(under, atX(pinkPath, X(1, 120)), pink)

// 3. Crossing in the second gap: dots where each thread leaves it.
dot(under, atX(bluePath, X(2, -110)), blue)
dot(under, atX(greenPath, X(2, 120)), green)

// 5. Lanes of their own, each ending its fan with a dot.
for (const path of [bluePath, pinkPath, greenPath]) dot(under, atX(path, X(3, 120)), path.getAttribute('stroke'), 13)

// 6. An agent beside the pink thread: a branch that leaves it and comes back, with its prompts.
const from = atX(pinkPath, X(4, -140))
const to = atX(pinkPath, X(4, 130))
const sag = 230 * k
const branch = stroke(under, `M ${from.x} ${from.y} C ${from.x + 90 * k} ${from.y + sag}, ${to.x - 90 * k} ${to.y + sag}, ${to.x} ${to.y}`, pink, 5)
const length = branch.getTotalLength()
for (const f of [0.32, 0.5, 0.68]) dot(under, branch.getPointAtLength(length * f), pink, 7)
dot(under, atX(bluePath, X(4, -100)), blue, 8)

// 4. The iPhone's Lock Screen: the same threads through the wallpaper, gathering into the running timer.
if (lock) {
  const [ox, oy] = lock.dataset.origin.split(' ').map(Number)
  lock.setAttribute('viewBox', `${ox} ${oy} ${lock.clientWidth} ${lock.clientHeight}`)
  for (const { d, color } of paths) stroke(lock, d, color, 7, { opacity: 0.85 })
  el(lock, 'circle', { cx: merge[0], cy: merge[1], r: 40 * k, fill: 'none', stroke: marigold, 'stroke-width': 5 * k, opacity: 0.9 })
  el(lock, 'circle', { cx: merge[0], cy: merge[1], r: 16 * k, fill: marigold })
}
