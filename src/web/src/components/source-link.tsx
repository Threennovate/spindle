import { Code } from 'lucide-react'
import { useSourceUrl } from '@/components/source-url'

export function SourceLink() {
  const sourceUrl = useSourceUrl()
  return (
    <a href={sourceUrl} target="_blank" rel="noreferrer" className="text-muted-foreground hover:text-foreground inline-flex items-center gap-2 text-sm">
      <Code className="size-4" aria-hidden="true" />
      Source code
    </a>
  )
}
