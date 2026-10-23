import { describe, expect, it } from 'vitest'
import type { Me } from '@/api/types'
import { describeNewWorkspace } from './workspace'

const firma = { id: 'a', name: 'Firma', role: 'member' as const }
const own = { id: 'b', name: 'Ana Horvat', role: 'owner' as const }
const workspace = { id: 'one', organizationId: 'a', name: 'Firma', role: 'member' as const }

function me(newWorkspace: Me['newWorkspace'], organizations: Me['organizations'] = [firma]): Me {
  return { organizations, workspaces: [workspace], newWorkspace } as Me
}

describe('describeNewWorkspace', () => {
  it('tells an invited member it is their own, on its own trial', () => {
    expect(describeNewWorkspace(me({ allowed: true, organizationId: null, trialDays: 90 }), workspace)).toBe(
      "A space of your own, apart from Firma, with its own projects, clients, tags and team. It's not on Firma's plan: it starts its own 90-day free trial.",
    )
  })

  it('leaves the trial out on a self-hosted server', () => {
    expect(describeNewWorkspace(me({ allowed: true, organizationId: null }), workspace)).toBe(
      'A space of your own, apart from Firma, with its own projects, clients, tags and team. You own it and can invite people to it.',
    )
  })

  it('names the organization it goes in when that is not the current one', () => {
    expect(describeNewWorkspace(me({ allowed: true, organizationId: 'b' }, [firma, own]), workspace)).toBe(
      'A separate space in Ana Horvat, with its own projects, clients, tags and team. You become its admin.',
    )
  })

  it('says nothing more for the current organization', () => {
    expect(describeNewWorkspace(me({ allowed: true, organizationId: 'a' }), workspace)).toBe(
      'A separate space with its own projects, clients, tags and team. You become its admin.',
    )
  })
})
