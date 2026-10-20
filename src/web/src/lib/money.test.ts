import { describe, expect, it } from 'vitest'
import { amountText, formatRate, parseAmount } from './money'

describe('parseAmount', () => {
  it('reads blank as no amount', () => {
    expect(parseAmount('')).toBeNull()
    expect(parseAmount('   ')).toBeNull()
  })

  it('reads decimals with a point or a comma', () => {
    expect(parseAmount('60')).toBe(60)
    expect(parseAmount(' 72.50 ')).toBe(72.5)
    expect(parseAmount('72,5')).toBe(72.5)
    expect(parseAmount('0')).toBe(0)
  })

  it('rejects what is not an amount', () => {
    expect(parseAmount('abc')).toBeUndefined()
    expect(parseAmount('-5')).toBeUndefined()
    expect(parseAmount('1.2.3')).toBeUndefined()
    expect(parseAmount('Infinity')).toBeUndefined()
  })
})

describe('amounts as text', () => {
  it('round-trips through a text field', () => {
    expect(amountText(null)).toBe('')
    expect(amountText(72.5)).toBe('72.5')
    expect(parseAmount(amountText(72.5))).toBe(72.5)
  })

  it('formats hourly rates in the workspace currency', () => {
    expect(formatRate(60, 'EUR')).toBe('€60.00/h')
    expect(formatRate(72.5, 'USD')).toBe('$72.50/h')
  })
})
