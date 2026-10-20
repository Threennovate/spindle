import { useQuery } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { usePermissions } from '../settings/queries'
import { tagsQuery, useSaveTag } from './queries'
import { matchKeywords } from './use-catalog'

interface TagPickerProps {
  workspaceId: string
  value: string[]
  onChange: (tagIds: string[]) => void
  /** The button that opens the picker. */
  children: ReactNode
  align?: 'start' | 'center' | 'end'
}

/** Picks any number of tags. Typing a name that does not exist offers to create it. */
export function TagPicker({ workspaceId, value, onChange, children, align = 'start' }: TagPickerProps) {
  const [search, setSearch] = useState('')
  const { data: tags = [] } = useQuery(tagsQuery(workspaceId))
  const saveTag = useSaveTag(workspaceId)
  const { canCreateTags } = usePermissions()

  const name = search.trim()
  const exists = tags.some((tag) => tag.name.toLowerCase() === name.toLowerCase())

  function toggle(tagId: string) {
    onChange(value.includes(tagId) ? value.filter((id) => id !== tagId) : [...value, tagId])
  }

  async function create() {
    try {
      const tag = await saveTag.mutateAsync({ name })
      onChange([...value, tag.id])
      setSearch('')
    } catch (error) {
      toast.error(error instanceof ApiError ? (Object.values(error.fieldErrors)[0]?.[0] ?? error.message) : "Couldn't create the tag.")
    }
  }

  return (
    <Popover onOpenChange={(open) => !open && setSearch('')}>
      <PopoverTrigger asChild>{children}</PopoverTrigger>
      <PopoverContent className="w-64 p-0" align={align}>
        <Command filter={matchKeywords}>
          <CommandInput placeholder={canCreateTags ? 'Search or create tags' : 'Search tags'} value={search} onValueChange={setSearch} />
          <CommandList>
            <CommandEmpty>{name ? 'No matching tags.' : canCreateTags ? 'No tags yet. Type a name to create one.' : 'No tags yet.'}</CommandEmpty>
            <CommandGroup>
              {tags.map((tag) => (
                <CommandItem
                  key={tag.id}
                  value={tag.id}
                  keywords={[tag.name]}
                  data-checked={value.includes(tag.id)}
                  onSelect={() => toggle(tag.id)}
                >
                  <span className="truncate">{tag.name}</span>
                </CommandItem>
              ))}
            </CommandGroup>
            {canCreateTags && name && !exists && (
              <CommandGroup forceMount>
                <CommandItem value="create-tag" forceMount disabled={saveTag.isPending} onSelect={() => void create()}>
                  <Plus />
                  Create tag “{name}”
                </CommandItem>
              </CommandGroup>
            )}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}
