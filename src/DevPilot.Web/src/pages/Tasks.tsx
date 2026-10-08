import { useCallback, useEffect, useState } from "react"
import { useNavigate } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { Plus, Search, Sparkles, CornerDownLeft, Loader2, AlertCircle } from "lucide-react"
import { PageContainer, PageHeading, TaskRow } from "@/components/shared"
import { BatchTaskPanel } from "@/components/BatchTaskPanel"
import { JiraImportModal } from "@/components/JiraImportModal"
import { Button, Panel, Badge, Kbd } from "@/components/ui/primitives"
import { getTasks, createTask } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import { TaskStatus, TaskPriority, type TaskListItem } from "@/types"

type FilterKey = "all" | "awaiting-approval" | "executing" | "blocked" | "done" | "failed" | "draft"

const filterTabs: { key: FilterKey; label: string; tone: "amber" | "blue" | "red" | "green" | "gray" | "neutral" }[] = [
  { key: "all", label: "tasks.filters.all", tone: "neutral" },
  { key: "awaiting-approval", label: "tasks.filters.awaitingApproval", tone: "amber" },
  { key: "executing", label: "tasks.filters.executing", tone: "blue" },
  { key: "blocked", label: "tasks.filters.blocked", tone: "red" },
  { key: "done", label: "tasks.filters.done", tone: "green" },
  { key: "failed", label: "tasks.filters.failed", tone: "red" },
  { key: "draft", label: "tasks.filters.draft", tone: "gray" },
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
  const { t: tr } = useTranslation()
  const navigate = useNavigate()
  const { activeWorkspace, activeWorkspaceId } = useWorkspace()
  const [tasks, setTasks] = useState<TaskListItem[]>([])

  const [activeFilter, setActiveFilter] = useState<FilterKey>("all")
  const [searchQuery, setSearchQuery] = useState("")
  const [title, setTitle] = useState("")
  const [description, setDescription] = useState("")

  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [showBatch, setShowBatch] = useState(false)
  const [showJira, setShowJira] = useState(false)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)

  const fetchTasks = useCallback(async () => {
    if (!activeWorkspaceId) {
      setTasks([])
      setIsLoading(false)
      return
    }

    setIsLoading(true)
    setError(null)
    try {
      const data = await getTasks({ repositoryWorkspaceId: activeWorkspaceId })
      setTasks(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : tr("tasks.errLoad"))
    } finally {
      setIsLoading(false)
    }
  }, [activeWorkspaceId])

  useEffect(() => {
    fetchTasks()
  }, [fetchTasks])

  const handleCreateTask = async () => {
    if (!title.trim() || isSubmitting) return
    if (!activeWorkspaceId) {
      setCreateError(tr("tasks.errNoWorkspace"))
      return
    }

    setIsSubmitting(true)
    setCreateError(null)
    try {
      const created = await createTask({
        repositoryWorkspaceId: activeWorkspaceId,
        title: title.trim(),
        description: description.trim(),
        priority: TaskPriority.Medium,
      })

      setTitle("")
      setDescription("")
      navigate(`/tasks/${created.id}`)
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : tr("tasks.errCreate"))
    } finally {
      setIsSubmitting(false)
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
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
        eyebrow={tr("tasks.eyebrow")}
        title={tr("tasks.title")}
        description={tr("tasks.description")}
        actions={
          !showBatch ? (
            <Button size="sm" onClick={() => setShowBatch(true)}>
              <Plus className="h-3.5 w-3.5" />
              {tr("tasks.batch.open")}
            </Button>
          ) : undefined
        }
      />

      {showJira && (
        <JiraImportModal
          workspaceId={activeWorkspaceId}
          onClose={() => setShowJira(false)}
          onImported={fetchTasks}
        />
      )}

      {showBatch && (
        <BatchTaskPanel
          workspaceId={activeWorkspaceId}
          onClose={() => setShowBatch(false)}
          onCreated={fetchTasks}
        />
      )}

      <Panel className="mb-6 overflow-hidden">
        <div className="flex items-start gap-3 p-3.5">
          <div className="mt-1.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
            <Sparkles className="h-4 w-4" />
          </div>
          <div className="flex-1 min-w-0 space-y-1.5">
            <input
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              onKeyDown={handleKeyDown}
              maxLength={200}
              placeholder={tr("tasks.titlePlaceholder")}
              className="w-full bg-transparent text-[14px] font-medium text-foreground outline-none placeholder:text-subtle-foreground"
              required
            />
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              onKeyDown={handleKeyDown}
              maxLength={10000}
              rows={2}
              placeholder={tr("tasks.descriptionPlaceholder")}
              className="w-full resize-none bg-transparent text-[13px] leading-relaxed text-foreground outline-none placeholder:text-subtle-foreground"
            />
            {createError && (
              <div className="mb-2 flex items-center gap-1.5 text-[12px] font-medium text-danger">
                <AlertCircle className="h-3.5 w-3.5" />
                {createError}
              </div>
            )}
            <div className="mt-2 flex items-center justify-between">
              <div className="flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
                <span>{tr("tasks.context")}</span>
                <Badge tone="neutral" mono>
                  {activeWorkspace
                    ? `${activeWorkspace.owner}/${activeWorkspace.repository}`
                    : tr("tasks.noActiveWorkspace")}
                </Badge>
                <span className="hidden sm:inline">{tr("tasks.activeWorkspace")}</span>
              </div>
              <Button
                variant="primary"
                size="sm"
                disabled={!title.trim() || isSubmitting || !activeWorkspaceId}
                onClick={handleCreateTask}
              >
                {isSubmitting ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <>
                    {tr("tasks.analyze")}
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
                  : "border-border bg-surface text-muted-foreground hover:bg-surface-3 hover:text-foreground")
              }
            >
              {f.key !== "all" && (
                <span
                  className="h-1.5 w-1.5 rounded-full"
                  style={{ background: `var(--dot-${f.tone})` }}
                />
              )}
              {tr(f.label)}
              <span className="font-mono text-[11px] opacity-60">{count}</span>
            </button>
          )
        })}
        <div className="ml-auto flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5">
          <Search className="h-3.5 w-3.5 text-subtle-foreground" />
          <input
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder={tr("tasks.filterPlaceholder")}
            className="w-32 bg-transparent text-[12.5px] text-foreground outline-none placeholder:text-subtle-foreground"
          />
        </div>
      </div>

      <Panel className="overflow-hidden">
        {isLoading ? (
          <div className="flex flex-col items-center justify-center gap-2 px-4 py-16 text-center">
            <Loader2 className="h-5 w-5 animate-spin text-subtle-foreground" />
            <p className="text-[13px] text-muted-foreground">{tr("tasks.loading")}</p>
          </div>
        ) : error ? (
          <div className="flex flex-col items-center justify-center gap-3 px-4 py-12 text-center">
            <AlertCircle className="h-6 w-6 text-danger" />
            <div>
              <p className="text-[13.5px] font-medium text-foreground">{tr("tasks.failedLoad")}</p>
              <p className="mt-0.5 text-[12.5px] text-muted-foreground">{error}</p>
            </div>
            <Button variant="default" size="sm" onClick={fetchTasks}>
              {tr("common.retry")}
            </Button>
          </div>
        ) : filteredTasks.length === 0 ? (
          <div className="flex flex-col items-center gap-2 px-4 py-12 text-center">
            <Plus className="h-5 w-5 text-subtle-foreground" />
            <p className="text-[13px] text-muted-foreground">
              {!activeWorkspaceId
                ? tr("tasks.noWorkspaceEmpty")
                : searchQuery.trim() || activeFilter !== "all"
                  ? tr("tasks.noMatch")
                  : tr("tasks.noTasks")}
            </p>
          </div>
        ) : (
          filteredTasks.map((t) => <TaskRow key={t.id} task={t} />)
        )}
      </Panel>
    </PageContainer>
  )
}
