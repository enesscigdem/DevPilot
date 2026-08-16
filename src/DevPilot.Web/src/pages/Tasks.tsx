import { useCallback, useEffect, useState } from "react"
import { useNavigate } from "react-router-dom"
import { Plus, Search, Sparkles, CornerDownLeft, Loader2, AlertCircle } from "lucide-react"
import { PageContainer, PageHeading, TaskRow } from "@/components/shared"
import { Button, Panel, Badge, Kbd } from "@/components/ui/primitives"
import { getTasks, createTask, getWorkspaces } from "@/api"
import { TaskStatus, TaskPriority, type TaskListItem, type Workspace } from "@/types"

type FilterKey = "all" | "awaiting-approval" | "executing" | "blocked" | "done" | "failed" | "draft"

const filterTabs: { key: FilterKey; label: string; tone: "amber" | "blue" | "red" | "green" | "gray" | "neutral" }[] = [
  { key: "all", label: "All", tone: "neutral" },
  { key: "awaiting-approval", label: "Awaiting approval", tone: "amber" },
  { key: "executing", label: "Executing", tone: "blue" },
  { key: "blocked", label: "Blocked", tone: "red" },
  { key: "done", label: "Done", tone: "green" },
  { key: "failed", label: "Failed", tone: "red" },
  { key: "draft", label: "Draft", tone: "gray" },
]

function matchesFilter(task: TaskListItem, filter: FilterKey): boolean {
  switch (filter) {
    case "all":
      return true
    case "awaiting-approval":
      return task.status === TaskStatus.AwaitingApproval
    case "executing":
      return (
        task.status === TaskStatus.Executing ||
        task.status === TaskStatus.Analyzing ||
        task.status === TaskStatus.Approved
      )
    case "blocked":
      return task.status === TaskStatus.Rejected
    case "done":
      return task.status === TaskStatus.Completed
    case "failed":
      return task.status === TaskStatus.Failed
    case "draft":
      return task.status === TaskStatus.Draft || task.status === TaskStatus.ReadyForAnalysis
    default:
      return true
  }
}

export function Tasks() {
  const navigate = useNavigate()
  const [tasks, setTasks] = useState<TaskListItem[]>([])
  const [workspaces, setWorkspaces] = useState<Workspace[]>([])
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string>("")

  const [activeFilter, setActiveFilter] = useState<FilterKey>("all")
  const [searchQuery, setSearchQuery] = useState("")
  const [draft, setDraft] = useState("")

  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [isSubmitting, setIsSubmitting] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)

  const fetchWorkspaces = useCallback(async () => {
    try {
      const wsList = await getWorkspaces()
      setWorkspaces(wsList)
      if (wsList.length > 0) {
        setSelectedWorkspaceId((prev) => prev || wsList[0].id)
      }
    } catch (err) {
      console.error("Failed to load workspaces", err)
    }
  }, [])

  const fetchTasks = useCallback(async () => {
    setIsLoading(true)
    setError(null)
    try {
      const data = await getTasks()
      setTasks(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load tasks from server.")
    } finally {
      setIsLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchWorkspaces()
    fetchTasks()
  }, [fetchWorkspaces, fetchTasks])

  const selectedWorkspace = workspaces.find((w) => w.id === selectedWorkspaceId) || workspaces[0]

  const handleCreateTask = async () => {
    if (!draft.trim() || isSubmitting) return
    setIsSubmitting(true)
    setCreateError(null)
    try {
      const workspaceId = selectedWorkspaceId || (workspaces[0] ? workspaces[0].id : "")
      if (!workspaceId) {
        throw new Error("No repository workspace available. Please ensure a workspace exists.")
      }

      const created = await createTask({
        repositoryWorkspaceId: workspaceId,
        title: draft.trim(),
        description: draft.trim(),
        priority: TaskPriority.Medium,
      })

      setDraft("")
      navigate(`/tasks/${created.id}`)
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : "Failed to create task.")
    } finally {
      setIsSubmitting(false)
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
      e.preventDefault()
      handleCreateTask()
    }
  }

  const filteredTasks = tasks.filter((t) => {
    const filterMatch = matchesFilter(t, activeFilter)
    const searchMatch =
      !searchQuery.trim() ||
      t.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
      t.id.toLowerCase().includes(searchQuery.toLowerCase())
    return filterMatch && searchMatch
  })

  return (
    <PageContainer>
      <PageHeading
        eyebrow="Tasks"
        title="Engineering tasks"
        description="Describe an engineering change in plain language. DevPilot analyzes the Roslyn workspace, proposes a plan, and waits for your approval before touching code."
      />

      <Panel className="mb-6 overflow-hidden">
        <div className="flex items-start gap-3 p-3.5">
          <div className="mt-1.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
            <Sparkles className="h-4 w-4" />
          </div>
          <div className="flex-1">
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={handleKeyDown}
              rows={2}
              placeholder="e.g. Add rate limiting to the public products endpoint, 100 requests per minute per API key…"
              className="w-full resize-none bg-transparent text-[14px] leading-relaxed text-foreground outline-none placeholder:text-subtle-foreground"
            />
            {createError && (
              <div className="mb-2 flex items-center gap-1.5 text-[12px] font-medium text-danger">
                <AlertCircle className="h-3.5 w-3.5" />
                {createError}
              </div>
            )}
            <div className="mt-2 flex items-center justify-between">
              <div className="flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
                <span>Context</span>
                {workspaces.length > 1 ? (
                  <select
                    value={selectedWorkspaceId}
                    onChange={(e) => setSelectedWorkspaceId(e.target.value)}
                    className="rounded-[var(--radius-sm)] border border-border bg-surface px-2 py-0.5 font-mono text-[11px] text-foreground outline-none transition-colors hover:border-primary-ring"
                  >
                    {workspaces.map((ws) => (
                      <option key={ws.id} value={ws.id}>
                        {ws.displayName}
                      </option>
                    ))}
                  </select>
                ) : (
                  <Badge tone="neutral" mono>
                    {selectedWorkspace
                      ? `${selectedWorkspace.owner}/${selectedWorkspace.repository}`
                      : "enesscigdem/DevPilot"}
                  </Badge>
                )}
                <span className="hidden sm:inline">· active workspace</span>
              </div>
              <Button
                variant="primary"
                size="sm"
                disabled={!draft.trim() || isSubmitting}
                onClick={handleCreateTask}
              >
                {isSubmitting ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <>
                    Analyze
                    <Kbd>
                      <CornerDownLeft className="h-3 w-3" />
                    </Kbd>
                  </>
                )}
              </Button>
            </div>
          </div>
        </div>
      </Panel>

      <div className="mb-3 flex flex-wrap items-center gap-1.5">
        {filterTabs.map((f) => {
          const count = tasks.filter((t) => matchesFilter(t, f.key)).length
          const isActive = activeFilter === f.key
          return (
            <button
              key={f.key}
              onClick={() => setActiveFilter(f.key)}
              className={
                "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px] font-medium transition-colors " +
                (isActive
                  ? "border-primary-ring/60 bg-primary-soft text-primary"
                  : "border-border bg-surface text-muted-foreground hover:bg-surface-2 hover:text-foreground")
              }
            >
              {f.key !== "all" && (
                <span
                  className="h-1.5 w-1.5 rounded-full"
                  style={{ background: `var(--dot-${f.tone})` }}
                />
              )}
              {f.label}
              <span className="font-mono text-[11px] opacity-60">{count}</span>
            </button>
          )
        })}
        <div className="ml-auto flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5">
          <Search className="h-3.5 w-3.5 text-subtle-foreground" />
          <input
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder="Filter tasks"
            className="w-32 bg-transparent text-[12.5px] text-foreground outline-none placeholder:text-subtle-foreground"
          />
        </div>
      </div>

      <Panel className="overflow-hidden">
        {isLoading ? (
          <div className="flex flex-col items-center justify-center gap-2 px-4 py-16 text-center">
            <Loader2 className="h-5 w-5 animate-spin text-subtle-foreground" />
            <p className="text-[13px] text-muted-foreground">Loading tasks from API…</p>
          </div>
        ) : error ? (
          <div className="flex flex-col items-center justify-center gap-3 px-4 py-12 text-center">
            <AlertCircle className="h-6 w-6 text-danger" />
            <div>
              <p className="text-[13.5px] font-medium text-foreground">Failed to load tasks</p>
              <p className="mt-0.5 text-[12.5px] text-muted-foreground">{error}</p>
            </div>
            <Button variant="default" size="sm" onClick={fetchTasks}>
              Retry
            </Button>
          </div>
        ) : filteredTasks.length === 0 ? (
          <div className="flex flex-col items-center gap-2 px-4 py-12 text-center">
            <Plus className="h-5 w-5 text-subtle-foreground" />
            <p className="text-[13px] text-muted-foreground">
              {searchQuery.trim() || activeFilter !== "all"
                ? "No tasks match the selected filter."
                : "No tasks found."}
            </p>
          </div>
        ) : (
          filteredTasks.map((t) => <TaskRow key={t.id} task={t} />)
        )}
      </Panel>
    </PageContainer>
  )
}
