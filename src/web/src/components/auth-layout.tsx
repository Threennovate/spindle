import { useState, type ReactNode } from 'react'
import { cn } from 'cn'
import { Logo } from '@/components/logo'
import { AppDownload } from '@/components/app-download'
import { AuthThreadsCanvas } from '@/components/auth-threads'
import { SourceLink } from '@/components/source-link'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'

interface AuthLayoutProps {
  title: string
  description: string
  children: ReactNode
  footer: ReactNode
}

/**
 * The sign-in pages: the card, with the website's project threads running into its fields (see auth/threads.ts).
 * While a sign-in winds the mark, the threads draw it, so the logo's own mark steps aside.
 */
export function AuthLayout({ title, description, children, footer }: AuthLayoutProps) {
  const [winding, setWinding] = useState(false)
  return (
    <main className="group/auth bg-sidebar dark:bg-background relative flex min-h-svh flex-col items-center justify-center gap-6 overflow-hidden p-6">
      <AuthThreadsCanvas onWinding={setWinding} />
      <div data-thread-mark className={cn(winding && '[&_svg]:opacity-0')}>
        <Logo size="lg" />
      </div>
      <Card data-thread-card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle className="text-xl">{title}</CardTitle>
          <CardDescription>{description}</CardDescription>
        </CardHeader>
        <CardContent>{children}</CardContent>
      </Card>
      <p className="text-muted-foreground text-sm">{footer}</p>
      <AppDownload />
      <SourceLink />
    </main>
  )
}
