import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/problem'
import { toFormErrors } from './form-errors'

const FIELDS = ['email', 'password'] as const

describe('toFormErrors', () => {
  it('puts validation errors next to their fields and the rest on top', () => {
    const error = new ApiError(400, {
      title: 'One or more validation errors occurred.',
      errors: { email: ['Email is already taken.', 'Second message'], timeZone: ['Unknown time zone.'] },
    } as ConstructorParameters<typeof ApiError>[1])

    expect(toFormErrors(error, FIELDS)).toEqual({ email: 'Email is already taken.', form: 'Unknown time zone.' })
  })

  it('shows the problem title for other failures', () => {
    const error = new ApiError(401, { title: 'Invalid email or password.' })

    expect(toFormErrors(error, FIELDS)).toEqual({ form: 'Invalid email or password.' })
  })

  it('adds the problem detail when there is one', () => {
    const error = new ApiError(429, { title: 'Too many failed sign-in attempts.', detail: 'Try again later.' })

    expect(toFormErrors(error, FIELDS)).toEqual({ form: 'Too many failed sign-in attempts. Try again later.' })
  })

  it('falls back to a generic message without problem details', () => {
    expect(toFormErrors(new ApiError(502), FIELDS)).toEqual({ form: 'Something went wrong. Please try again.' })
  })
})
