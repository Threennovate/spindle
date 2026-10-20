import { cn } from 'cn'

/**
 * The Spindle mark: a spindle drawn from bars of time. Thread is wound on it from the shaft at the top to the shaft at
 * the bottom, and the longest bar, in marigold, is your hour, where the parallel work comes together.
 * The bars take the text colour, so the mark stands on any background. The same drawing as branding/build.mjs.
 */
const full = [
  { y: 2.3, w: 4.6, h: 8.6 },
  { y: 14.1, w: 21 },
  { y: 21.9, w: 33 },
  { y: 29.7, w: 41, accent: true },
  { y: 37.5, w: 33 },
  { y: 45.3, w: 21 },
  { y: 53.1, w: 4.6, h: 8.6 },
]

/** Three bars instead of five, for 24 px and smaller, where five would run together. */
const small = [
  { y: 4.3, w: 7, h: 10 },
  { y: 17.9, w: 26, h: 7 },
  { y: 28.5, w: 44, h: 7, accent: true },
  { y: 39.1, w: 26, h: 7 },
  { y: 49.7, w: 7, h: 10 },
]

export function LogoMark({ className, compact = false }: { className?: string; compact?: boolean }) {
  const bars = compact ? small : full
  const thickness = compact ? 7 : 4.6
  return (
    <svg viewBox="0 0 64 64" aria-hidden="true" className={cn('size-7 shrink-0', className)}>
      {bars.map(({ y, w, h = thickness, accent }) => (
        <rect key={y} x={32 - w / 2} y={y} width={w} height={h} rx={Math.min(w, h) / 2} className={accent ? 'fill-logo-accent' : 'fill-current'} />
      ))}
    </svg>
  )
}

/**
 * The mark with the name, set in Fraunces at weight 450 with fully soft terminals, which echo the round ends of the
 * bars. Only the name uses Fraunces; the rest of the interface stays in Geist. The mark sits a touch low, so it centres
 * on the letters rather than on the line.
 */
export function Logo({ className, size = 'md' }: { className?: string; size?: 'md' | 'lg' }) {
  return (
    <span className={cn('flex items-center', size === 'lg' ? 'gap-2.5 text-3xl' : 'gap-1.5 text-[17px]', className)}>
      <LogoMark className={cn('relative top-[0.04em]', size === 'lg' ? 'size-12' : 'size-9')} />
      <span className="font-brand leading-none font-[450] tracking-[-0.01em] [font-variation-settings:'SOFT'_100,'WONK'_0]">Spindle</span>
    </span>
  )
}
