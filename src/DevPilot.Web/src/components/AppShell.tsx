import { useEffect, useState, type ReactNode } from "react"
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
  Command as CommandIcon,
} from "lucide-react"
import { useTheme } from "@/lib/theme"
import { cn } from "@/lib/utils"
import { CommandMenu } from "./CommandMenu"
import { StatusDot } from "./ui/primitives"
import { repository } from "@/data/mock"

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
  const [cmdOpen, setCmdOpen] = useState(false)
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
        <div className="px-3">
          <button className="group flex w-full items-center gap-2.5 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-2 text-left shadow-[var(--shadow-sm)] transition-colors hover:border-border-strong">
            <FolderGit2 className="h-4 w-4 shrink-0 text-muted-foreground" strokeWidth={2} />
            <div className="min-w-0 flex-1">
              <div className="truncate font-mono text-[12px] font-medium text-foreground">{repository.fullName}</div>
              <div className="flex items-center gap-1 text-[11px] text-subtle-foreground">
                <GitBranch className="h-3 w-3" />
                <span className="font-mono">{repository.branch}</span>
              </div>
            </div>
            <StatusDot tone="green" />
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
    </div>
  )
}

function ActiveExecutionMini() {
  return (
    <NavLink
      to="/executions/EXEC-142"
      className="block rounded-[var(--radius-md)] border border-primary-ring/60 bg-primary-soft px-2.5 py-2 transition-colors hover:border-primary/50"
    >
      <div className="flex items-center gap-1.5">
        <StatusDot tone="blue" pulse />
        <span className="font-mono text-[10px] uppercase tracking-wide text-primary">Agent running</span>
      </div>
      <div className="mt-1 truncate text-[12px] font-medium text-foreground">TASK-142 · Review stage</div>
      <div className="mt-0.5 font-mono text-[11px] text-primary/80">00:41 elapsed · Reviewer</div>
    </NavLink>
  )
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
