import { ChevronsUpDown } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { matchKeywords } from '../catalog/use-catalog'

interface Zone {
  id: string
  /** "Europe/Zagreb" as people read it: "Zagreb (Europe)". */
  label: string
  offset: string
}

/** The zone's current offset from UTC, e.g. "GMT+2". */
function offsetOf(zone: string, now: Date): string {
  return new Intl.DateTimeFormat('en-GB', { timeZone: zone, timeZoneName: 'shortOffset' }).formatToParts(now).find((part) => part.type === 'timeZoneName')?.value ?? ''
}

function describe(zone: string, now: Date): Zone {
  const parts = zone.split('/')
  const city = parts[parts.length - 1].replaceAll('_', ' ')
  const region = parts.slice(0, -1).join(' / ').replaceAll('_', ' ')
  return { id: zone, label: region ? `${city} (${region})` : city, offset: offsetOf(zone, now) }
}

/** Picks an IANA time zone, searchable by city, region or offset. */
export function TimeZonePicker({ id, value, onChange }: { id: string; value: string; onChange: (zone: string) => void }) {
  const [open, setOpen] = useState(false)
  const zones = useMemo(() => {
    const now = new Date()
    const all = Intl.supportedValuesOf('timeZone')
    // UTC is not in every browser's list, but people use it.
    return (all.includes('UTC') ? all : [...all, 'UTC']).map((zone) => describe(zone, now))
  }, [])
  const current = zones.find((zone) => zone.id === value) ?? describe(value, new Date())

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button id={id} variant="outline" role="combobox" aria-expanded={open} className="w-full justify-between font-normal sm:w-80">
          <span className="truncate">
            {current.label} <span className="text-muted-foreground">{current.offset}</span>
          </span>
          <ChevronsUpDown className="opacity-50" />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-80 p-0" align="start">
        <Command filter={matchKeywords}>
          <CommandInput placeholder="Search cities or regions" />
          <CommandList>
            <CommandEmpty>No time zone matches.</CommandEmpty>
            {zones.map((zone) => (
              <CommandItem
                key={zone.id}
                value={zone.id}
                keywords={[zone.id, zone.label, zone.offset]}
                data-checked={zone.id === value}
                onSelect={() => {
                  onChange(zone.id)
                  setOpen(false)
                }}
              >
                <span className="truncate">{zone.label}</span>
                <span className="text-muted-foreground ml-auto shrink-0 text-xs">{zone.offset}</span>
              </CommandItem>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}
