import { describe, expect, it } from 'vitest'
import type { Me } from '@/api/types'
import { firstStepsFor, isStepDone, setsUpOrganization, type FirstStepFacts } from './first-steps'

const nothing: FirstStepFacts = { projects: 0, others: 0, hasDesktopApp: false, alone: false }

describe('first steps', () => {
  it('gives whoever set up the organization three steps, and someone who joined only the desktop app', () => {
    expect(firstStepsFor(true)).toEqual(['projects', 'desktop', 'team'])
    expect(firstStepsFor(false)).toEqual(['desktop'])
  })

  it('counts a step done only from what is there or what the person said', () => {
    expect(['projects', 'desktop', 'team'].map((step) => isStepDone(step as never, nothing))).toEqual([false, false, false])
    expect(isStepDone('projects', { ...nothing, projects: 1 })).toBe(true)
    expect(isStepDone('desktop', { ...nothing, hasDesktopApp: true })).toBe(true)
    expect(isStepDone('team', { ...nothing, others: 1 })).toBe(true)
    expect(isStepDone('team', { ...nothing, alone: true })).toBe(true)
  })

  it('knows who set up the organization by their roles in it and in the workspace', () => {
    const me = (organizationRole: string, workspaceRole: string) =>
      ({
        organizations: [{ id: 'o', name: 'Acme', role: organizationRole }],
        workspaces: [{ id: 'w', organizationId: 'o', name: 'Acme', role: workspaceRole }],
      }) as unknown as Me

    expect(setsUpOrganization(me('owner', 'admin'), 'w')).toBe(true)
    expect(setsUpOrganization(me('admin', 'admin'), 'w')).toBe(true)
    expect(setsUpOrganization(me('member', 'admin'), 'w')).toBe(false)
    expect(setsUpOrganization(me('member', 'member'), 'w')).toBe(false)
  })
})
