import { ChevronDown, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { cn } from 'cn'
import { Button } from '@/components/ui/button'
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList, CommandSeparator } from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { matchKeywords } from '../catalog/use-catalog'
import type { FilterSection } from './filters'

interface FilterOptionsProps {
  sections: FilterSection[]
  selected: readonly string[]
  onChange: (selected: string[]) => void
  searchPlaceholder: string
}

/** A searchable list to pick any number of options from. */
export function FilterOptions({ sections, selected, onChange, searchPlaceholder }: FilterOptionsProps) {
  const chosen = new Set(selected)

  function toggle(id: string) {
    onChange(chosen.has(id) ? selected.filter((value) => value !== id) : [...selected, id])
  }

  return (
    <Command filter={matchKeywords}>
      <CommandInput placeholder={searchPlaceholder} />
      <CommandList>
        <CommandEmpty>Nothing matches.</CommandEmpty>
        {sections.map((section, index) => (
          <FilterGroup key={section.heading ?? index} section={section} chosen={chosen} onToggle={toggle} separated={index > 0} />
        ))}
      </CommandList>
    </Command>
  )
}

function FilterGroup({ section, chosen, onToggle, separated }: { section: FilterSection; chosen: Set<string>; onToggle: (id: string) => void; separated: boolean }) {
  if (section.options.length === 0) {
    return null
  }
  return (
    <>
      {separated && <CommandSeparator />}
      <CommandGroup heading={section.heading}>
        {section.options.map((option) => (
          <CommandItem
            key={option.id}
            value={option.id}
            keywords={[option.label, option.detail ?? '', ...(option.keywords ?? [])]}
            data-checked={chosen.has(option.id)}
            onSelect={() => onToggle(option.id)}
          >
            {option.color && <span aria-hidden="true" className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: option.color }} />}
            <span className="truncate">{option.label}</span>
            {option.detail && <span className="text-muted-foreground truncate text-xs">{option.detail}</span>}
          </CommandItem>
        ))}
      </CommandGroup>
    </>
  )
}

interface FilterChipProps {
  /** What the filter does, e.g. "Project · Acme API, Acme Web". */
  label: string
  /** The filter's name, for the button that clears it. */
  name: string
  /** Clears the filter; without it the chip opens a list instead. */
  onClear?: () => void
  /** What opens when the chip is clicked, to change the filter; it is given a way to close itself. */
  children: (close: () => void) => ReactNode
  contentClassName?: string
  icon?: ReactNode
  /** Tinted while the filter narrows the report down. */
  active?: boolean
  /** For screen readers, when the label alone doesn't say which filter it is. */
  'aria-label'?: string
}

/** A filter in use: it says what it does, opens to change it, and clears with ×. */
export function FilterChip({ label, name, onClear, children, contentClassName, icon, active = true, 'aria-label': ariaLabel }: FilterChipProps) {
  const [open, setOpen] = useState(false)
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <div className={cn('flex h-8 min-w-0 items-center rounded-lg', active ? 'bg-primary/15 text-primary-text' : 'bg-muted')}>
        <PopoverTrigger asChild>
          <Button
            variant="ghost"
            aria-label={ariaLabel}
            className={cn('h-8 max-w-72 min-w-0 hover:bg-transparent aria-expanded:bg-transparent', active && 'text-primary-text hover:text-primary-text aria-expanded:text-primary-text')}
          >
            {icon}
            <span className="truncate">{label}</span>
            {!onClear && <ChevronDown className="opacity-60" />}
          </Button>
        </PopoverTrigger>
        {onClear && (
          <Button variant="ghost" size="icon-xs" className="text-primary-text hover:text-primary-text hover:bg-primary/15 mr-1 -ml-1.5" aria-label={`Clear the ${name} filter`} onClick={onClear}>
            <X />
          </Button>
        )}
      </div>
      <PopoverContent className={cn('w-72 p-0', contentClassName)} align="start">
        {children(() => setOpen(false))}
      </PopoverContent>
    </Popover>
  )
}
