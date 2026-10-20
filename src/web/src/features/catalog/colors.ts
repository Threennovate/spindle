/** Project colors to pick from, chosen to stay readable as dots and labels in light and dark mode. */
export const PROJECT_COLORS = [
  '#0B83D9',
  '#9E5BD9',
  '#D94182',
  '#E36A00',
  '#BF7000',
  '#2DA608',
  '#06A893',
  '#C9806B',
  '#465BB3',
  '#990099',
  '#C7AF14',
  '#566614',
  '#D92B2B',
  '#525266',
  '#991102',
] as const

/** A color for a new project, varied so consecutive projects are told apart. */
export function nextProjectColor(existingCount: number): string {
  return PROJECT_COLORS[existingCount % PROJECT_COLORS.length]
}
