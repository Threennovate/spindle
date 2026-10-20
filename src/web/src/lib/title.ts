import { useEffect } from 'react'

// The tab title is "<page> · Spindle", with the running timer in front so it can be read from any tab.
let page = 'Spindle'
let prefix: string | null = null

function apply() {
  document.title = prefix ? `${prefix} · ${page}` : page
}

export function usePageTitle(title: string) {
  useEffect(() => {
    page = `${title} · Spindle`
    apply()
  }, [title])
}

export function setTitlePrefix(value: string | null) {
  if (value !== prefix) {
    prefix = value
    apply()
  }
}
