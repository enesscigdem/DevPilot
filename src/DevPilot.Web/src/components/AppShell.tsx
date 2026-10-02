import { useEffect, useRef, useState, type ReactNode } from "react"
import { NavLink, useLocation } from "react-router-dom"
import {
  BarChart3,
  Boxes,
  Cpu,
  FolderGit2,
  ListChecks,
  Sparkles,
  Activity,
  Network,
  Search,
  Sun,
  Moon,
  GitBranch,
  Check,
  Plus,
  Loader2,
  AlertCircle,
  Command as CommandIcon,
  Languages,
} from "lucide-react"
import { useTranslation } from "react-i18next"
import { LANGUAGES, setLang, type Lang } from "@/i18n"
import { useTheme } from "@/lib/theme"
import { useWorkspace } from "@/lib/workspace"
import { RepositoryWorkspaceStatus } from "@/types"
import { cn } from "@/lib/utils"
import { CommandMenu } from "./CommandMenu"
import { RepositoryPickerModal } from "./RepositoryPickerModal"
import { StatusDot } from "./ui/primitives"

interface NavEntry {
  to: string
  label: string // i18n key
  icon: typeof Boxes
  end?: boolean
}

// Grouped by the question each area answers, in the order work flows through DevPilot.
const navOverview: NavEntry = { to: "/", label: "nav.overview", icon: Boxes, end: true }

const navGroups: { label: string; items: NavEntry[] }[] = [
  {
    label: "nav.planRun",
    items: [
      { to: "/tasks", label: "nav.tasks", icon: ListChecks },
      { to: "/executions", label: "nav.executions", icon: Activity },
    ],
  },
  {
    label: "nav.understand",
    items: [
      { to: "/projects", label: "nav.repository", icon: FolderGit2 },
      { to: "/architecture", label: "nav.impactMap", icon: Network },
      { to: "/brain", label: "nav.projectBrain", icon: Sparkles },
    ],
  },
  {
    label: "nav.measure",
    items: [{ to: "/insights", label: "nav.insights", icon: BarChart3 }],
  },
  {
    label: "nav.settings",
    items: [{ to: "/models", label: "nav.aiModels", icon: Cpu }],
  },
]

function NavItem({ item }: { item: NavEntry }) {
  const { t } = useTranslation()
  return (
    <NavLink
      to={item.to}
      end={item.end}
      className={({ isActive }) =>
        cn(
          "group relative flex items-center gap-2.5 rounded-[var(--radius-md)] px-2.5 py-[7px] text-[13px] font-medium transition-colors",
          isActive
            ? "bg-surface text-foreground shadow-[var(--shadow-sm)]"
            : "text-muted-foreground hover:bg-surface-3 hover:text-foreground",
        )
      }
    >
      {({ isActive }) => (
        <>
          <span
            className={cn(
              "absolute left-0 top-1/2 h-4 w-[2px] -translate-y-1/2 rounded-full bg-primary transition-opacity",
              isActive ? "opacity-100" : "opacity-0",
            )}
          />
          <item.icon
            className={cn("h-4 w-4", isActive ? "text-primary" : "text-subtle-foreground group-hover:text-foreground")}
            strokeWidth={2}
          />
          {t(item.label)}
        </>
      )}
    </NavLink>
  )
}

function Logo() {
  const { t } = useTranslation()
  return (
    <div className="flex items-center gap-2.5">
      <div className="relative flex h-8 w-8 items-center justify-center rounded-[9px] bg-foreground text-canvas">
        <div className="grid grid-cols-2 gap-[3px]">
          <span className="h-1.5 w-1.5 rounded-[2px] bg-canvas" />
          <span className="h-1.5 w-1.5 rounded-[2px] bg-primary" />
          <span className="h-1.5 w-1.5 rounded-[2px] bg-accent" />
          <span className="h-1.5 w-1.5 rounded-[2px] bg-canvas/60" />
        </div>
      </div>
      <div className="leading-tight">
        <div className="text-[15px] font-semibold tracking-tight text-foreground">DevPilot</div>
        <div className="font-mono text-[10px] tracking-wide text-subtle-foreground">{t("common.engineeringWorkspace")}</div>
      </div>
    </div>
  )
}

export function AppShell({ children }: { children: ReactNode }) {
  const { theme, setTheme } = useTheme()
  const { t, i18n } = useTranslation()
  const {
    activeWorkspace,
  } = useWorkspace()

  const [cmdOpen, setCmdOpen] = useState(false)
  const [repoPickerOpen, setRepoPickerOpen] = useState(false)
  const location = useLocation()

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault()
        setCmdOpen((o) => !o)
      }
    }
    window.addEventListener("keydown", handler)
    return () => window.removeEventListener("keydown", handler)
  }, [])

  return (
    <div className="flex h-screen w-screen overflow-hidden bg-canvas">
      {/* Left navigation rail */}
      <aside className="flex w-[236px] shrink-0 flex-col border-r border-border bg-surface-2">
        <div className="px-4 pb-4 pt-5">
          <Logo />
        </div>

        {/* Repo switcher */}
        <div className="relative px-3">
          <button
            type="button"
            onClick={() => setRepoPickerOpen(true)}
            className="group flex w-full items-center gap-2.5 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-2 text-left shadow-[var(--shadow-sm)] transition-colors hover:border-border-strong"
          >
            <FolderGit2 className="h-4 w-4 shrink-0 text-muted-foreground" strokeWidth={2} />
            <div className="min-w-0 flex-1">
              <div className="truncate font-mono text-[12px] font-medium text-foreground">
                {activeWorkspace
                  ? `${activeWorkspace.owner}/${activeWorkspace.repository}`
                  : t("shared.shell.noRepository")}
              </div>
              <div className="flex items-center gap-1 text-[11px] text-subtle-foreground">
                <GitBranch className="h-3 w-3" />
                <span className="font-mono">{activeWorkspace ? activeWorkspace.branch : t("shared.shell.none")}</span>
              </div>
            </div>
            <StatusDot
              tone={
                activeWorkspace
                  ? activeWorkspace.status === RepositoryWorkspaceStatus.Completed
                    ? "green"
                    : activeWorkspace.status === RepositoryWorkspaceStatus.Cloning
                      ? "blue"
                      : activeWorkspace.status === RepositoryWorkspaceStatus.Failed
                        ? "red"
                        : "neutral"
                  : "gray"
              }
              pulse={activeWorkspace?.status === RepositoryWorkspaceStatus.Cloning}
            />
          </button>
        </div>

        <nav className="mt-4 flex flex-col gap-0.5 px-3">
          <NavItem item={navOverview} />
          {navGroups.map((group) => (
            <div key={group.label} className="mt-3 flex flex-col gap-0.5">
              <div className="tech-label px-2.5 pb-1.5">{t(group.label)}</div>
              {group.items.map((item) => (
                <NavItem key={item.to} item={item} />
              ))}
            </div>
          ))}
        </nav>

        <div className="mt-auto px-3 pb-4">
          <ActiveExecutionMini />
          <div className="mt-3 flex items-center gap-1 rounded-[var(--radius-md)] border border-border bg-surface p-0.5">
            <div role="group" aria-label={t("common.switchLanguage")} className="flex flex-1 gap-0.5">
              {LANGUAGES.map((l) => (
                <button
                  key={l.code}
                  type="button"
                  onClick={() => setLang(l.code as Lang)}
                  title={l.name}
                  aria-pressed={i18n.language === l.code}
                  className={cn(
                    "flex-1 rounded-[6px] py-1 font-mono text-[11px] font-semibold transition-colors",
                    i18n.language === l.code
                      ? "bg-primary-soft text-primary"
                      : "text-subtle-foreground hover:bg-surface-3 hover:text-foreground",
                  )}
                >
                  {l.short}
                </button>
              ))}
            </div>
            <span className="h-4 w-px shrink-0 bg-border" aria-hidden="true" />
            <div role="group" aria-label={t("common.toggleTheme")} className="flex flex-1 gap-0.5">
              {([["light", Sun], ["dark", Moon]] as const).map(([mode, Icon]) => (
                <button
                  key={mode}
                  type="button"
                  onClick={() => setTheme(mode)}
                  title={mode === "light" ? t("common.light") : t("common.dark")}
                  aria-pressed={theme === mode}
                  className={cn(
                    "flex flex-1 items-center justify-center rounded-[6px] py-1 transition-colors",
                    theme === mode ? "bg-primary-soft text-primary" : "text-subtle-foreground hover:bg-surface-3 hover:text-foreground",
                  )}
                >
                  <Icon className="h-3.5 w-3.5" />
                </button>
              ))}
            </div>
          </div>
        </div>
      </aside>

      {/* Main column */}
      <div className="flex min-w-0 flex-1 flex-col">
        <TopBar onOpenCommand={() => setCmdOpen(true)} path={location.pathname} />
        <main className="min-h-0 flex-1 overflow-y-auto">{children}</main>
      </div>

      <CommandMenu open={cmdOpen} onClose={() => setCmdOpen(false)} />
      <RepositoryPickerModal open={repoPickerOpen} onClose={() => setRepoPickerOpen(false)} />
    </div>
  )
}

function ActiveExecutionMini() {
  const { t } = useTranslation()
  const { activeAgentExecution, overview } = useWorkspace()
  const comparisonId = overview?.openModelComparisonId ?? null
  const [, setTick] = useState(0)

  const isRunning = Boolean(activeAgentExecution && !activeAgentExecution.completedAt)

  useEffect(() => {
    if (!isRunning) return
    const interval = setInterval(() => {
      setTick((t) => t + 1)
    }, 1000)
    return () => clearInterval(interval)
  }, [isRunning])

  const comparisonLink = comparisonId ? (
    <NavLink
      to={`/comparisons/${comparisonId}`}
      className="mt-1 block rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5 text-[11.5px] font-medium text-primary hover:border-primary/50"
    >
      {t("modelCompare.banner.title")}
    </NavLink>
  ) : null

  if (!activeAgentExecution) {
    return (
      <div>
        <div className="block rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-2 text-subtle-foreground">
          <div className="flex items-center gap-1.5">
            <StatusDot tone="neutral" />
            <span className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">{t("shared.shell.agentIdle")}</span>
          </div>
          <div className="mt-1 truncate text-[12px] text-muted-foreground">{t("shared.shell.noActiveExecution")}</div>
        </div>
        {comparisonLink}
      </div>
    )
  }

  const elapsedText = formatElapsed(activeAgentExecution.elapsedSeconds, activeAgentExecution.startedAt, activeAgentExecution.completedAt)
  const stageKey = activeAgentExecution.currentStageKey
  const currentStage = stageKey
    ? t("shared.shell.stageSuffix", {
        stage: t(`shared.stage.${stageKey}`, { defaultValue: stageKey.charAt(0).toUpperCase() + stageKey.slice(1) }),
      })
    : t("shared.shell.running")

  return (
    <div>
    <NavLink
      to={`/executions/${activeAgentExecution.executionId}`}
      className="block rounded-[var(--radius-md)] border border-primary-ring/60 bg-primary-soft px-2.5 py-2 transition-colors hover:border-primary/50"
    >
      <div className="flex items-center gap-1.5">
        <StatusDot tone="blue" pulse />
        <span className="font-mono text-[10px] uppercase tracking-wide text-primary">{t("shared.shell.agentRunning")}</span>
      </div>
      <div className="mt-1 truncate text-[12px] font-medium text-foreground">
        {activeAgentExecution.taskDisplayId} · {currentStage}
      </div>
      <div className="mt-0.5 font-mono text-[11px] text-primary/80">
        {t("shared.shell.elapsed", { time: elapsedText })}
      </div>
    </NavLink>
    {comparisonLink}
    </div>
  )
}

function formatElapsed(elapsedSeconds?: number | null, startedAt?: string | null, completedAt?: string | null): string {
  if (completedAt && startedAt) {
    const start = new Date(startedAt).getTime()
    const end = new Date(completedAt).getTime()
    if (!isNaN(start) && !isNaN(end) && end >= start) {
      const diffSec = Math.floor((end - start) / 1000)
      const mins = Math.floor(diffSec / 60)
      const secs = diffSec % 60
      if (mins >= 60) {
        const hrs = Math.floor(mins / 60)
        const remMins = mins % 60
        return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
      }
      return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
  }
  if (completedAt && elapsedSeconds != null) {
    const mins = Math.floor(elapsedSeconds / 60)
    const secs = elapsedSeconds % 60
    if (mins >= 60) {
      const hrs = Math.floor(mins / 60)
      const remMins = mins % 60
      return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
    return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
  }
  if (startedAt && !completedAt) {
    const start = new Date(startedAt).getTime()
    if (!isNaN(start)) {
      const diffSec = Math.max(0, Math.floor((Date.now() - start) / 1000))
      const mins = Math.floor(diffSec / 60)
      const secs = diffSec % 60
      if (mins >= 60) {
        const hrs = Math.floor(mins / 60)
        const remMins = mins % 60
        return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
      }
      return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
  }
  if (elapsedSeconds != null) {
    const mins = Math.floor(elapsedSeconds / 60)
    const secs = elapsedSeconds % 60
    if (mins >= 60) {
      const hrs = Math.floor(mins / 60)
      const remMins = mins % 60
      return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
    return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
  }
  return "00:00"
}

const routeTitles: Record<string, string> = {
  "/": "nav.overview",
  "/projects": "nav.repository",
  "/tasks": "nav.tasks",
  "/brain": "nav.projectBrain",
  "/executions": "nav.executions",
  "/executions/compare": "nav.compareExecutions",
  "/architecture": "nav.impactMap",
  "/insights": "nav.insights",
  "/models": "nav.aiModels",
}

function TopBar({ onOpenCommand, path }: { onOpenCommand: () => void; path: string }) {
  const { t } = useTranslation()
  const titleKey =
    routeTitles[path] ??
    (path.startsWith("/tasks/")
      ? "nav.taskImpact"
      : path.startsWith("/comparisons/")
        ? "nav.modelComparison"
      : path.startsWith("/executions/")
        ? "nav.execution"
        : path.startsWith("/review/")
          ? "nav.reviewDelivery"
          : "DevPilot")
  const title = t(titleKey, { defaultValue: titleKey })

  return (
    <header className="flex h-14 shrink-0 items-center gap-4 border-b border-border bg-surface/80 px-6 backdrop-blur-sm">
      <div className="flex items-center gap-2 text-[13px]">
        <span className="font-mono text-subtle-foreground">devpilot</span>
        <span className="text-border-strong">/</span>
        <span className="font-medium text-foreground">{title}</span>
      </div>

      <button
        onClick={onOpenCommand}
        className="ml-auto flex h-9 w-full max-w-[340px] items-center gap-2.5 rounded-[var(--radius-md)] border border-border bg-surface-2 px-3 text-left text-[13px] text-subtle-foreground transition-colors hover:border-border-strong hover:bg-surface"
      >
        <Search className="h-4 w-4" strokeWidth={2} />
        <span className="flex-1">{t("common.searchPlaceholder")}</span>
        <span className="flex items-center gap-0.5 font-mono text-[11px]">
          <CommandIcon className="h-3 w-3" />K
        </span>
      </button>

      <div className="flex items-center gap-2">
        <div className="flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2.5 py-1">
          <StatusDot tone="green" />
          <span className="font-mono text-[11px] text-muted-foreground">{t("common.indexed")}</span>
        </div>
        <div className="h-7 w-7 rounded-full bg-foreground text-center font-mono text-[12px] font-semibold leading-7 text-canvas">
          E
        </div>
      </div>
    </header>
  )
}
