import { describe, expect, it } from 'vitest'
import { applyMentions, insertMention, mentionAt, mentionText, parseMentions } from './mentions'

const projects = [
  { id: 'anchor', name: 'AnchorNet', isBillable: true },
  { id: 'frame', name: 'FrameIt', isBillable: false },
  { id: 'acme', name: 'Acme', isBillable: true },
  { id: 'acme-web', name: 'Acme website', isBillable: true },
]

describe('parseMentions', () => {
  it('gives the text after a mention to its project, up to the next one', () => {
    expect(parseMentions('Review PR #42; @FrameIt fix the PDF export, @anchornet deploy', projects)).toMatchObject({
      lead: 'Review PR #42',
      segments: [
        { projectId: 'frame', text: 'fix the PDF export' },
        { projectId: 'anchor', text: 'deploy' },
      ],
    })
  })

  it('reads only whole names, the longest first, and leaves the rest as text', () => {
    expect(parseMentions('@Acme website copy', projects).segments).toEqual([{ projectId: 'acme-web', text: 'copy' }])
    expect(parseMentions('@Acme copy', projects).segments).toEqual([{ projectId: 'acme', text: 'copy' }])
    expect(parseMentions('@FrameItx and mail@FrameIt and @Framelt', projects).mentions).toEqual([])
  })
})

describe('mentionAt', () => {
  it('finds the mention being typed at the caret', () => {
    expect(mentionAt('Review @Fra', 11)).toEqual({ start: 7, query: 'Fra' })
    expect(mentionAt('Review @Acme web', 16)).toEqual({ start: 7, query: 'Acme web' })
    expect(mentionAt('mail@Fra', 8)).toBeNull()
    expect(mentionAt('@FrameIt fix;', 13)).toBeNull()
  })

  it('puts the chosen name in its place', () => {
    expect(insertMention('Review @fra and more', { start: 7, query: 'fra' }, 'FrameIt')).toEqual({ text: 'Review @FrameIt and more', caret: 16 })
  })
})

describe('applyMentions', () => {
  const single = { projectId: 'anchor', taskId: null, isBillable: true }

  it('splits evenly between the entry project and those mentioned, each with its own text', () => {
    const result = applyMentions(parseMentions('Review PR #42 @FrameIt fix export', projects), single, projects)
    expect(result).toEqual({
      description: 'Review PR #42; fix export',
      projectId: 'anchor',
      taskId: null,
      isBillable: true,
      parts: [
        { projectId: 'anchor', taskId: null, isBillable: true, percent: 50, description: 'Review PR #42' },
        { projectId: 'frame', taskId: null, isBillable: false, percent: 50, description: 'fix export' },
      ],
    })
  })

  it('puts an entry without a project on what was mentioned, and one project is no split', () => {
    const none = { projectId: null, taskId: null, isBillable: false }
    expect(applyMentions(parseMentions('Planning @FrameIt', projects), none, projects)).toMatchObject({
      projectId: 'frame',
      description: 'Planning',
      parts: null,
    })
    expect(applyMentions(parseMentions('@AnchorNet Review @FrameIt export', projects), none, projects)?.parts?.map((p) => [p.projectId, p.description])).toEqual([
      ['anchor', 'Review'],
      ['frame', 'export'],
    ])
  })

  it('keeps the shares of a split and gives a new project room', () => {
    const parts = [
      { projectId: 'anchor', taskId: null, isBillable: true, percent: 70, description: 'Old words' },
      { projectId: 'frame', taskId: null, isBillable: false, percent: 30, description: '' },
    ]
    const result = applyMentions(parseMentions('@FrameIt export @Acme call', projects), { ...single, parts }, projects)
    expect(result?.parts?.map((p) => [p.projectId, p.percent, p.description])).toEqual([
      ['anchor', 47, 'Old words'],
      ['frame', 20, 'export'],
      ['acme', 33, 'call'],
    ])
  })

  it('changes only the description without mentions', () => {
    expect(applyMentions(parseMentions('Just words', projects), single, projects)).toEqual({ ...single, description: 'Just words', parts: null })
  })
})

describe('mentionText', () => {
  it('writes a split described per project as mentions, which read back the same', () => {
    const parts = [
      { projectId: 'anchor', taskId: null, isBillable: true, percent: 60, description: 'Review PR #42' },
      { projectId: 'frame', taskId: null, isBillable: false, percent: 40, description: 'Fix the export' },
    ]
    const text = mentionText(parts, projects)!
    expect(text).toBe('@AnchorNet Review PR #42 @FrameIt Fix the export')
    const base = { projectId: 'anchor', taskId: null, isBillable: true, parts }
    expect(applyMentions(parseMentions(text, projects), base, projects, true)?.parts).toEqual(parts)
    // Leaving a project's mention out of the whole text leaves its part without a description.
    expect(applyMentions(parseMentions('@AnchorNet Review PR #42', projects), base, projects, true)?.parts?.map((p) => p.description)).toEqual(['Review PR #42', ''])
  })
})
