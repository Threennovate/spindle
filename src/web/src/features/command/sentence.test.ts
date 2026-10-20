import { TZDate } from '@date-fns/tz'
import { format } from 'date-fns'
import { describe, expect, it } from 'vitest'
import vectors from '../../../../../tests/vectors/entry-sentence.json'
import { mentionAt, readSentence, resolveSentence, withoutMention, type SentenceChip } from './sentence'

interface Vector {
  text: string
  chips?: SentenceChip[]
  options?: { hour12?: boolean; dateFormat?: string }
  dayEnds?: Record<string, string[]>
  description: string
  projectId?: string
  taskId?: string
  clientId?: string
  tagIds?: string[]
  billable?: boolean
  start: string | null
  stop: string | null
  timeText: string
}

const { timeZone } = vectors
const local = (wallClock: string) => {
  const [day, time] = wallClock.split('T')
  const [y, mo, d] = day.split('-').map(Number)
  const [h, mi, s] = time.split(':').map(Number)
  return new Date(new TZDate(y, mo - 1, d, h, mi, s, timeZone).getTime())
}
const wallClock = (instant: Date) => format(new TZDate(instant.getTime(), timeZone), "yyyy-MM-dd'T'HH:mm:ss")
const now = local(vectors.now)

describe('the shared entry sentence vectors', () => {
  it.each((vectors.cases as Vector[]).map((vector) => [`${vector.text}${vector.options ? ` ${JSON.stringify(vector.options)}` : ''}`, vector] as const))(
    '%s',
    (_, vector) => {
      const options = { hour12: false, dateFormat: 'dd.MM.yyyy', ...vector.options }
      const sentence = readSentence(vector.text, vector.chips ?? [], options)
      expect({
        description: sentence.description,
        projectId: sentence.projectId,
        taskId: sentence.taskId,
        clientId: sentence.clientId,
        tagIds: sentence.tagIds,
        billable: sentence.billable,
        timeText: sentence.timeText,
      }).toEqual({
        description: vector.description,
        projectId: vector.projectId ?? null,
        taskId: vector.taskId ?? null,
        clientId: vector.clientId ?? null,
        tagIds: vector.tagIds ?? [],
        billable: vector.billable ?? false,
        timeText: vector.timeText,
      })

      const span = resolveSentence(sentence.time, {
        now,
        timeZone,
        hour12: options.hour12,
        dayEnds: (day) => (vector.dayEnds?.[day] ?? []).map(local),
      })
      expect(vector.start === null ? span.start.getTime() : wallClock(span.start)).toEqual(vector.start === null ? now.getTime() : vector.start)
      expect(span.stop && wallClock(span.stop)).toEqual(vector.stop)
    },
  )
})

describe('mentions at the caret', () => {
  it('finds the project or tag being typed', () => {
    expect(mentionAt('Review @ac', 10)).toEqual({ kind: '@', query: 'ac', start: 7, end: 10 })
    expect(mentionAt('Review #rev now', 11)).toEqual({ kind: '#', query: 'rev', start: 7, end: 11 })
    expect(mentionAt('Review @', 8)).toEqual({ kind: '@', query: '', start: 7, end: 8 })
  })

  it('ignores signs inside words and text away from the caret', () => {
    expect(mentionAt('mail ana@x.com', 14)).toBeNull()
    expect(mentionAt('Fix #482 later', 14)).toBeNull()
  })

  it('takes the mention out, leaving one space', () => {
    expect(withoutMention('Review @ac', { kind: '@', query: 'ac', start: 7, end: 10 })).toEqual({ text: 'Review ', caret: 7 })
    expect(withoutMention('Review @ac since 9', { kind: '@', query: 'ac', start: 7, end: 10 })).toEqual({ text: 'Review since 9', caret: 7 })
    expect(withoutMention('@ac Review', { kind: '@', query: 'ac', start: 0, end: 3 })).toEqual({ text: 'Review', caret: 0 })
  })
})
