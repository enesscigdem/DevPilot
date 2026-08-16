import { useEffect, useMemo, useRef, useState } from "react"
import { useNavigate } from "react-router-dom"
import { Search, CornerDownLeft, ArrowUp, ArrowDown } from "lucide-react"
import { commandItems } from "@/data/mock"
import { Kbd } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"

export function CommandMenu({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [query, setQuery] = useState("")
  const [active, setActive] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)
  const navigate = useNavigate()

  const results = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return commandItems
    return commandItems.filter(
      (c) => c.label.toLowerCase().includes(q) || c.hint.toLowerCase().includes(q) || c.group.toLowerCase().includes(q),
    )
  }, [query])

  useEffect(() => {
    if (open) {
      setQuery("")
      setActive(0)
      requestAnimationFrame(() => inputRef.current?.focus())
    }
  }, [open])

  useEffect(() => {
    setActive(0)
  }, [query])

  if (!open) return null

  const grouped = results.reduce<Record<string, typeof commandItems>>((acc, item) => {
    ;(acc[item.group] ??= []).push(item)
    return acc
  }, {})

  const flat = Object.values(grouped).flat()

  const select = (href: string) => {
    navigate(href)
    onClose()
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown") {
      e.preventDefault()
      setActive((a) => Math.min(a + 1, flat.length - 1))
    } else if (e.key === "ArrowUp") {
      e.preventDefault()
      setActive((a) => Math.max(a - 1, 0))
    } else if (e.key === "Enter") {
      e.preventDefault()
      if (flat[active]) select(flat[active].href)
    } else if (e.key === "Escape") {
      onClose()
    }
  }

  let runningIndex = -1

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center px-4 pt-[12vh]"
      onMouseDown={onClose}
    >
      <div className="absolute inset-0 bg-foreground/20 backdrop-blur-[1px]" />
      <div
        className="animate-fade-rise relative w-full max-w-[560px] overflow-hidden rounded-[var(--radius-lg)] border border-border-strong bg-surface shadow-[var(--shadow-lg)]"
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={onKeyDown}
      >
        <div className="flex items-center gap-2.5 border-b border-border px-3.5">
          <Search className="h-4 w-4 shrink-0 text-subtle-foreground" strokeWidth={2} />
          <input
            ref={inputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search commands, tasks, files…"
            className="h-12 w-full bg-transparent text-sm text-foreground outline-none placeholder:text-subtle-foreground"
          />
          <Kbd>Esc</Kbd>
        </div>

        <div className="max-h-[52vh] overflow-y-auto py-1.5">
          {flat.length === 0 && (
            <div className="px-4 py-8 text-center text-sm text-subtle-foreground">No matches for "{query}"</div>
          )}
          {Object.entries(grouped).map(([group, items]) => (
            <div key={group} className="px-1.5 pb-1">
              <div className="tech-label px-2.5 py-1.5">{group}</div>
              {items.map((item) => {
                runningIndex++
                const idx = runningIndex
                const isActive = idx === active
                return (
                  <button
                    key={item.label}
                    onMouseEnter={() => setActive(idx)}
                    onClick={() => select(item.href)}
                    className={cn(
                      "flex w-full items-center justify-between gap-3 rounded-[var(--radius-md)] px-2.5 py-2 text-left transition-colors",
                      isActive ? "bg-primary-soft" : "hover:bg-surface-3",
                    )}
                  >
                    <span className={cn("text-[13px] font-medium", isActive ? "text-primary" : "text-foreground")}>
                      {item.label}
                    </span>
                    <span className="font-mono text-[11px] text-subtle-foreground">{item.hint}</span>
                  </button>
                )
              })}
            </div>
          ))}
        </div>

        <div className="flex items-center gap-4 border-t border-border bg-surface-2 px-3.5 py-2 text-[11px] text-subtle-foreground">
          <span className="flex items-center gap-1.5">
            <ArrowUp className="h-3 w-3" />
            <ArrowDown className="h-3 w-3" /> navigate
          </span>
          <span className="flex items-center gap-1.5">
            <CornerDownLeft className="h-3 w-3" /> open
          </span>
          <span className="ml-auto font-mono">DevPilot</span>
        </div>
      </div>
    </div>
  )
}
