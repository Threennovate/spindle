import type { Me } from '@/api/types'

export type FirstStep = 'projects' | 'desktop' | 'team'

/**
 * The steps that get someone new to value. Whoever set up the organization adds projects, gets the desktop app and
 * brings the team; someone who joined finds all that done, and only the desktop app is theirs to get.
 */
export function firstStepsFor(sets: boolean): FirstStep[] {
  return sets ? ['projects', 'desktop', 'team'] : ['desktop']
}

/** What the workspace and the person's own choices say about each step. Nothing is guessed. */
export interface FirstStepFacts {
  projects: number
  /** Other members and pending invitations. */
  others: number
  /** Downloaded the desktop app here, or said they have it. */
  hasDesktopApp: boolean
  /** Said they track alone, so the team step is done. */
  alone: boolean
}

export function isStepDone(step: FirstStep, facts: FirstStepFacts): boolean {
  switch (step) {
    case 'projects':
      return facts.projects > 0
    case 'desktop':
      return facts.hasDesktopApp
    case 'team':
      return facts.others > 0 || facts.alone
  }
}

/**
 * Whether the person set up the current workspace's organization, as far as the web can tell: they own or administer
 * both. They see the full first steps; everyone else sees only the desktop app.
 */
export function setsUpOrganization(me: Me, workspaceId: string): boolean {
  const workspace = me.workspaces.find((w) => w.id === workspaceId)
  const organization = me.organizations.find((o) => o.id === workspace?.organizationId)
  return workspace?.role === 'admin' && organization !== undefined && organization.role !== 'member'
}
