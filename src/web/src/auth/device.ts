/** A readable name for this browser, e.g. "Safari on macOS", shown in the list of signed-in devices. */
export function describeBrowser(userAgent: string): string {
  const browser =
    /Edg(e|A|iOS)?\//.test(userAgent) ? 'Edge'
    : /OPR\//.test(userAgent) ? 'Opera'
    : /Firefox\/|FxiOS\//.test(userAgent) ? 'Firefox'
    : /Chrome\/|CriOS\//.test(userAgent) ? 'Chrome'
    : /Safari\//.test(userAgent) ? 'Safari'
    : 'Browser'

  const os =
    /iPhone|iPad|iPod/.test(userAgent) ? 'iOS'
    : /Android/.test(userAgent) ? 'Android'
    : /Mac OS X/.test(userAgent) ? 'macOS'
    : /Windows/.test(userAgent) ? 'Windows'
    : /Linux/.test(userAgent) ? 'Linux'
    : null

  return os ? `${browser} on ${os}` : browser
}
