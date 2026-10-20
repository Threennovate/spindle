export interface FilterOption {
  id: string
  label: string
  /** Shown after the label, e.g. a project's client. */
  detail?: string
  color?: string
  keywords?: string[]
}

export interface FilterSection {
  heading?: string
  options: FilterOption[]
}

/** The names of the picked options: the first two, and how many more. */
export function pickedNames(sections: FilterSection[], selected: readonly string[]): string {
  const options = new Map(sections.flatMap((section) => section.options).map((option) => [option.id, option.label]))
  const names = selected.map((id) => options.get(id)).filter((name): name is string => !!name)
  if (names.length === 0) {
    return `${selected.length} picked`
  }
  return names.length > 2 ? `${names.slice(0, 2).join(', ')} +${names.length - 2}` : names.join(', ')
}
