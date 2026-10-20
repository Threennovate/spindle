import { describe, expect, it } from 'vitest'
import { safeRedirect } from './redirect'

describe('safeRedirect', () => {
  it.each(['/', '/reports', '/reports?range=week#top'])('allows the in-app path %s', (path) => {
    expect(safeRedirect(path)).toBe(path)
  })

  it.each(['https://evil.example', '//evil.example', '/\\evil.example', 'javascript:alert(1)', ''])(
    'replaces %s with the home page',
    (target) => {
      expect(safeRedirect(target)).toBe('/')
    },
  )

  it('goes home when there is no target', () => {
    expect(safeRedirect(undefined)).toBe('/')
  })
})
