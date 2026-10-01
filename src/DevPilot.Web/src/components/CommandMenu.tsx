import { useEffect, useMemo, useRef, useState } from "react"
import { useNavigate } from "react-router-dom"
import { Search, CornerDownLeft, ArrowUp, ArrowDown } from "lucide-react"
import { getExecutions, getTasks } from "@/api"
import { Kbd } from "@/components/ui/primitives"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"

interface CommandItem {
  label: string
  hint: string
  href: string
  group: string
}

const MAX_TASK_ITEMS = 8
const MAX_EXECUTION_ITEMS = 6

function byNewest<T>(getDate: (item: T) => string) {
  return (a: T, b: T) => new Date(getDate(b)).getTime() - new Date(getDate(a)).getTime()
}

export function CommandMenu({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [query, setQuery] = useState("")
  const [active, setActive] = useState(0)
  const [dynamicItems, setDynamicItems] = useState<CommandItem[]>([])
  const inputRef = useRef<HTMLInputElement>(null)
  const navigate = useNavigate()
  const { activeWorkspace, activeWorkspaceId } = useWorkspace()

  const repoHint = activeWorkspace ? `${activeWorkspace.owner}/${activeWorkspace.repository}` : "No repository selected"

  // Navigation entries mirror the real sidebar; task / execution entries come from the active repository.
  const commandItems = useMemo<CommandItem[]>(
    () => [
      { label: "Go to Overview", hint: repoHint, href: "/", group: "Navigate" },
      { label: "Open Repository", hint: "Structure and analyzer state", href: "/projects", group: "Navigate" },
      { label: "View Tasks", hint: "Plan and approve changes", href: "/tasks", group: "Navigate" },
      { label: "Open Project Brain", hint: "Ask the codebase", href: "/brain", group: "Navigate" },
      { label: "View Executions", hint: "Runs and review", href: "/executions", group: "Navigate" },
      { label: "Impact map", hint: "How changes ripple across layers", href: "/architecture", group: "Navigate" },
      { label: "Insights", hint: "Success, repair, time and cost", href: "/insights", group: "Navigate" },
      { label: "Compare executions", hint: "Original vs retry", href: "/executions/compare", group: "Navigate" },
      ...dynamicItems,
    ],
    [repoHint, dynamicItems],
  )

  useEffect(() => {
    if (!open || !activeWorkspaceId) {
      setDynamicItems([])
      return
    }

    let cancelled = false
    void Promise.allSettled([
      getTasks({ repositoryWorkspaceId: activeWorkspaceId }),
      getExecutions(activeWorkspaceId),
    ]).then(([tasksResult, executionsResult]) => {
      if (cancelled) return
      const items: CommandItem[] = []
      if (tasksResult.status === "fulfilled") {
        tasksResult.value
          .slice()
          .sort(byNewest((t) => t.updatedAt))
          .slice(0, MAX_TASK_ITEMS)
          .forEach((t) =>
            items.push({
              label: `Open task: ${t.title}`,
              hint: `TASK-${t.id.slice(0, 8)}`,
              href: `/tasks/${t.id}`,
              group: "Tasks",
            }),
          )
      }
      if (executionsResult.status === "fulfilled") {
        executionsResult.value
          .slice()
          .sort(byNewest((e) => e.createdAt))
          .slice(0, MAX_EXECUTION_ITEMS)
          .forEach((e) =>
            items.push({
              label: `Open execution: ${e.taskTitle}`,
              hint: `EXEC-${e.id.slice(0, 8)}`,
              href: `/executions/${e.id}`,
              group: "Executions",
            }),
          )
      }
      setDynamicItems(items)
    })

    return () => {
      cancelled = true
    }
  }, [open, activeWorkspaceId])

  const results = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return commandItems
    return commandItems.filter(
      (c) => c.label.toLowerCase().includes(q) || c.hint.toLowerCase().includes(q) || c.group.toLowerCase().includes(q),
    )
  }, [query, commandItems])

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

  const grouped = results.reduce<Record<string, CommandItem[]>>((acc, item) => {
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
            placeholder="Search pages, tasks, executions…"
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
                    key={`${item.group}:${item.href}`}
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
