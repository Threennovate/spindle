import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { CircleCheck, Circle, Download, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { api } from '@/api/client'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'
import { useAppDownloads } from '@/lib/app-downloads'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from 'cn'
import { usePersistentState } from '@/lib/use-persistent-state'
import { projectsQuery } from '../catalog/queries'
import { ProjectDialog } from '../projects/project-dialog'
import { usePermissions } from '../settings/queries'
import { serverQuery } from '../subscriptions/queries'
import { useReadOnly } from '../subscriptions/read-only'
import { InviteDialog } from '../team/invite-dialog'
import { invitationsQuery, membersQuery } from '../team/queries'
import { useWorkspace } from '../workspace'
import { firstStepsFor, isStepDone, setsUpOrganization, type FirstStep, type FirstStepFacts } from './first-steps'

const DONE = ['no', 'yes'] as const

/** Where people find the desktop apps when this server has no files of its own, e.g. a self-hosted one. */
const DOWNLOAD_PAGE = 'https://spindletrack.com/download'

/**
 * What someone new does first, above the timer, until they put it away. Whoever set up the organization adds
 * projects, gets the desktop app and invites the team; someone who joined only gets the desktop app.
 */
export function FirstStepsCard() {
  const { me, workspaceId } = useWorkspace()
  const { readOnly } = useReadOnly()
  if (!me.firstSteps || readOnly) return null
  return <FirstSteps key={workspaceId} sets={setsUpOrganization(me, workspaceId)} />
}

function FirstSteps({ sets }: { sets: boolean }) {
  const { me, workspace, workspaceId } = useWorkspace()
  const queryClient = useQueryClient()
  const projects = useQuery({ ...projectsQuery(workspaceId, true), enabled: sets })
  const members = useQuery({ ...membersQuery(workspaceId), enabled: sets })
  const invitations = useQuery({ ...invitationsQuery(workspaceId), enabled: sets })
  // Choices only the person can make, kept in this browser. Lost in a private window, which only shows a step again.
  const [desktop, setDesktop] = usePersistentState(`spindle:first-steps:${me.id}:desktop`, 'no', DONE)
  const [team, setTeam] = usePersistentState(`spindle:first-steps:${me.id}:team`, 'no', DONE)
  const hide = useMutation({
    mutationFn: () => unwrap(api.PUT('/api/me/first-steps', { body: { hidden: true } })),
    onSuccess: (updated) => queryClient.setQueryData(meQuery.queryKey, updated),
    onError: () => toast.error("Couldn't hide the first steps. Try again."),
  })

  // Steps that depend on the workspace wait for it, rather than flashing as not done.
  if (sets && (projects.isPending || members.isPending || invitations.isPending)) return null

  const facts: FirstStepFacts = {
    projects: projects.data?.length ?? 0,
    others: Math.max(0, (members.data?.length ?? 1) - 1) + (invitations.data?.length ?? 0),
    hasDesktopApp: desktop === 'yes',
    alone: team === 'yes',
  }
  const steps = firstStepsFor(sets)
  const done = steps.filter((step) => isStepDone(step, facts)).length
  const finished = done === steps.length

  return (
    <Card>
      <CardHeader>
        <CardTitle>{finished ? 'You’re set up' : 'First steps'}</CardTitle>
        <CardDescription>
          {finished
            ? 'Start the timer when you start working.'
            : steps.length > 1
              ? `${done} of ${steps.length} done`
              : 'One thing to do on your computer.'}
        </CardDescription>
        <CardAction>
          {finished ? (
            <Button size="sm" variant="outline" onClick={() => hide.mutate()} disabled={hide.isPending}>
              Hide
            </Button>
          ) : (
            <Button size="icon-sm" variant="ghost" aria-label="Hide first steps" onClick={() => hide.mutate()} disabled={hide.isPending}>
              <X />
            </Button>
          )}
        </CardAction>
      </CardHeader>
      {!finished && (
        <CardContent>
          <ol className="grid gap-1">
            {steps.map((step) => (
              <Step key={step} step={step} done={isStepDone(step, facts)} onHasDesktopApp={() => setDesktop('yes')} onAlone={() => setTeam('yes')} workspace={workspace} />
            ))}
          </ol>
        </CardContent>
      )}
    </Card>
  )
}

interface StepProps {
  step: FirstStep
  done: boolean
  workspace: { id: string; name: string }
  onHasDesktopApp: () => void
  onAlone: () => void
}

function Step({ step, done, workspace, onHasDesktopApp, onAlone }: StepProps) {
  switch (step) {
    case 'projects':
      return <ProjectsStep done={done} workspaceId={workspace.id} />
    case 'desktop':
      return <DesktopStep done={done} onHasIt={onHasDesktopApp} />
    case 'team':
      return <TeamStep done={done} workspace={workspace} onAlone={onAlone} />
  }
}

function StepRow({ done, title, description, children }: { done: boolean; title: string; description: ReactNode; children?: ReactNode }) {
  const Icon = done ? CircleCheck : Circle
  return (
    <li className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-2 rounded-lg px-1 py-2 md:grid-cols-[auto_1fr_auto] md:items-center">
      <Icon aria-hidden="true" className={cn('mt-0.5 size-5 self-start', done ? 'text-primary-text' : 'text-muted-foreground/60')} />
      <div className="grid gap-0.5">
        <p className={cn('text-sm font-medium', done && 'text-muted-foreground')}>
          {title}
          {done && <span className="sr-only"> (done)</span>}
        </p>
        {!done && <div className="text-muted-foreground text-sm text-pretty">{description}</div>}
      </div>
      {!done && children && <div className="col-start-2 flex flex-wrap items-center gap-2 md:col-start-3 md:justify-end">{children}</div>}
    </li>
  )
}

function ProjectsStep({ done, workspaceId }: { done: boolean; workspaceId: string }) {
  const { canCreateProjects, isAdmin } = usePermissions()
  const [creating, setCreating] = useState(false)
  return (
    <StepRow
      done={done}
      title="Add your projects"
      description={
        <>
          Time is kept per project and client.{' '}
          {isAdmin && (
            <>
              Coming from another tracker?{' '}
              <Link to="/import" className="text-foreground underline underline-offset-4">
                Import from Toggl Track, Clockify or Harvest
              </Link>
              .
            </>
          )}
        </>
      }
    >
      {canCreateProjects && (
        <>
          <Button size="sm" variant="outline" onClick={() => setCreating(true)}>
            New project
          </Button>
          <ProjectDialog workspaceId={workspaceId} open={creating} onOpenChange={setCreating} />
        </>
      )}
    </StepRow>
  )
}

function DesktopStep({ done, onHasIt }: { done: boolean; onHasIt: () => void }) {
  const downloads = useAppDownloads()
  const hosted = useQuery(serverQuery).data?.hosted === true
  return (
    <StepRow
      done={done}
      title="Get the desktop app"
      description={
        <>
          The timer, always at hand. It also reads Claude Code and Codex logs on your computer and suggests how to split
          time when you work on several projects at once. The logs stay on your computer.
          {!hosted && <> Sign in with this server’s address, {window.location.origin}.</>}
        </>
      }
    >
      {downloads.length > 0 ? (
        downloads.map((app) => (
          <Button key={app.platform} asChild size="sm" variant="outline">
            <a href={app.url} onClick={onHasIt}>
              <Download />
              {app.label}
            </a>
          </Button>
        ))
      ) : (
        <Button asChild size="sm" variant="outline">
          <a href={DOWNLOAD_PAGE} target="_blank" rel="noreferrer" onClick={onHasIt}>
            <Download />
            Download
          </a>
        </Button>
      )}
      <Button size="sm" variant="ghost" onClick={onHasIt}>
        I have it
      </Button>
    </StepRow>
  )
}

function TeamStep({ done, workspace, onAlone }: { done: boolean; workspace: { id: string; name: string }; onAlone: () => void }) {
  const [inviting, setInviting] = useState(false)
  return (
    <StepRow done={done} title="Invite your team" description="Everyone tracks their own time. Reports show it together.">
      <Button size="sm" variant="outline" onClick={() => setInviting(true)}>
        Invite people
      </Button>
      <Button size="sm" variant="ghost" onClick={onAlone}>
        Just me
      </Button>
      <InviteDialog workspaceId={workspace.id} workspaceName={workspace.name} open={inviting} onOpenChange={setInviting} />
    </StepRow>
  )
}
