/** Hands a file to the browser to save, under the given name. */
export function saveFile(file: Blob, name: string) {
  const url = URL.createObjectURL(file)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  document.body.append(link)
  link.click()
  link.remove()
  // Revoked later, so the browser has certainly started the download.
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

/** The file name a server suggested, e.g. from `attachment; filename=a.csv; filename*=UTF-8''a.csv`. */
export function suggestedFileName(disposition: string | null): string | null {
  if (!disposition) {
    return null
  }
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)
  if (encoded) {
    return decodeURIComponent(encoded[1])
  }
  return /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? null
}
