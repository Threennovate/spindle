import { useNavigate } from '@tanstack/react-router'
import { Fragment, useEffect, useRef, useState } from 'react'
import { Kbd, Keys } from '@/components/kbd'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { requestDescriptionFocus } from '../time-entries/focus'
import { useTimerActions } from '../time-entries/timer-actions'
import { useWorkspace } from '../workspace'
import { SHOW_SHORTCUTS_EVENT } from './events'
import { GO_TO, SHORTCUTS } from './shortcuts'

/** Single-key shortcuts, as in Toggl. They stay quiet while the user types or a dialog or menu is open. */
export function KeyboardShortcuts() {
  const navigate = useNavigate()
  const { workspaceId } = useWorkspace()
  const timer = useTimerActions(workspaceId)
  const [helpOpen, setHelpOpen] = useState(false)

  // The listener is attached once; it reads the latest actions through this ref.
  const latest = useRef(timer)
  useEffect(() => {
    latest.current = timer
  })

  useEffect(() => {
    let awaitingGoTo: number | null = null

    function onKeyDown(event: KeyboardEvent) {
      if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.altKey || isTyping(event.target) || somethingIsOpen()) {
        return
      }

      if (awaitingGoTo !== null) {
        window.clearTimeout(awaitingGoTo)
        awaitingGoTo = null
        const to = GO_TO[event.key as keyof typeof GO_TO]
        if (to) {
          event.preventDefault()
          void navigate({ to })
        }
        return
      }

      switch (event.key) {
        case 'g':
          awaitingGoTo = window.setTimeout(() => (awaitingGoTo = null), 1000)
          break
        case 'n':
          event.preventDefault()
          requestDescriptionFocus()
          void navigate({ to: '/' })
          break
        case 's':
          event.preventDefault()
          latest.current.toggle()
          break
        case 'c':
          event.preventDefault()
          void latest.current.continueLatest()
          break
        case '?':
          event.preventDefault()
          setHelpOpen(true)
          break
      }
    }

    const show = () => setHelpOpen(true)
    window.addEventListener('keydown', onKeyDown)
    window.addEventListener(SHOW_SHORTCUTS_EVENT, show)
    return () => {
      window.removeEventListener('keydown', onKeyDown)
      window.removeEventListener(SHOW_SHORTCUTS_EVENT, show)
      if (awaitingGoTo !== null) {
        window.clearTimeout(awaitingGoTo)
      }
    }
  }, [navigate])

  return (
    <Dialog open={helpOpen} onOpenChange={setHelpOpen}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Keyboard shortcuts</DialogTitle>
          <DialogDescription>They work anywhere except while typing in a field. Every command is also in the palette.</DialogDescription>
        </DialogHeader>
        <dl className="grid grid-cols-[auto_1fr] items-center gap-x-4 gap-y-2 text-sm">
          {Object.values(SHORTCUTS).map((shortcut) => (
            <Fragment key={shortcut.keys.join('+')}>
              <dt>
                <Keys keys={shortcut.keys} />
              </dt>
              <dd className="text-muted-foreground">{shortcut.description}</dd>
            </Fragment>
          ))}
        </dl>
        <div className="grid gap-3 border-t pt-4 text-sm">
          <section>
            <h3 className="font-medium">In the palette</h3>
            <p className="text-muted-foreground text-pretty">
              One line says it all: <span className="text-foreground">Review PR @acme #review since 9:40</span>, or{' '}
              <span className="text-foreground">Standup yesterday 9-9:15</span>. You see what it saves before you press <Kbd>enter</Kbd>.
            </p>
          </section>
          <section>
            <h3 className="font-medium">Parallel work</h3>
            <p className="text-muted-foreground text-pretty">
              Two things at once? <Keys keys={SHORTCUTS.palette.keys} sequence={false} />, type <span className="text-foreground">split</span>, pick the other project, and{' '}
              <Keys keys={['←', '→']} compact sequence={false} /> set the shares.
            </p>
          </section>
        </div>
      </DialogContent>
    </Dialog>
  )
}

function isTyping(target: EventTarget | null): boolean {
  return target instanceof HTMLElement && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))
}

function somethingIsOpen(): boolean {
  return document.querySelector('[role="dialog"], [role="alertdialog"], [role="menu"], [role="listbox"]') !== null
}
