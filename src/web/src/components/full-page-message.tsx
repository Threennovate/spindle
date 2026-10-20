import type { ReactNode } from 'react'
import { LogoMark } from '@/components/logo'

interface FullPageMessageProps {
  title: string
  description: string
  action?: ReactNode
}

/** Startup failures, unknown pages and unexpected errors. */
export function FullPageMessage({ title, description, action }: FullPageMessageProps) {
  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-4 p-6 text-center">
      <LogoMark className="size-10" />
      <div className="grid gap-1">
        <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
        <p className="text-muted-foreground max-w-sm text-sm">{description}</p>
      </div>
      {action}
    </main>
  )
}
