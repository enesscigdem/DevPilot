import { useState } from "react"
import { useNavigate } from "react-router-dom"
import { Plus, Search, Sparkles, CornerDownLeft } from "lucide-react"
import { PageContainer, PageHeading, TaskRow } from "@/components/shared"
import { Button, Panel, Badge, Kbd } from "@/components/ui/primitives"
import { tasks, statusMeta, type TaskStatus } from "@/data/mock"

const filters: { key: TaskStatus | "all"; label: string }[] = [
  { key: "all", label: "All" },
  { key: "awaiting-approval", label: "Awaiting approval" },
  { key: "executing", label: "Executing" },
  { key: "blocked", label: "Blocked" },
  { key: "done", label: "Done" },
  { key: "failed", label: "Failed" },
  { key: "draft", label: "Draft" },
]

export function Tasks() {
  const navigate = useNavigate()
  const [active, setActive] = useState<TaskStatus | "all">("all")
  const [draft, setDraft] = useState("")

  const shown = active === "all" ? tasks : tasks.filter((t) => t.status === active)

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
              rows={2}
              placeholder="e.g. Add rate limiting to the public products endpoint, 100 requests per minute per API key…"
              className="w-full resize-none bg-transparent text-[14px] leading-relaxed text-foreground outline-none placeholder:text-subtle-foreground"
            />
            <div className="mt-2 flex items-center justify-between">
              <div className="flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
                <span>Context</span>
                <Badge tone="neutral" mono>
                  enesscigdem/DevPilot
                </Badge>
                <span className="hidden sm:inline">· 214 files indexed</span>
              </div>
              <Button
                variant="primary"
                size="sm"
                disabled={!draft.trim()}
                onClick={() => navigate("/tasks/TASK-142")}
              >
                Analyze
                <Kbd>
                  <CornerDownLeft className="h-3 w-3" />
                </Kbd>
              </Button>
            </div>
          </div>
        </div>
      </Panel>

      <div className="mb-3 flex flex-wrap items-center gap-1.5">
        {filters.map((f) => {
          const count = f.key === "all" ? tasks.length : tasks.filter((t) => t.status === f.key).length
          const isActive = active === f.key
          return (
            <button
              key={f.key}
              onClick={() => setActive(f.key)}
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
                  style={{ background: `var(--dot-${statusMeta[f.key as TaskStatus].tone})` }}
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
            placeholder="Filter tasks"
            className="w-32 bg-transparent text-[12.5px] text-foreground outline-none placeholder:text-subtle-foreground"
          />
        </div>
      </div>

      <Panel className="overflow-hidden">
        {shown.map((t) => (
          <TaskRow key={t.id} task={t} />
        ))}
        {shown.length === 0 && (
          <div className="flex flex-col items-center gap-2 px-4 py-12 text-center">
            <Plus className="h-5 w-5 text-subtle-foreground" />
            <p className="text-[13px] text-muted-foreground">No tasks in this state.</p>
          </div>
        )}
      </Panel>
    </PageContainer>
  )
}
