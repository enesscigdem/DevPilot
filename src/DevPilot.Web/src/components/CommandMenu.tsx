import { useEffect, useMemo, useRef, useState } from "react"
import { useNavigate } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { setLang } from "@/i18n"
import { Search, CornerDownLeft, ArrowUp, ArrowDown } from "lucide-react"
import { getExecutions, getTasks } from "@/api"
import { Kbd } from "@/components/ui/primitives"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"

interface CommandItem {
  label: string
  hint: string
  href?: string
  action?: () => void
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
  const { t, i18n } = useTranslation()

  const repoHint = activeWorkspace ? `${activeWorkspace.owner}/${activeWorkspace.repository}` : t("command.noRepository")

  // Navigation entries mirror the real sidebar; task / execution entries come from the active repository.
  const commandItems = useMemo<CommandItem[]>(
    () => [
      { label: t("command.goOverview"), hint: repoHint, href: "/", group: t("command.groupNavigate") },
      { label: t("command.openRepository"), hint: t("command.repositoryHint"), href: "/projects", group: t("command.groupNavigate") },
      { label: t("command.viewTasks"), hint: t("command.tasksHint"), href: "/tasks", group: t("command.groupNavigate") },
      { label: t("command.openBrain"), hint: t("command.brainHint"), href: "/brain", group: t("command.groupNavigate") },
      { label: t("command.viewExecutions"), hint: t("command.executionsHint"), href: "/executions", group: t("command.groupNavigate") },
      { label: t("nav.impactMap"), hint: t("command.impactHint"), href: "/architecture", group: t("command.groupNavigate") },
      { label: t("nav.insights"), hint: t("command.insightsHint"), href: "/insights", group: t("command.groupNavigate") },
      { label: t("nav.compareExecutions"), hint: t("command.compareHint"), href: "/executions/compare", group: t("command.groupNavigate") },
      { label: t("nav.aiModels"), hint: t("command.modelsHint"), href: "/models", group: t("command.groupSettings") },
      { label: t("nav.automation"), hint: t("command.automationHint"), href: "/automation", group: t("command.groupSettings") },
      {
        label: i18n.language === "tr" ? t("command.switchToEnglish") : t("command.switchToTurkish"),
        hint: t("command.languageHint"),
        action: () => setLang(i18n.language === "tr" ? "en" : "tr"),
        group: t("command.groupSettings"),
      },
      ...dynamicItems,
    ],
    [repoHint, dynamicItems, t, i18n.language],
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
          .sort(byNewest((tk) => tk.updatedAt))
          .slice(0, MAX_TASK_ITEMS)
          .forEach((tk) =>
            items.push({
              label: t("command.openTask", { title: tk.title }),
              hint: `TASK-${tk.id.slice(0, 8)}`,
              href: `/tasks/${tk.id}`,
              group: t("nav.tasks"),
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
              label: t("command.openExecution", { title: e.taskTitle }),
              hint: `EXEC-${e.id.slice(0, 8)}`,
              href: `/executions/${e.id}`,
              group: t("nav.executions"),
            }),
          )
      }
      setDynamicItems(items)
    })

    return () => {
      cancelled = true
    }
  }, [open, activeWorkspaceId, t])

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

  const select = (item: CommandItem) => {
    if (item.action) item.action()
    else if (item.href) navigate(item.href)
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
      if (flat[active]) select(flat[active])
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
            placeholder={t("command.placeholder")}
            className="h-12 w-full bg-transparent text-sm text-foreground outline-none placeholder:text-subtle-foreground"
          />
          <Kbd>Esc</Kbd>
        </div>

        <div className="max-h-[52vh] overflow-y-auto py-1.5">
          {flat.length === 0 && (
            <div className="px-4 py-8 text-center text-sm text-subtle-foreground">{t("command.noMatches", { query })}</div>
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
                    key={`${item.group}:${item.href ?? item.label}`}
                    onMouseEnter={() => setActive(idx)}
                    onClick={() => select(item)}
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
            <ArrowDown className="h-3 w-3" /> {t("command.navigate")}
          </span>
          <span className="flex items-center gap-1.5">
            <CornerDownLeft className="h-3 w-3" /> {t("command.open")}
          </span>
          <span className="ml-auto font-mono">DevPilot</span>
        </div>
      </div>
    </div>
  )
}
