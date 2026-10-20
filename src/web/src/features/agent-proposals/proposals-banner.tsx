import { useQuery } from '@tanstack/react-query'
import { Sparkles } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useWorkspace } from '../workspace'
import { proposedBy, waitingSummary } from './proposal'
import { agentProposalsQuery } from './queries'
import { ReviewDialog } from './review-dialog'

/**
 * "✦ Claude Code proposed 2 new entries and 1 change.": shown while the person's agents have proposals waiting in the
 * workspace, whichever computer or service they came from. Review opens them; nothing is saved before.
 */
export function ProposalsBanner() {
  const { workspaceId } = useWorkspace()
  const { data } = useQuery(agentProposalsQuery(workspaceId))
  const [open, setOpen] = useState(false)
  const pending = data?.pending ?? []

  if (pending.length === 0 && !open) {
    return null
  }

  return (
    <>
      {pending.length > 0 && (
        <section
          aria-label="Proposals from your agents"
          className="border-primary/25 bg-primary/5 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border px-3 py-2"
        >
          <p className="flex min-w-0 flex-1 items-center gap-2 text-sm">
            <Sparkles aria-hidden="true" className="text-primary-text size-4 shrink-0" />
            <span>
              <span className="font-semibold">
                {proposedBy(pending)} proposed {waitingSummary(pending)}.
              </span>
              <span className="text-muted-foreground"> Nothing is saved until you add it.</span>
            </span>
          </p>
          <Button type="button" size="sm" onClick={() => setOpen(true)}>
            Review…
          </Button>
        </section>
      )}
      <ReviewDialog open={open} pending={pending} onClose={() => setOpen(false)} />
    </>
  )
}
