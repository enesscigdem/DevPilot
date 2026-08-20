import { useEffect, useRef, useState, type ReactNode } from "react"
import { NavLink, useLocation } from "react-router-dom"
import {
  Boxes,
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
} from "lucide-react"
import { useTheme } from "@/lib/theme"
import { useWorkspace } from "@/lib/workspace"
import { RepositoryWorkspaceStatus } from "@/types"
import { cn } from "@/lib/utils"
import { CommandMenu } from "./CommandMenu"
import { RepositoryPickerModal } from "./RepositoryPickerModal"
import { StatusDot } from "./ui/primitives"

const nav = [
  { to: "/", label: "Workspace", icon: Boxes, end: true },
  { to: "/projects", label: "Projects", icon: FolderGit2 },
  { to: "/tasks", label: "Tasks", icon: ListChecks },
  { to: "/brain", label: "Project Brain", icon: Sparkles },
  { to: "/executions", label: "Executions", icon: Activity },
  { to: "/architecture", label: "Architecture", icon: Network },
]

function Logo() {
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
        <div className="font-mono text-[10px] tracking-wide text-subtle-foreground">engineering workspace</div>
      </div>
    </div>
  )
}

export function AppShell({ children }: { children: ReactNode }) {
  const { theme, toggle } = useTheme()
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
                  : "No repository"}
              </div>
              <div className="flex items-center gap-1 text-[11px] text-subtle-foreground">
                <GitBranch className="h-3 w-3" />
                <span className="font-mono">{activeWorkspace ? activeWorkspace.branch : "none"}</span>
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
          <div className="tech-label px-2.5 pb-1.5">Workspace</div>
          {nav.map((item) => (
            <NavLink
              key={item.to}
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
                  {item.label}
                </>
              )}
            </NavLink>
          ))}
        </nav>

        <div className="mt-auto px-3 pb-4">
          <ActiveExecutionMini />
          <div className="mt-3 flex items-center justify-between rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5">
            <span className="tech-label">theme</span>
            <button
              onClick={toggle}
              className="flex items-center gap-1.5 rounded-[var(--radius-sm)] px-1.5 py-1 text-[12px] font-medium text-muted-foreground transition-colors hover:bg-surface-3 hover:text-foreground"
              aria-label="Toggle theme"
            >
              {theme === "light" ? <Sun className="h-3.5 w-3.5" /> : <Moon className="h-3.5 w-3.5" />}
              {theme === "light" ? "Light" : "Dark"}
            </button>
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
  const { activeAgentExecution } = useWorkspace()
  const [, setTick] = useState(0)

  const isRunning = Boolean(activeAgentExecution && !activeAgentExecution.completedAt)

  useEffect(() => {
    if (!isRunning) return
    const interval = setInterval(() => {
      setTick((t) => t + 1)
    }, 1000)
    return () => clearInterval(interval)
  }, [isRunning])

  if (!activeAgentExecution) {
    return (
      <div className="block rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-2 text-subtle-foreground">
        <div className="flex items-center gap-1.5">
          <StatusDot tone="neutral" />
          <span className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">Agent idle</span>
        </div>
        <div className="mt-1 truncate text-[12px] text-muted-foreground">No active execution</div>
      </div>
    )
  }

  const elapsedText = formatElapsed(activeAgentExecution.elapsedSeconds, activeAgentExecution.startedAt, activeAgentExecution.completedAt)
  const currentStage = activeAgentExecution.currentStageKey
    ? `${activeAgentExecution.currentStageKey.charAt(0).toUpperCase() + activeAgentExecution.currentStageKey.slice(1)} stage`
    : "Running"

  return (
    <NavLink
      to={`/executions/${activeAgentExecution.executionId}`}
      className="block rounded-[var(--radius-md)] border border-primary-ring/60 bg-primary-soft px-2.5 py-2 transition-colors hover:border-primary/50"
    >
      <div className="flex items-center gap-1.5">
        <StatusDot tone="blue" pulse />
        <span className="font-mono text-[10px] uppercase tracking-wide text-primary">Agent running</span>
      </div>
      <div className="mt-1 truncate text-[12px] font-medium text-foreground">
        {activeAgentExecution.taskDisplayId} · {currentStage}
      </div>
      <div className="mt-0.5 font-mono text-[11px] text-primary/80">
        {elapsedText} elapsed
      </div>
    </NavLink>
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
  "/": "Workspace",
  "/projects": "Project Workspace",
  "/tasks": "Tasks",
  "/brain": "Project Brain",
  "/executions": "Executions",
  "/architecture": "Architecture & Impact",
}

function TopBar({ onOpenCommand, path }: { onOpenCommand: () => void; path: string }) {
  const title =
    routeTitles[path] ??
    (path.startsWith("/tasks/")
      ? "Task & Impact Analysis"
      : path.startsWith("/executions/")
        ? "Execution Workspace"
        : path.startsWith("/review/")
          ? "Code Review"
          : "DevPilot")

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
        <span className="flex-1">Search or run a command…</span>
        <span className="flex items-center gap-0.5 font-mono text-[11px]">
          <CommandIcon className="h-3 w-3" />K
        </span>
      </button>

      <div className="flex items-center gap-2">
        <div className="flex items-center gap-1.5 rounded-full border border-border bg-surface-2 px-2.5 py-1">
          <StatusDot tone="green" />
          <span className="font-mono text-[11px] text-muted-foreground">indexed</span>
        </div>
        <div className="h-7 w-7 rounded-full bg-foreground text-center font-mono text-[12px] font-semibold leading-7 text-canvas">
          E
        </div>
      </div>
    </header>
  )
}
