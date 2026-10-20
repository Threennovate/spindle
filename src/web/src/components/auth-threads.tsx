import { useEffect, useRef, type CSSProperties } from 'react'
import { authThreads, threadColor } from '@/auth/threads'

/** The canvas the threads are drawn on, over the whole sign-in page (its parent). */
export function AuthThreadsCanvas({ onWinding }: { onWinding: (on: boolean) => void }) {
  const ref = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const canvas = ref.current
    const root = canvas?.parentElement
    if (!canvas || !root) return
    authThreads.attach(root, canvas, onWinding)
    return () => authThreads.detach()
  }, [onWinding])
  return <canvas ref={ref} aria-hidden="true" className="pointer-events-none absolute inset-0 z-10 size-full" />
}

/** The field's thread colour beside its label, hollow until the field is filled in. Shown only while the threads are. */
export function ThreadDot({ port }: { port: string }) {
  return (
    <span
      data-thread-dot={port}
      aria-hidden="true"
      style={{ '--thread': threadColor(port) } as CSSProperties}
      className="hidden size-[7px] shrink-0 rounded-full shadow-[inset_0_0_0_1.5px_var(--thread)] transition-colors group-data-[threads=on]/auth:inline-block data-full:bg-(--thread)"
    />
  )
}
