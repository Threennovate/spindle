import { describe, expect, it } from 'vitest'
import { fold, matchItem, matchLabel } from './match'

describe('fold', () => {
  it('drops accents and keeps the length', () => {
    expect(fold('Čakovec Đurđa')).toBe('cakovec durda')
    expect(fold('Čakovec Đurđa')).toHaveLength('Čakovec Đurđa'.length)
  })
})

describe('matchLabel', () => {
  it('ranks the whole label, then the first word, then other words, then initials, then inside a word', () => {
    const scores = [
      matchLabel('Reports', 'reports'),
      matchLabel('Reports overview', 'rep'),
      matchLabel('Go to Reports', 'rep'),
      matchLabel('Go to Reports', 'gtr'),
      matchLabel('Preports', 'rep'),
    ].map((match) => match?.score)
    expect(scores).toEqual([...scores].sort((a, b) => b! - a!))
    expect(new Set(scores).size).toBe(5)
  })

  it('finds every typed word, in any order', () => {
    expect(matchLabel('Website redesign', 'red web')).not.toBeNull()
    expect(matchLabel('Website redesign', 'red app')).toBeNull()
  })

  it('ignores accents and case', () => {
    expect(matchLabel('Čakovec', 'CAK')?.ranges).toEqual([[0, 3]])
  })

  it('treats a step from lower to upper case as a new word', () => {
    expect(matchLabel('TigerTrail', 'tr')?.ranges).toEqual([[5, 7]])
  })

  it('returns the ranges to highlight, merged', () => {
    expect(matchLabel('Go to Reports', 'gtr')?.ranges).toEqual([
      [0, 1],
      [3, 4],
      [6, 7],
    ])
    expect(matchLabel('Design review', 'des rev')?.ranges).toEqual([
      [0, 3],
      [7, 10],
    ])
  })

  it('reads a label ending in an ellipsis as the whole label', () => {
    expect(matchLabel('Tags…', 'tags')?.score).toBe(matchLabel('Tags', 'tags')?.score)
  })

  it('matches everything for an empty query', () => {
    expect(matchLabel('Anything', '  ')).toEqual({ score: 0, ranges: [] })
  })
})

describe('matchItem', () => {
  it('finds an item by a keyword, a little below the same match in its label', () => {
    const byKeyword = matchItem('Stop timer', ['end', 'finish'], 'finish')
    const byLabel = matchItem('Finish', [], 'finish')
    expect(byKeyword).not.toBeNull()
    expect(byKeyword!.ranges).toEqual([])
    expect(byKeyword!.score).toBeLessThan(byLabel!.score)
  })

  it('finds words spread over the label and its keywords', () => {
    const match = matchItem('Acme website', ['Start timer'], 'start acme')
    expect(match).not.toBeNull()
    expect(match!.ranges).toEqual([[0, 4]])
  })
})
