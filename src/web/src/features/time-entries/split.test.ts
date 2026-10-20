import { describe, expect, it } from 'vitest'
import {
  addPercent,
  allocate,
  apportion,
  describeEachPart,
  equalPercents,
  moveBoundary,
  partsOf,
  percentOfDuration,
  removePercent,
  setPercent,
  shareOneDescription,
  sharesRequest,
} from './split'

describe('apportion', () => {
  it('adds up exactly, the largest remainders first and the earlier on a tie', () => {
    expect(allocate(3600, [60, 40])).toEqual([2160, 1440])
    expect(allocate(10, [33, 33, 34])).toEqual([3, 3, 4])
    expect(allocate(1, [50, 50])).toEqual([1, 0])
    expect(allocate(0, [50, 50])).toEqual([0, 0])
  })

  it('keeps every part at the minimum', () => {
    expect(apportion([98, 1, 1], 100, 5)).toEqual([88, 6, 6])
  })
})

describe('equalPercents', () => {
  it('splits evenly, the first parts taking what is left over', () => {
    expect(equalPercents(2)).toEqual([50, 50])
    expect(equalPercents(3)).toEqual([34, 33, 33])
    expect(equalPercents(6)).toEqual([17, 17, 17, 17, 16, 16])
  })
})

describe('moveBoundary', () => {
  it('changes only the two parts beside the boundary', () => {
    expect(moveBoundary([34, 33, 33], 1, 80)).toEqual([34, 46, 20])
  })

  it('snaps to the step and keeps each part at least 1 percent', () => {
    expect(moveBoundary([50, 50], 0, 62, 5)).toEqual([60, 40])
    expect(moveBoundary([50, 50], 0, 100)).toEqual([99, 1])
    expect(moveBoundary([50, 50], 0, -20)).toEqual([1, 99])
  })
})

describe('setPercent', () => {
  it('makes room in the other parts in proportion', () => {
    expect(setPercent([50, 50], 0, 70)).toEqual([70, 30])
    expect(setPercent([50, 30, 20], 0, 60)).toEqual([60, 24, 16])
  })

  it('leaves at least 1 percent for each other part', () => {
    expect(setPercent([50, 30, 20], 2, 100)).toEqual([1, 1, 98])
  })
})

describe('adding and removing parts', () => {
  it('gives a new part an equal share', () => {
    expect(addPercent([50, 50])).toEqual([34, 33, 33])
    expect(addPercent([70, 30])).toEqual([47, 20, 33])
  })

  it('gives a removed part to the others in proportion', () => {
    expect(removePercent([50, 30, 20], 0)).toEqual([60, 40])
    expect(removePercent([60, 40], 1)).toEqual([100])
  })
})

describe('partsOf', () => {
  it('is the whole entry on its project when not split', () => {
    expect(partsOf({ projectId: 'p', taskId: null, isBillable: true, shares: [] })).toEqual([{ projectId: 'p', taskId: null, isBillable: true, percent: 100 }])
  })

  it('is the parts of a split entry', () => {
    const shares = [
      { projectId: 'a', taskId: null, isBillable: true, percent: 60, description: null },
      { projectId: 'b', taskId: 't', isBillable: false, percent: 40, description: null },
    ]
    expect(partsOf({ projectId: 'a', taskId: null, isBillable: true, shares })).toEqual([
      { projectId: 'a', taskId: null, isBillable: true, percent: 60 },
      { projectId: 'b', taskId: 't', isBillable: false, percent: 40 },
    ])
  })

  it('keeps the descriptions of parts described on their own', () => {
    const shares = [
      { projectId: 'a', taskId: null, isBillable: true, percent: 60, description: 'Fix the export' },
      { projectId: 'b', taskId: null, isBillable: false, percent: 40, description: '' },
    ]
    expect(partsOf({ projectId: 'a', taskId: null, isBillable: true, shares }).map((part) => part.description)).toEqual(['Fix the export', ''])
  })
})

describe('describing parts', () => {
  const parts = [
    { projectId: 'a', taskId: null, isBillable: true, percent: 30 },
    { projectId: 'b', taskId: null, isBillable: true, percent: 70 },
  ]

  it('gives the one description to the largest part and starts the others empty', () => {
    expect(describeEachPart(parts, ' Fix the export ').map((part) => part.description)).toEqual(['', 'Fix the export'])
  })

  it('joins the parts\' descriptions when they go back to one', () => {
    const described = [
      { ...parts[0], description: 'Review PR #42' },
      { ...parts[1], description: ' ' },
    ]
    expect(shareOneDescription(described)).toEqual({ parts, description: 'Review PR #42' })
  })

  it('sends the parts\' descriptions only when they are described on their own', () => {
    const single = { projectId: null, taskId: null, isBillable: false }
    expect(sharesRequest(parts, single)).toMatchObject({ describeParts: false, shares: [{ projectId: 'a' }, { projectId: 'b' }] })
    expect(sharesRequest(parts, single).shares[0]).not.toHaveProperty('description')
    expect(sharesRequest(describeEachPart(parts, 'Export'), single)).toMatchObject({
      describeParts: true,
      shares: [{ description: '' }, { description: 'Export' }],
    })
  })
})

it('reads a duration as a percentage of the entry', () => {
  expect(percentOfDuration(4320, 10800)).toBe(40)
})
