/**
 * Reads an amount someone typed: blank means none, and a comma works as the decimal point.
 * Returns `undefined` when the text is not an amount.
 */
export function parseAmount(text: string): number | null | undefined {
  const trimmed = text.trim()
  if (trimmed === '') {
    return null
  }
  const amount = Number(trimmed.replace(',', '.'))
  return Number.isFinite(amount) && amount >= 0 ? amount : undefined
}

/** An amount as it goes into a text field. */
export function amountText(amount: number | null | undefined): string {
  return amount == null ? '' : String(amount)
}

const formatters = new Map<string, Intl.NumberFormat>()

export function formatMoney(amount: number, currency: string): string {
  let formatter = formatters.get(currency)
  if (!formatter) {
    formatter = new Intl.NumberFormat('en-GB', { style: 'currency', currency, currencyDisplay: 'narrowSymbol' })
    formatters.set(currency, formatter)
  }
  return formatter.format(amount)
}

export function formatRate(amount: number, currency: string): string {
  return `${formatMoney(amount, currency)}/h`
}
