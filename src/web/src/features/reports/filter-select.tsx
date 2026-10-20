import { ChevronDown, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { cn } from 'cn'
import { Button } from '@/components/ui/button'
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList, CommandSeparator } from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { matchKeywords } from '../catalog/use-catalog'

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

interface FilterSelectProps {
  label: string
  sections: FilterSection[]
  selected: readonly string[]
  onChange: (selected: string[]) => void
  searchPlaceholder: string
  icon?: ReactNode
}

/** A filter button that opens a searchable list to pick any number of options; it shows how many are picked. */
export function FilterSelect({ label, sections, selected, onChange, searchPlaceholder, icon }: FilterSelectProps) {
  const [open, setOpen] = useState(false)
  const chosen = new Set(selected)
  const options = sections.flatMap((section) => section.options)
  const names = options.filter((option) => chosen.has(option.id)).map((option) => option.label)

  function toggle(id: string) {
    onChange(chosen.has(id) ? selected.filter((value) => value !== id) : [...selected, id])
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <div className={cn('flex items-center rounded-md', selected.length > 0 && 'bg-primary/10')}>
        <PopoverTrigger asChild>
          <Button
            variant="ghost"
            size="sm"
            className={cn(selected.length > 0 && 'text-primary-text hover:text-primary-text')}
            aria-label={selected.length ? `${label}: ${names.join(', ')}` : label}
          >
            {icon}
            {label}
            {selected.length > 0 && <span className="font-semibold tabular-nums">· {selected.length}</span>}
            <ChevronDown className="opacity-60" />
          </Button>
        </PopoverTrigger>
        {selected.length > 0 && (
          <Button variant="ghost" size="icon-sm" className="text-primary-text hover:text-primary-text -ml-1" aria-label={`Clear the ${label.toLowerCase()} filter`} onClick={() => onChange([])}>
            <X />
          </Button>
        )}
      </div>
      <PopoverContent className="w-72 p-0" align="start">
        <Command filter={matchKeywords}>
          <CommandInput placeholder={searchPlaceholder} />
          <CommandList>
            <CommandEmpty>Nothing matches.</CommandEmpty>
            {sections.map((section, index) => (
              <FilterGroup key={section.heading ?? index} section={section} chosen={chosen} onToggle={toggle} separated={index > 0} />
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
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
