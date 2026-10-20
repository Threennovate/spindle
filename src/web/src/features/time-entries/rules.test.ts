import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/problem'
import type { Project } from '@/api/types'
import { isDetailsError, missingDetails, missingInParts, requiresTask } from './rules'

const none = { requireDescription: false, requireProject: false, requireTask: false, requireTag: false }
const all = { requireDescription: true, requireProject: true, requireTask: true, requireTag: true }

function project(tasks: { isDone: boolean }[]): Project {
  return {
    id: 'p1',
    name: 'Website',
    clientId: null,
    color: '#000000',
    isPrivate: false,
    isBillable: false,
    separateBilledTime: false,
    billingMode: 'asTracked',
    dailyHours: null,
    estimateSeconds: null,
    archivedAt: null,
    trackedSeconds: 0,
    hourlyRate: null,
    canManage: false,
    tasks: tasks.map((task, index) => ({ id: `t${index}`, projectId: 'p1', name: `Task ${index}`, isDone: task.isDone, estimateSeconds: null, trackedSeconds: 0, agreementId: null })),
  }
}

describe('missingDetails', () => {
  it('asks for nothing by default', () => {
    expect(missingDetails(none, { description: '', projectId: null, taskId: null, tagIds: [] }, undefined)).toEqual({})
  })

  it('lists every required detail that is missing', () => {
    expect(Object.keys(missingDetails(all, { description: '  ', projectId: null, taskId: null, tagIds: [] }, undefined))).toEqual([
      'description',
      'projectId',
      'tagIds',
    ])
  })

  it('is satisfied when the details are there', () => {
    const details = { description: 'Design review', projectId: 'p1', taskId: 't0', tagIds: ['tag'] }
    expect(missingDetails(all, details, project([{ isDone: false }]))).toEqual({})
  })

  it('requires a task only on projects with open tasks', () => {
    const details = { description: 'Design review', projectId: 'p1', taskId: null, tagIds: ['tag'] }
    expect(missingDetails(all, details, project([]))).toEqual({})
    expect(missingDetails(all, details, project([{ isDone: true }]))).toEqual({})
    expect(missingDetails(all, details, project([{ isDone: true }, { isDone: false }]))).toEqual({ taskId: 'A task is required on this project.' })
    expect(requiresTask(none, project([{ isDone: false }]))).toBe(false)
  })
})

describe('parts described on their own', () => {
  const parts = [
    { projectId: 'p1', taskId: null, description: 'Fix the export' },
    { projectId: 'p2', taskId: null, description: ' ' },
  ]

  it('need a description each instead of the entry', () => {
    const settings = { ...none, requireDescription: true }
    expect(missingDetails(settings, { description: '', tagIds: [], shares: parts }, undefined)).toEqual({})
    expect(missingInParts(settings, parts, () => undefined)).toEqual({ 1: 'A description is required in this workspace.' })
    expect(missingInParts(none, parts, () => undefined)).toEqual({})
  })
})

describe('isDetailsError', () => {
  it('recognizes refusals about the entry’s details', () => {
    expect(isDetailsError(new ApiError(400, { title: 'Invalid', errors: { projectId: ['A project is required in this workspace.'] } } as never))).toBe(true)
    expect(isDetailsError(new ApiError(400, { title: 'Invalid', errors: { start: ['Time before … is locked.'] } } as never))).toBe(false)
    expect(isDetailsError(new ApiError(500))).toBe(false)
    expect(isDetailsError(new TypeError('Failed to fetch'))).toBe(false)
  })
})
