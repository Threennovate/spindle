import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { useCatalog } from '../catalog/use-catalog'
import { useWorkspace } from '../workspace'
import { changedEntrySummary, type EntryChange } from './entry-changed'

/**
 * Asks which version to keep when a save or delete found the entry changed elsewhere since it was loaded, e.g. on
 * another device while the editor was open. Nothing is overwritten until the person chooses.
 */
export function EntryChangedDialog({
  change,
  onKeepMine,
  onUseTheirs,
}: {
  change: EntryChange | null
  /** Saves or deletes anyway, over the other device's change. */
  onKeepMine: (change: EntryChange) => void
  /** Leaves the entry as the other device saved it. */
  onUseTheirs: (change: EntryChange) => void
}) {
  const { workspaceId, timeZone, hour12 } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const deleting = change?.action === 'delete'

  return (
    <AlertDialog open={change !== null} onOpenChange={(open) => !open && change && onUseTheirs(change)}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>This entry changed on another device</AlertDialogTitle>
          <AlertDialogDescription>
            {deleting ? 'It was changed after you opened it. It is now:' : 'It was changed after you opened it, so your changes weren’t saved yet. It is now:'}
          </AlertDialogDescription>
        </AlertDialogHeader>
        {change && (
          <p className="bg-muted/60 rounded-md px-3 py-2 text-sm">
            {changedEntrySummary(change.current, catalog.project(change.current.projectId)?.name, timeZone, hour12, new Date())}
          </p>
        )}
        <AlertDialogFooter>
          {/* Each choice closes the dialog through `change`, so closing doesn't also count as the other choice. */}
          <AlertDialogCancel
            onClick={(event) => {
              event.preventDefault()
              if (change) onUseTheirs(change)
            }}
          >
            {deleting ? 'Keep it' : 'Use theirs'}
          </AlertDialogCancel>
          <AlertDialogAction
            variant={deleting ? 'destructive' : 'default'}
            onClick={(event) => {
              event.preventDefault()
              if (change) onKeepMine(change)
            }}
          >
            {deleting ? 'Delete anyway' : 'Keep mine'}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
