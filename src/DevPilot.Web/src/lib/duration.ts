/** "1m 05s" / "42s" — a compact elapsed-time label. */
export function formatDuration(ms?: number | null): string {
  if (ms == null) return ""
  const total = Math.max(0, Math.round(ms / 1000))
  const minutes = Math.floor(total / 60)
  const seconds = total % 60
  return minutes > 0 ? `${minutes}m ${seconds.toString().padStart(2, "0")}s` : `${seconds}s`
}
