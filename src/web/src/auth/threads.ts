/**
 * The threads on the sign-in pages, drawn the way the website draws them. Each field the form asks for is one project
 * thread: from one point at the top of the page they come down in a tight braid, fan out into lanes and turn into the
 * field's edge of the card. A thread stays dotted (unrecorded) until its field is filled in, then turns solid as you
 * type, and short pulses run along the solid part. Signing in sends one ink thread out of the other side of the card,
 * up to the logo, where it winds the mark bar by bar; sending a reset link runs it out of the page instead.
 *
 * The page marks what the threads need: the card ([data-thread-card]), each port ([data-thread="email"] on an input or
 * any element), the submit button ([data-thread-go]) and the logo ([data-thread-mark]). The engine measures them every
 * frame, so errors, alerts and route changes simply move the threads. Below 48 px of room left of the card the threads
 * are off and the page looks as it always did.
 */

export type ThreadFinale = 'wind' | 'send'

/** The website's three project colours. */
const COLORS: Record<string, string> = {
  social: '#13987F',
  name: '#13987F',
  repeat: '#13987F',
  email: '#2D6BE0',
  password: '#D0477F',
}
export const threadColor = (port: string) => COLORS[port] ?? COLORS.email

interface Pt {
  x: number
  y: number
  /** Reached by an S-curve instead of a corner. */
  s?: boolean
  /** This corner's own radius. */
  r?: number
}

interface Thread {
  port: string
  color: string
  p: Pt[]
  /** The first sample of the branch into the card, where a dotted thread starts. */
  f: number
  n: number
}

interface Bar {
  a: Pt
  b: Pt
  f0: number
  f1: number
  w: number
  accent: boolean
}

interface Geometry {
  key: string
  off: boolean
  X: number
  threads: Thread[]
  windInk: Pt[] | null
  sendInk: Pt[] | null
  wind: Pt[]
  bars: Bar[]
}

interface PortState {
  fill: number
  full: boolean
  pulses: number[]
  next: number
  shakeAt: number
  invalid: boolean
  /** After a failed sign-in, the value that was refused: the thread stays dotted until it changes. */
  refused?: string
}

interface Finale {
  kind: ThreadFinale
  at: number
  done: boolean
  resolve: (() => void) | null
  failedAt: number | null
  windAt: number | null
  endedAt: number | null
}

const clamp = (v: number, a: number, b: number) => Math.min(b, Math.max(a, v))
const smooth = (u: number) => {
  const k = clamp(u, 0, 1)
  return k * k * (3 - 2 * k)
}
// Per-frame easing written for 60 fps, made independent of the frame rate.
const toward = (rate: number, dt: number) => 1 - Math.pow(1 - rate, dt * 60)

/** When the sign-in pages were last on screen: moving between them re-threads quickly instead of growing slowly. */
let lastSeen = -Infinity

const STEP = 4
const GROW = 900 / STEP
const REGROW = 1700 / STEP
const INK = 1000 / STEP
const WIND_SECONDS = 0.65

/** A polyline through the points: rounded corners of radius R, and an S-curve into any point marked s. */
function dense(pts: Pt[], R: number): Pt[] {
  const out: Pt[] = [{ x: pts[0].x, y: pts[0].y }]
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i], n = pts[i + 1]
    if (b.s) {
      const my = (a.y + b.y) / 2
      for (let j = 1; j <= 28; j++) {
        const t = j / 28, m = 1 - t
        out.push({ x: (m * m * m + 3 * m * m * t) * a.x + (3 * m * t * t + t * t * t) * b.x, y: m * m * m * a.y + (3 * m * m * t + 3 * m * t * t) * my + t * t * t * b.y })
      }
      continue
    }
    if (n && !n.s) {
      const l1 = Math.hypot(b.x - a.x, b.y - a.y), l2 = Math.hypot(n.x - b.x, n.y - b.y)
      if (l1 > 0.01 && l2 > 0.01) {
        const r = Math.min(b.r ?? R, l1 / 2, l2 / 2)
        const ux = (b.x - a.x) / l1, uy = (b.y - a.y) / l1, vx = (n.x - b.x) / l2, vy = (n.y - b.y) / l2
        if (r > 0.3 && Math.abs(ux * vy - uy * vx) > 1e-3) {
          const sx = b.x - ux * r, sy = b.y - uy * r, ex = b.x + vx * r, ey = b.y + vy * r
          out.push({ x: sx, y: sy })
          for (let j = 1; j <= 12; j++) {
            const t = j / 12, m = 1 - t
            out.push({ x: m * m * sx + 2 * m * t * b.x + t * t * ex, y: m * m * sy + 2 * m * t * b.y + t * t * ey })
          }
          continue
        }
      }
    }
    out.push({ x: b.x, y: b.y })
  }
  return out
}

/** Even samples every `step` px along the rounded polyline. */
function sample(pts: Pt[], R: number, step = STEP): Pt[] {
  const d = dense(pts, R)
  const out: Pt[] = [{ x: d[0].x, y: d[0].y }]
  let carry = 0
  for (let i = 1; i < d.length; i++) {
    const a = d[i - 1], b = d[i]
    const len = Math.hypot(b.x - a.x, b.y - a.y)
    if (len < 1e-6) continue
    let pos = step - carry
    while (pos <= len) {
      const u = pos / len
      out.push({ x: a.x + (b.x - a.x) * u, y: a.y + (b.y - a.y) * u })
      pos += step
    }
    carry = len - (pos - step)
  }
  const end = d[d.length - 1], last = out[out.length - 1]
  if (Math.hypot(end.x - last.x, end.y - last.y) > 0.5) out.push({ x: end.x, y: end.y })
  return out
}

export class ThreadEngine {
  private root: HTMLElement | null = null
  private canvas: HTMLCanvasElement | null = null
  private ctx: CanvasRenderingContext2D | null = null
  private onWinding: ((on: boolean) => void) | null = null
  private raf = 0
  private t0 = performance.now()
  private last = 0
  private reduce = false
  private geo: Geometry | null = null
  private ports = new Map<string, PortState>()
  private portsKey = ''
  private plucks: { y: number; a: number; t: number }[] = []
  private ripples: { x: number; y: number; c: string; t: number }[] = []
  private finale: Finale | null = null
  private growAt = 0.3
  private growSpeed = GROW
  private snapAt = -9
  private colors = { ring: '#000', ink: '#fff', accent: '#f7be2b' }
  private colorsAt = -9
  private size = { w: 0, h: 0, dpr: 1 }
  private lastMx: number | null = null
  private lastPluck = 0
  private winding = false

  attach(root: HTMLElement, canvas: HTMLCanvasElement, onWinding: (on: boolean) => void) {
    this.root = root
    this.canvas = canvas
    this.ctx = canvas.getContext('2d')
    this.onWinding = onWinding
    this.reduce = matchMedia('(prefers-reduced-motion: reduce)').matches
    this.t0 = performance.now()
    this.last = 0
    this.geo = null
    this.ports.clear()
    this.portsKey = ''
    this.plucks = []
    this.ripples = []
    this.finale = null
    this.snapAt = -9
    this.colorsAt = -9
    this.size = { w: 0, h: 0, dpr: 1 }
    this.winding = false
    const recent = this.t0 - lastSeen < 15000
    this.growAt = recent ? 0 : 0.3
    this.growSpeed = recent ? REGROW : GROW
    addEventListener('pointermove', this.onPointer, { passive: true })
    this.raf = requestAnimationFrame(this.frame)
  }

  detach() {
    cancelAnimationFrame(this.raf)
    removeEventListener('pointermove', this.onPointer)
    if (this.root) delete this.root.dataset.threads
    this.finale?.resolve?.()
    this.root = this.canvas = this.ctx = null
    lastSeen = performance.now()
  }

  /** Starts the finale as the form is sent: the threads fill and the ink thread sets off. */
  begin(kind: ThreadFinale) {
    if (this.finale?.resolve) this.finale.resolve()
    if (!this.active() || this.reduce) return
    this.finale = { kind, at: this.now(), done: false, resolve: null, failedAt: null, windAt: null, endedAt: null }
    for (const state of this.ports.values()) state.pulses.push(0, -14)
  }

  /** The server said yes: finish the finale. Resolves once the mark is wound (or the ink has left), at most 2.5 s on. */
  complete(): Promise<void> {
    const finale = this.finale
    if (!finale || finale.failedAt !== null || !this.active()) return Promise.resolve()
    return new Promise((resolve) => {
      const timer = setTimeout(done, 2500)
      function done() {
        clearTimeout(timer)
        resolve()
      }
      finale.done = true
      finale.resolve = done
    })
  }

  /** The server said no: the ink thread fades, and the refused field's thread snaps back to dotted. */
  fail(port?: string) {
    const t = this.now()
    if (this.finale && this.finale.failedAt === null) this.finale.failedAt = t
    if (!port) return
    const state = this.state(port)
    const el = this.root?.querySelector<HTMLInputElement>(`input[data-thread="${port}"]`)
    state.refused = el?.value ?? ''
    state.shakeAt = t
    this.snapAt = t
  }

  private now() {
    return (performance.now() - this.t0) / 1000
  }

  private active() {
    return !!this.geo && !this.geo.off
  }

  private state(port: string): PortState {
    let state = this.ports.get(port)
    if (!state) {
      state = { fill: 0, full: false, pulses: [], next: this.now() + 1 + Math.random() * 1.5, shakeAt: -9, invalid: false }
      this.ports.set(port, state)
    }
    return state
  }

  private setWinding(on: boolean) {
    if (on === this.winding) return
    this.winding = on
    this.onWinding?.(on)
  }

  private onPointer = (e: PointerEvent) => {
    if (!this.root || !this.geo || this.geo.off || e.pointerType !== 'mouse' || this.reduce) return
    const r = this.root.getBoundingClientRect(), sx = e.clientX - r.left, sy = e.clientY - r.top, t = this.now()
    if (this.lastMx !== null && Math.abs(sx - (this.geo.X + 4)) < 30) {
      const dx = sx - this.lastMx
      if (Math.abs(dx) > 2 && t - this.lastPluck > 0.11) {
        this.plucks.push({ y: sy, a: clamp(dx * 1.4, -13, 13), t })
        this.lastPluck = t
        if (this.plucks.length > 8) this.plucks.shift()
      }
    }
    this.lastMx = sx
  }

  private measure(): Geometry | null {
    const root = this.root
    if (!root) return null
    const rr = root.getBoundingClientRect()
    const rel = (el: Element | null | undefined) => {
      if (!el) return null
      const r = el.getBoundingClientRect()
      return { l: r.left - rr.left, t: r.top - rr.top, r: r.right - rr.left, b: r.bottom - rr.top, w: r.width, cy: (r.top + r.bottom) / 2 - rr.top }
    }
    const cardEl = root.querySelector('[data-thread-card]')
    const card = rel(cardEl)
    const mark = rel(root.querySelector('[data-thread-mark] svg'))
    const go = rel(cardEl?.querySelector('[data-thread-go]'))
    const portEls = cardEl ? [...cardEl.querySelectorAll<HTMLElement>('[data-thread]')] : []
    const ports = portEls.map((el) => ({ port: el.dataset.thread ?? '', box: rel(el)! })).filter((p) => p.port && p.box.w > 0)
    const key = [rr.width, rr.height, card?.l, card?.t, card?.b, go?.cy, mark?.l, mark?.t, ...ports.flatMap((p) => [p.port, p.box.cy])]
      .map((v) => (typeof v === 'number' ? Math.round(v) : String(v)))
      .join()
    if (this.geo?.key === key) return this.geo

    const X = card ? card.l - 72 : 0
    if (!card || !mark || ports.length === 0 || X < 48) {
      return (this.geo = { key, off: true, X, threads: [], windInk: null, sendInk: null, wind: [], bars: [] })
    }

    // A new set of fields threads itself again from the top, quickly.
    const portsKey = ports.map((p) => p.port).join()
    if (this.portsKey && this.portsKey !== portsKey) {
      this.growAt = this.now()
      this.growSpeed = REGROW
      for (const state of this.ports.values()) state.pulses = []
    }
    this.portsKey = portsKey

    // The thread that turns first takes the lane nearest the card, so none crosses another.
    const n = ports.length
    const order = ports.map((_, i) => i).sort((a, b) => ports[a].box.cy - ports[b].box.cy)
    const fanA = Math.max(120, ports[order[0]].box.cy - 150), fanB = fanA + 70
    const threads = ports.map(({ port, box }, i) => {
      const rank = order.indexOf(i)
      const braid = X + (5 * (n - 1)) / 2 - 5 * rank, lane = X + 4 + 8 * (n - 1) - 16 * rank
      const p = sample([
        { x: X, y: -14 }, { x: braid, y: Math.min(100, fanA - 10), s: true }, { x: braid, y: fanA },
        { x: lane, y: fanB, s: true }, { x: lane, y: box.cy }, { x: card.l - 1, y: box.cy },
      ], 22)
      let f = p.findIndex((q) => q.y >= box.cy - 22.5)
      if (f < 0) f = p.length - 1
      return { port, color: threadColor(port), p, f, n: p.length - 1 }
    })

    // The mark in the logo, in its 64-unit box, and the path that winds it: down the top shaft, across each bar in turn.
    const s = mark.w / 64
    const P = (x: number, y: number): Pt => ({ x: mark.l + x * s, y: mark.t + y * s })
    const wp = [[32, 4.6], [32, 8.6], [23.8, 16.4], [40.2, 16.4], [46.2, 24.2], [17.8, 24.2], [13.8, 32], [50.2, 32], [46.2, 39.8], [17.8, 39.8], [23.8, 47.6], [40.2, 47.6], [32, 55.4], [32, 59.4]]
      .map(([x, y]) => ({ ...P(x, y), r: 2 * s }))
    const wind = sample(wp, 2 * s, 1.2)
    const near = (q: Pt) => {
      let best = 0, bd = Infinity
      wind.forEach((w, i) => {
        const d = (w.x - q.x) ** 2 + (w.y - q.y) ** 2
        if (d < bd) { bd = d; best = i }
      })
      return best / (wind.length - 1)
    }
    const bars = [[0, 1], [2, 3], [4, 5], [6, 7], [8, 9], [10, 11], [12, 13]].map(([i, j], k) => {
      const f0 = i ? near(wp[i]) : 0
      return { a: wp[i], b: wp[j], f0, f1: Math.max(near(wp[j]), f0 + 0.01), w: 4.6 * s, accent: k === 3 }
    })

    const right = card.r + 48, shaft = P(32, 4.6), up = Math.max(8, mark.t - 30)
    const windInk = go ? sample([{ x: card.r + 1, y: go.cy }, { x: right, y: go.cy }, { x: right, y: up }, { x: shaft.x, y: up }, shaft], 26) : null
    const sendInk = go ? sample([{ x: card.r + 1, y: go.cy }, { x: right, y: go.cy }, { x: right + 70, y: go.cy - 70, s: true }, { x: rr.width + 30, y: go.cy - 70 }], 26) : null
    return (this.geo = { key, off: false, X, threads, windInk, sendInk, wind, bars })
  }

  private frame = () => {
    this.raf = requestAnimationFrame(this.frame)
    const root = this.root, canvas = this.canvas, ctx = this.ctx
    if (!root || !canvas || !ctx) return
    const t = this.now(), dt = clamp(t - this.last, 0.001, 0.05)
    this.last = t

    const dpr = Math.min(2, devicePixelRatio || 1), w = root.clientWidth, h = root.clientHeight
    if (w !== this.size.w || h !== this.size.h || dpr !== this.size.dpr) {
      this.size = { w, h, dpr }
      canvas.width = Math.round(w * dpr)
      canvas.height = Math.round(h * dpr)
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.clearRect(0, 0, w, h)

    const g = this.measure()
    if (!g || g.off) {
      root.dataset.threads = 'off'
      this.setWinding(false)
      return
    }
    root.dataset.threads = 'on'
    if (t - this.colorsAt > 0.5) {
      const cs = getComputedStyle(root)
      this.colors = { ring: cs.backgroundColor, ink: cs.color, accent: cs.getPropertyValue('--logo-accent').trim() || '#f7be2b' }
      this.colorsAt = t
    }
    const { ring, ink, accent } = this.colors
    const reduce = this.reduce

    this.plucks = this.plucks.filter((pl) => t - pl.t < 2.4)
    const grown = reduce ? Infinity : Math.max(0, (t - this.growAt) * this.growSpeed)
    const finale = this.finale
    const filling = !!finale && finale.failedAt === null

    const dot = (x: number, y: number, r: number, fill: string, alpha = 1, stroke?: string, sw = 0) => {
      ctx.globalAlpha = alpha
      ctx.beginPath()
      ctx.arc(x, y, r, 0, Math.PI * 2)
      ctx.fillStyle = fill
      ctx.fill()
      if (stroke) {
        ctx.lineWidth = sw
        ctx.strokeStyle = stroke
        ctx.stroke()
      }
      ctx.globalAlpha = 1
    }
    const glow = (x: number, y: number, r: number, color: string) => {
      dot(x, y, r * 3.4, color, 0.1)
      dot(x, y, r * 2, color, 0.18)
      dot(x, y, r, color, 1, ring, 3)
    }
    const stroke = (p: Pt[], a: number, b: number, color: string, width: number, alpha: number, dotted: boolean, off?: (i: number) => [number, number] | null) => {
      const i0 = Math.max(0, Math.floor(a)), i1 = Math.min(p.length - 1, Math.ceil(b))
      if (i1 <= i0) return
      ctx.beginPath()
      for (let i = i0; i <= i1; i++) {
        const o = off?.(i)
        const x = p[i].x + (o ? o[0] : 0), y = p[i].y + (o ? o[1] : 0)
        if (i === i0) ctx.moveTo(x, y)
        else ctx.lineTo(x, y)
      }
      ctx.lineCap = 'round'
      ctx.lineJoin = 'round'
      ctx.lineWidth = width
      ctx.strokeStyle = color
      ctx.globalAlpha = alpha
      ctx.setLineDash(dotted ? [1, 7] : [])
      ctx.stroke()
      ctx.setLineDash([])
      ctx.globalAlpha = 1
    }

    const card = root.querySelector('[data-thread-card]')
    g.threads.forEach((T, k) => {
      const state = this.state(T.port)
      const el = card?.querySelector<HTMLElement>(`[data-thread="${T.port}"]`)
      let typed = 0
      if (el instanceof HTMLInputElement) {
        if (state.refused !== undefined && el.value !== state.refused) state.refused = undefined
        typed = state.refused !== undefined ? 0 : el.readOnly && el.value ? 1 : clamp(el.value.length / 8, 0, 1)
        const invalid = el.getAttribute('aria-invalid') === 'true'
        if (invalid && !state.invalid) state.shakeAt = t
        state.invalid = invalid
      } else if (el) {
        typed = el.hasAttribute('data-thread-filled') || el.matches(':hover') || el.contains(document.activeElement) ? 1 : 0
      }
      const target = filling ? 1 : typed
      const was = state.fill
      const rate = reduce ? 1 : target < was && t - this.snapAt < 1 ? 0.22 : filling ? 0.2 : 0.12
      state.fill += (target - state.fill) * toward(rate, dt)
      if (Math.abs(target - state.fill) < 0.003) state.fill = target
      const fill = state.fill
      const drawn = Math.min(T.n, grown), solidEnd = T.f + (T.n - T.f) * fill
      if (fill === 1 && !state.full && drawn >= T.n && !reduce) this.ripples.push({ x: T.p[T.n].x, y: T.p[T.n].y, c: T.color, t })
      if ((fill === 1) !== state.full) {
        state.full = fill === 1
        root.querySelectorAll(`[data-thread-dot="${T.port}"]`).forEach((d) => d.toggleAttribute('data-full', state.full))
      }

      // Trunk samples take the cursor's plucks sideways; dotted (unrecorded) samples hang slack and sway.
      const snap = T.port === 'password' ? 12 * Math.exp(-(t - this.snapAt) * 3) : 0
      const off = (i: number): [number, number] | null => {
        if (reduce) return null
        if (i < T.f) {
          let dx = 0
          for (const pl of this.plucks) {
            const age = t - pl.t, gg = Math.exp(-(((T.p[i].y - pl.y) / 85) ** 2))
            if (gg > 0.01) dx += pl.a * gg * Math.exp(-age * 2.4) * Math.cos(age * 19 + k * 0.8)
          }
          return [dx * smooth((T.f - i) / 10), 0]
        }
        if (i > solidEnd) {
          const slack = smooth(Math.min(i - solidEnd, T.n - i) / 12)
          return [0, (4.5 + snap) * slack * Math.sin(T.p[i].x * 0.02 + t * 1.7 + k * 2)]
        }
        return null
      }
      const solidTo = Math.min(drawn, solidEnd)
      if (solidTo > 0.5) stroke(T.p, 0, solidTo, T.color, 2, 1, false, off)
      if (drawn > solidEnd) stroke(T.p, solidEnd, drawn, T.color, 2, 0.6, true, off)

      if (drawn < T.n && drawn > 1) {
        const q = T.p[Math.floor(drawn)]
        glow(q.x, q.y, 4, T.color)
      } else if (fill > was + 0.001 && fill < 1) {
        const q = T.p[Math.floor(solidEnd)]
        glow(q.x, q.y, 4, T.color)
      }
      if (drawn >= T.n) {
        const a = t - state.shakeAt, sh = a < 0.6 ? Math.sin(a * 46) * 5 * Math.exp(-a * 6) : 0
        const q = T.p[T.n], on = fill === 1
        dot(q.x + sh, q.y, 4, on ? T.color : ring, 1, on ? ring : T.color, on ? 2.5 : 1.5)
      }

      // Pulses: a short comet of dots on the recorded (solid) part only; it stops where recording stops.
      if (!reduce) {
        if (drawn >= T.f && t > state.next) {
          state.pulses.push(0)
          state.next = t + 1.1 + Math.random() * 2.2
        }
        const limit = Math.min(solidEnd, T.n) + 0.5, speed = 72 * dt * (filling ? 1.8 : 1)
        state.pulses = state.pulses.map((s) => s + speed).filter((s) => s < limit)
        for (const s of state.pulses) {
          for (let j = 4; j >= 0; j--) {
            const i = Math.floor(s - j * 2)
            if (i < 0 || i > T.n) continue
            const o = off(i), x = T.p[i].x + (o ? o[0] : 0), y = T.p[i].y + (o ? o[1] : 0)
            if (j) dot(x, y, 2.6 - j * 0.3, T.color, (1 - j / 5) * 0.9)
            else {
              dot(x, y, 6, T.color, 0.3)
              dot(x, y, 1.6, ring)
            }
          }
        }
      }
    })

    // The finale: the ink thread runs out of the card; on sign-in it climbs to the logo and winds the mark.
    let winding = false
    if (finale) {
      const path = finale.kind === 'wind' ? g.windInk : g.sendInk
      const w = t - finale.at
      const inkN = path ? path.length - 1 : 0
      const inkAt = clamp((w - 0.12) * INK, 0, inkN)
      const arrived = inkAt >= inkN
      let alpha = 1
      if (finale.failedAt !== null) alpha = 1 - (t - finale.failedAt) / 0.35
      if (finale.endedAt !== null) alpha = 1 - (t - finale.endedAt) / 0.3
      if (finale.done && arrived && finale.failedAt === null) {
        if (finale.kind === 'send' && finale.endedAt === null) {
          finale.endedAt = t
          finale.resolve?.()
        }
        if (finale.kind === 'wind' && finale.windAt === null) finale.windAt = t
      }
      if (path && inkAt > 0.5 && alpha > 0) {
        stroke(path, 0, inkAt, ink, 2.2, alpha, false)
        if (!arrived) glow(path[Math.floor(inkAt)].x, path[Math.floor(inkAt)].y, 5, ink)
      }
      if (finale.windAt !== null) {
        winding = true
        const wt = t - finale.windAt, wm = clamp(wt / WIND_SECONDS, 0, 1)
        stroke(g.wind, 0, wm * (g.wind.length - 1), ink, 1.4, clamp(1 - (wt - WIND_SECONDS) / 0.6, 0, 1), false)
        const ghost = 1 - 0.86 * smooth(wt / 0.2)
        ctx.lineCap = 'round'
        for (const b of g.bars) {
          ctx.strokeStyle = b.accent ? accent : ink
          ctx.lineWidth = b.w
          ctx.globalAlpha = ghost
          ctx.beginPath()
          ctx.moveTo(b.a.x, b.a.y)
          ctx.lineTo(b.b.x, b.b.y)
          ctx.stroke()
          const f = clamp((wm - b.f0) / (b.f1 - b.f0), 0, 1)
          if (f > 0) {
            ctx.globalAlpha = 1
            ctx.beginPath()
            ctx.moveTo(b.a.x, b.a.y)
            ctx.lineTo(b.a.x + (b.b.x - b.a.x) * f, b.a.y + (b.b.y - b.a.y) * f)
            ctx.stroke()
          }
        }
        if (wm >= 1) {
          const gl = Math.sin(Math.PI * clamp((wt - WIND_SECONDS) / 1.4, 0, 1)), b = g.bars[3]
          ctx.save()
          ctx.shadowColor = accent
          ctx.shadowBlur = 24 * gl
          ctx.strokeStyle = accent
          ctx.lineWidth = b.w
          ctx.beginPath()
          ctx.moveTo(b.a.x, b.a.y)
          ctx.lineTo(b.b.x, b.b.y)
          ctx.stroke()
          ctx.restore()
          if (wt > WIND_SECONDS + 0.15) finale.resolve?.()
        }
        ctx.globalAlpha = 1
      }
      if ((finale.failedAt !== null && t - finale.failedAt > 0.35) || (finale.endedAt !== null && t - finale.endedAt > 0.3)) this.finale = null
    }
    this.setWinding(winding)

    this.ripples = this.ripples.filter((rp) => t - rp.t < 0.9)
    for (const rp of this.ripples) {
      const a = (t - rp.t) / 0.9, e = 1 - Math.pow(1 - a, 4)
      ctx.beginPath()
      ctx.arc(rp.x, rp.y, 3 + 30 * e, 0, Math.PI * 2)
      ctx.strokeStyle = rp.c
      ctx.lineWidth = 2 * (1 - a) + 0.5
      ctx.globalAlpha = 0.7 * (1 - a)
      ctx.stroke()
      ctx.globalAlpha = 1
    }
  }
}

/** The one sign-in page on screen draws its threads with this; its forms play the finale through it. */
export const authThreads = new ThreadEngine()
