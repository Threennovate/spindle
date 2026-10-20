import { Link, useRouterState } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { Building2, CalendarDays, ChartColumn, CircleUser, Code, CreditCard, FolderKanban, HandCoins, House, Keyboard, List, LogOut, Menu, Search, Settings, Sheet as SheetIcon, ShieldCheck, Tag, Users } from 'lucide-react'
import { cn } from 'cn'
import { useState, type ReactNode } from 'react'
import type { Me } from '@/api/types'
import { session } from '@/auth/session'
import { Logo } from '@/components/logo'
import { AppDownload } from '@/components/app-download'
import { useSourceUrl } from '@/components/source-url'
import { WorkspaceSwitcher } from '@/components/workspace-switcher'
import { Keys } from '@/components/kbd'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet'
import { ConfirmEmailBanner } from '@/features/account/confirm-email-banner'
import { openCommandPalette } from '@/features/command/events'
import { showKeyboardShortcuts } from '@/features/shortcuts/events'
import { SHORTCUTS } from '@/features/shortcuts/shortcuts'
import { serverQuery } from '@/features/subscriptions/queries'
import { useReadOnly } from '@/features/subscriptions/read-only'
import { ReadOnlyBanner } from '@/features/subscriptions/read-only-banner'
import { trialLabel } from '@/features/subscriptions/trial'
import { TrialBanner } from '@/features/subscriptions/trial-banner'
import { SidebarTimer } from '@/features/time-entries/running-timer'
import { useWorkspace } from '@/features/workspace'

const NAVIGATION = [
  {
    heading: 'Track',
    items: [
      { to: '/', label: 'Home', icon: House },
      { to: '/entries', label: 'Entries', icon: List },
      { to: '/calendar', label: 'Calendar', icon: CalendarDays },
      { to: '/timesheet', label: 'Timesheet', icon: SheetIcon },
    ],
  },
  {
    heading: 'Analyze',
    items: [
      { to: '/reports', label: 'Reports', icon: ChartColumn },
      { to: '/profitability', label: 'Profitability', icon: HandCoins, adminOnly: true },
    ],
  },
  {
    heading: 'Manage',
    items: [
      { to: '/projects', label: 'Projects', icon: FolderKanban },
      { to: '/clients', label: 'Clients', icon: Building2 },
      { to: '/tags', label: 'Tags', icon: Tag },
      { to: '/team', label: 'Team', icon: Users },
      { to: '/settings', label: 'Settings', icon: Settings },
      { to: '/plan', label: 'Plan', icon: CreditCard, planOnly: true },
    ],
  },
] as const

export function AppShell({ me, children }: { me: Me; children: ReactNode }) {
  const [menuOpen, setMenuOpen] = useState(false)
  // Home and Entries show the whole timer, so the sidebar's would only repeat it.
  const onTimerPage = useRouterState({ select: (state) => state.location.pathname === '/' || state.location.pathname === '/entries' })

  return (
    <div className="flex min-h-svh">
      <aside className="bg-sidebar text-sidebar-foreground border-sidebar-border sticky top-0 hidden h-svh w-60 shrink-0 flex-col border-r md:flex">
        <div className="flex h-14 items-center px-4">
          <Logo />
        </div>
        <div className="px-2 pb-2">
          <WorkspaceSwitcher />
        </div>
        <Navigation />
        <div className="mt-auto">{!onTimerPage && <SidebarTimer />}</div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="bg-background/95 supports-backdrop-filter:bg-background/80 sticky top-0 z-30 flex h-14 items-center gap-2 border-b px-4 backdrop-blur md:px-8">
          <Sheet open={menuOpen} onOpenChange={setMenuOpen}>
            <SheetTrigger asChild>
              <Button variant="ghost" size="icon" className="md:hidden" aria-label="Open menu">
                <Menu />
              </Button>
            </SheetTrigger>
            <SheetContent side="left" className="bg-sidebar w-64 p-0">
              <SheetHeader className="h-14 justify-center px-4">
                <SheetTitle>
                  <Logo />
                </SheetTitle>
                <SheetDescription className="sr-only">Navigation</SheetDescription>
              </SheetHeader>
              <div className="px-2">
                <WorkspaceSwitcher onSwitched={() => setMenuOpen(false)} />
              </div>
              <Navigation onNavigate={() => setMenuOpen(false)} />
            </SheetContent>
          </Sheet>
          <Logo className="md:hidden" />
          <PaletteButton />
          <div className="md:ml-auto">
            <AppDownload />
          </div>
          <div>
            <UserMenu me={me} />
          </div>
        </header>
        <ConfirmEmailBanner me={me} />
        <ReadOnlyBanner />
        <TrialBanner />
        <main className="flex-1 px-4 py-6 md:px-8 md:py-8">{children}</main>
      </div>
    </div>
  )
}

/** Opens the command palette, and shows its shortcut so people learn it. */
function PaletteButton() {
  return (
    <>
      <button
        type="button"
        onClick={openCommandPalette}
        className="text-muted-foreground hover:text-foreground hover:bg-muted/70 bg-muted/40 focus-visible:ring-ring/50 hidden h-9 w-72 items-center gap-2 rounded-lg border px-3 text-sm transition-colors outline-none focus-visible:ring-3 md:flex"
      >
        <Search className="size-4" aria-hidden="true" />
        <span>Search or run a command…</span>
        <Keys keys={SHORTCUTS.palette.keys} sequence={false} className="ml-auto" />
      </button>
      <Button variant="ghost" size="icon" className="ml-auto md:hidden" aria-label="Search or run a command" onClick={openCommandPalette}>
        <Search />
      </Button>
    </>
  )
}

function Navigation({ onNavigate }: { onNavigate?: () => void }) {
  const { isAdmin } = useWorkspace()
  // The plan is the organization's, so it is for its owners and admins, and only on the hosted service.
  const { canManage, organization } = useReadOnly()
  const hosted = useQuery(serverQuery).data?.hosted === true
  // A quiet reminder of the trial, all along, for those who choose the plan.
  const trial = hosted && canManage && organization?.access === 'full' ? trialLabel(organization.trialEndsAt) : null
  return (
    <nav aria-label="Main" className="grid gap-4 px-2 py-2">
      {NAVIGATION.map((section) => (
        <div key={section.heading} className="grid gap-1">
          <p className="text-muted-foreground px-3 pb-1 text-xs font-medium tracking-wide uppercase">{section.heading}</p>
          {section.items.filter((item) => (isAdmin || !('adminOnly' in item)) && (!('planOnly' in item) || (hosted && canManage))).map(({ to, label, icon: Icon }) => (
            <Link
              key={to}
              to={to}
              onClick={onNavigate}
              className="text-sidebar-foreground/80 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground flex items-center gap-2 rounded-md px-3 py-2 text-sm font-medium transition-colors"
              activeProps={{ className: 'bg-sidebar-accent text-sidebar-accent-foreground', 'aria-current': 'page' }}
              // Every page is under Home's `/`, so Home is current only on itself.
              activeOptions={{ exact: to === '/' }}
            >
              <Icon className="size-4" />
              {label}
              {to === '/plan' && trial && (
                <span className={cn('ml-auto text-xs font-normal', trial.soon ? 'text-primary-text' : 'text-muted-foreground')}>
                  <span className="sr-only">, trial: </span>
                  {trial.text}
                </span>
              )}
            </Link>
          ))}
        </div>
      ))}
    </nav>
  )
}

function UserMenu({ me }: { me: Me }) {
  const sourceUrl = useSourceUrl()
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon-lg" className="rounded-full" aria-label="Account">
          <Avatar>
            <AvatarFallback>{initials(me.fullName)}</AvatarFallback>
          </Avatar>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuLabel className="grid gap-0.5 font-normal">
          <span className="text-foreground truncate text-sm font-medium">{me.fullName}</span>
          <span className="text-muted-foreground truncate text-xs">{me.email}</span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <Link to="/account">
            <CircleUser />
            Account settings
          </Link>
        </DropdownMenuItem>
        {me.isOperator && (
          <DropdownMenuItem asChild>
            <Link to="/operator">
              <ShieldCheck />
              Operator
            </Link>
          </DropdownMenuItem>
        )}
        <DropdownMenuItem onSelect={openCommandPalette}>
          <Search />
          Command palette
          <Keys keys={SHORTCUTS.palette.keys} sequence={false} className="ml-auto" />
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={showKeyboardShortcuts}>
          <Keyboard />
          Keyboard shortcuts
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <a href={sourceUrl} target="_blank" rel="noreferrer">
            <Code />
            Source code
          </a>
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void session.signOut()}>
          <LogOut />
          Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function initials(fullName: string): string {
  const words = fullName.trim().split(/\s+/)
  const letters = words.length > 1 ? words[0][0] + words[words.length - 1][0] : words[0].slice(0, 2)
  return letters.toUpperCase()
}
