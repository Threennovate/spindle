import { useQuery } from '@tanstack/react-query'
import { createFileRoute, redirect } from '@tanstack/react-router'
import { useState } from 'react'
import { meQuery } from '@/auth/me'
import { session } from '@/auth/session'
import { AuthLayout } from '@/components/auth-layout'
import { FullPageMessage } from '@/components/full-page-message'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { authorizationPreviewQuery, errorRedirect, readAuthorizationRequest, useAnswerAuthorization } from '@/features/agents/connect'
import { errorMessage } from '@/lib/error-message'
import { usePageTitle } from '@/lib/title'

/**
 * Where an agent such as Claude Code or Codex sends the person to let it use Spindle over MCP: the OAuth consent
 * page. The person chooses the workspace and whether the agent may change things; the answer goes back to the agent.
 */
export const Route = createFileRoute('/connect')({
  validateSearch: (search: Record<string, unknown>) => search,
  beforeLoad: ({ location }) => {
    if (!session.isSignedIn()) {
      throw redirect({ to: '/login', search: { redirect: location.href } })
    }
  },
  component: ConnectPage,
})

function ConnectPage() {
  usePageTitle('Connect an agent')
  const request = readAuthorizationRequest(Route.useSearch())
  const preview = useQuery(authorizationPreviewQuery(request))
  const me = useQuery(meQuery)
  const answer = useAnswerAuthorization()
  const [chosenWorkspace, setWorkspace] = useState<string>()
  const [allowWrite, setAllowWrite] = useState(true)

  if (preview.isPending || me.isPending) {
    return <FullPageMessage title="Connecting an agent…" description="One moment." />
  }

  if (preview.isError || me.isError) {
    const back = errorRedirect(preview.error)
    return (
      <FullPageMessage
        title="This agent can't connect"
        description={errorMessage(preview.error ?? me.error, "Spindle couldn't read the request. Start connecting again from the agent.")}
        action={back && <Button onClick={() => window.location.assign(back)}>Back to the agent</Button>}
      />
    )
  }

  const { clientName, redirectHost, localRedirect, clientHost, asksToWrite } = preview.data
  const workspaces = me.data.workspaces
  const workspaceId = chosenWorkspace ?? me.data.defaultWorkspaceId ?? workspaces[0]?.id
  const write = asksToWrite && allowWrite

  function respond(approve: boolean) {
    answer.mutate({ request, approve, workspaceId, allowWrite: write })
  }

  return (
    <AuthLayout
      title={`Connect ${clientName}`}
      description={`${clientName} asks to use Spindle as you, ${me.data.fullName}.`}
      footer={
        <>
          You will go back to <span className="text-foreground font-medium">{redirectHost}</span>
          {clientHost && <> · named by {clientHost}</>}
        </>
      }
    >
      <div className="grid gap-5">
        {localRedirect && (
          <Alert>
            <AlertDescription>
              It answers to an agent on this computer. Allow it only if you just started connecting {clientName} here yourself.
            </AlertDescription>
          </Alert>
        )}
        {answer.isError && (
          <Alert variant="destructive">
            <AlertDescription>{errorMessage(answer.error, "Couldn't connect the agent. Try again.")}</AlertDescription>
          </Alert>
        )}

        {workspaces.length > 1 && (
          <div className="grid gap-2">
            <Label htmlFor="connect-workspace">Workspace</Label>
            <Select value={workspaceId} onValueChange={setWorkspace}>
              <SelectTrigger id="connect-workspace" className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {workspaces.map((workspace) => (
                  <SelectItem key={workspace.id} value={workspace.id}>
                    {workspace.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}

        <div className="grid gap-2 text-sm">
          <p>It will be able to read your entries, projects, reports and synced agent activity{workspaces.length > 1 ? ' in this workspace' : ''}.</p>
          {asksToWrite && (
            <div className="flex items-start gap-3">
              <Checkbox id="connect-write" checked={allowWrite} onCheckedChange={(checked) => setAllowWrite(checked === true)} className="mt-0.5" />
              <div className="grid gap-0.5">
                <Label htmlFor="connect-write">Also let it describe entries, run the timer and propose time</Label>
                <p className="text-muted-foreground text-xs">New entries and changes to times, projects and splits only wait for you to review.</p>
              </div>
            </div>
          )}
          <p className="text-muted-foreground text-xs">It appears under Account, where you can sign it out at any time.</p>
        </div>

        <div className="grid gap-2">
          <Button size="lg" disabled={answer.isPending || !workspaceId} onClick={() => respond(true)}>
            {answer.isPending ? 'Connecting…' : `Allow ${clientName}`}
          </Button>
          <Button size="lg" variant="outline" disabled={answer.isPending} onClick={() => respond(false)}>
            Don't allow
          </Button>
        </div>
      </div>
    </AuthLayout>
  )
}
