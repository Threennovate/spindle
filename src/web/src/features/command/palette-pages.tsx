import { ActionsPage, NamePage, ProjectPage, StartTimePage, TagsPage, type NamePageOptions, type ProjectPageOptions } from './pages'
import { SplitPage } from './split-page'
import { createSplitState } from './split-state'
import type { PaletteItem, PalettePage } from './types'
import type { TimeEntry } from '@/api/types'
import type { SplitPart } from '../time-entries/split'

/** A page that picks a project or a task, grouped by client, where a new project can be created on the spot. */
export function projectPage(options: ProjectPageOptions): PalettePage {
  return { title: options.title, placeholder: 'Search projects, tasks or clients', render: (query) => <ProjectPage {...options} query={query} /> }
}

export function tagsPage(): PalettePage {
  return { title: 'Tags', placeholder: 'Search or create tags', render: (query) => <TagsPage query={query} /> }
}

export function startTimePage(): PalettePage {
  return { title: 'Start time', placeholder: 'A time such as 9:40, or -15 for 15 minutes ago', render: (query) => <StartTimePage query={query} /> }
}

export function namePage(options: NamePageOptions): PalettePage {
  return { title: options.title, placeholder: options.placeholder, render: (query) => <NamePage {...options} query={query} /> }
}

export function actionsPage(item: PaletteItem): PalettePage {
  return { title: item.label, placeholder: 'Search actions', render: (query) => <ActionsPage item={item} query={query} /> }
}

/** A page to split an entry's time between projects, starting from these parts. */
export function splitPage(entry: TimeEntry, parts: SplitPart[]): PalettePage {
  const state = createSplitState(entry, parts)
  return {
    title: `Split ${entry.description ? `“${entry.description}”` : 'the entry'}`,
    placeholder: 'A share for the selected part, e.g. 60',
    render: (query) => <SplitPage state={state} query={query} />,
    onKeyDown: state.onKeyDown,
  }
}
