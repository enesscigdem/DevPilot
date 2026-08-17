import { Link } from "react-router-dom"
import {
  ArrowRight,
  GitBranch,
  Boxes,
  FileCode2,
  Clock,
  Coins,
  Cpu,
  CircleStop,
  GitPullRequest,
} from "lucide-react"
import { PageContainer, SectionHead } from "@/components/shared"
import { Badge, Button, Meter, Panel, StatusDot } from "@/components/ui/primitives"
import {
  attention,
  tasks,
  recentActivity,
  repository,
  execution,
  stages,
} from "@/data/mock"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"

export function Workspace() {
  const { activeWorkspace } = useWorkspace()
  const awaiting = tasks.filter((t) => t.status === "awaiting-approval" || t.status === "planning")
  const trouble = tasks.filter((t) => t.status === "blocked" || t.status === "failed")

  const repoFullName = activeWorkspace
    ? `${activeWorkspace.owner}/${activeWorkspace.repository}`
    : repository.fullName
  const branchName = activeWorkspace ? activeWorkspace.branch : repository.branch

  return (
    <PageContainer>
      {/* Greeting */}
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <div>
          <div className="tech-label mb-1.5">Tuesday · 09:42 · {branchName}</div>
          <h1 className="text-[24px] font-semibold tracking-tight text-foreground">
            What should you work on right now?
          </h1>
          <p className="mt-1.5 flex items-center gap-2 font-mono text-[12px] text-muted-foreground">
            <GitBranch className="h-3.5 w-3.5" />
            {repoFullName}
            <span className="text-border-strong">·</span>
            {repository.files} files
            <span className="text-border-strong">·</span>
            indexed {repository.lastIndexed}
          </p>
        </div>
        <Button variant="primary" size="lg" className="gap-2">
          New task
          <ArrowRight className="h-4 w-4" />
        </Button>
      </div>

      {/* Needs your attention */}
      <SectionHead title="Needs your attention" count={attention.length} />
      <div className="mb-8 grid grid-cols-1 gap-3 md:grid-cols-3">
        {attention.map((item) => (
          <Link
            key={item.id}
            to={item.href}
            className="group relative overflow-hidden rounded-[var(--radius-lg)] border border-border bg-surface p-4 shadow-[var(--shadow-sm)] transition-all hover:shadow-[var(--shadow-md)]"
          >
            <span
              className={cn(
                "absolute inset-x-0 top-0 h-[3px]",
                item.tone === "red" && "bg-danger",
                item.tone === "amber" && "bg-accent",
                item.tone === "blue" && "bg-primary",
              )}
            />
            <div className="flex items-center gap-2">
              <StatusDot tone={item.tone} pulse={item.tone === "red"} />
              <span className="text-[13.5px] font-semibold text-foreground">{item.title}</span>
            </div>
            <p className="mt-2 text-[12.5px] leading-relaxed text-muted-foreground">{item.reason}</p>
            <div className="mt-3 flex items-center justify-between">
              <span className="font-mono text-[11px] text-subtle-foreground">{item.meta}</span>
              <span className="flex items-center gap-1 text-[12px] font-medium text-primary opacity-0 transition-opacity group-hover:opacity-100">
                {item.cta}
                <ArrowRight className="h-3.5 w-3.5" />
              </span>
            </div>
          </Link>
        ))}
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_360px]">
        <div className="flex flex-col gap-8">
          {/* Active execution */}
          <section>
            <SectionHead
              title="Active agent execution"
              action={
                <Link to="/executions" className="text-[12px] font-medium text-primary hover:underline">
                  View all
                </Link>
              }
            />
            <Panel className="overflow-hidden">
              <div className="flex items-center justify-between border-b border-border bg-surface-2 px-4 py-3">
                <div className="flex items-center gap-2.5">
                  <StatusDot tone="blue" pulse />
                  <span className="font-mono text-[12px] font-medium text-foreground">TASK-142</span>
                  <span className="text-[13px] text-foreground">Add status filtering to the orders endpoint</span>
                </div>
                <Button variant="danger" size="sm" className="gap-1.5">
                  <CircleStop className="h-3.5 w-3.5" />
                  Cancel
                </Button>
              </div>

              {/* stage rail */}
              <div className="flex items-center gap-1 px-4 py-4">
                {stages.map((stage, i) => {
                  const currentIndex = stages.findIndex((s) => s.key === execution.currentStage)
                  const state = i < currentIndex ? "done" : i === currentIndex ? "active" : "todo"
                  return (
                    <div key={stage.key} className="flex flex-1 items-center gap-1">
                      <div className="flex flex-col items-center gap-1.5">
                        <div
                          className={cn(
                            "flex h-6 w-6 items-center justify-center rounded-full border text-[10px] font-semibold transition-colors",
                            state === "done" && "border-success bg-success-soft text-success",
                            state === "active" && "border-primary bg-primary text-primary-foreground",
                            state === "todo" && "border-border bg-surface text-subtle-foreground",
                          )}
                        >
                          {state === "active" ? <span className="h-1.5 w-1.5 rounded-full bg-primary-foreground animate-pulse-dot" /> : i + 1}
                        </div>
                        <span
                          className={cn(
                            "whitespace-nowrap text-[10.5px] font-medium",
                            state === "active" ? "text-foreground" : "text-subtle-foreground",
                          )}
                        >
                          {stage.label}
                        </span>
                      </div>
                      {i < stages.length - 1 && (
                        <div
                          className={cn(
                            "mb-4 h-[2px] flex-1 rounded-full",
                            i < currentIndex ? "bg-success" : "bg-border",
                          )}
                        />
                      )}
                    </div>
                  )
                })}
              </div>

              <div className="grid grid-cols-2 gap-px border-t border-border bg-border sm:grid-cols-4">
                <Metric icon={<Clock className="h-3.5 w-3.5" />} label="Elapsed" value={execution.elapsed} />
                <Metric icon={<Cpu className="h-3.5 w-3.5" />} label="Tokens" value={`${(execution.tokensUsed / 1000).toFixed(0)}K`} />
                <Metric icon={<Coins className="h-3.5 w-3.5" />} label="Est. cost" value={`$${execution.estCost.toFixed(2)}`} />
                <Metric icon={<FileCode2 className="h-3.5 w-3.5" />} label="Files" value="6" />
              </div>
            </Panel>
          </section>

          {/* Awaiting approval */}
          <section>
            <SectionHead title="Awaiting your approval" count={awaiting.length} />
            <Panel className="overflow-hidden">
              {awaiting.map((task) => (
                <ApprovalRow key={task.id} id={task.id} title={task.title} branch={task.branch} files={task.filesTouched} />
              ))}
              {awaiting.length === 0 && <Empty text="Nothing waiting on you." />}
            </Panel>
          </section>

          {/* Blocked / failed */}
          <section>
            <SectionHead title="Failed or blocked" count={trouble.length} />
            <Panel className="divide-y divide-border overflow-hidden">
              {trouble.map((task) => (
                <Link
                  key={task.id}
                  to={task.status === "failed" ? `/executions/EXEC-${task.id.split("-")[1]}` : `/tasks/${task.id}`}
                  className="flex items-center gap-3 px-4 py-3 transition-colors hover:bg-surface-2"
                >
                  <StatusDot tone="red" pulse={task.status === "failed"} />
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="font-mono text-[11px] text-subtle-foreground">{task.id}</span>
                      <span className="truncate text-[13px] font-medium text-foreground">{task.title}</span>
                    </div>
                    <p className="mt-0.5 truncate text-[12px] text-muted-foreground">{task.summary}</p>
                  </div>
                  <Badge tone="red">{task.status === "failed" ? "Build failed" : "Blocked"}</Badge>
                </Link>
              ))}
            </Panel>
          </section>
        </div>

        {/* Right rail */}
        <div className="flex flex-col gap-8">
          <section>
            <SectionHead title="Recent engineering activity" />
            <Panel className="p-4">
              <ol className="relative ml-1.5 border-l border-border">
                {recentActivity.map((item) => (
                  <li key={item.id} className="relative mb-4 pl-4 last:mb-0">
                    <span
                      className={cn(
                        "absolute -left-[5px] top-1 h-2.5 w-2.5 rounded-full border-2 border-surface",
                        item.tone === "green" && "bg-success",
                        item.tone === "red" && "bg-danger",
                        item.tone === "blue" && "bg-primary",
                        item.tone === "amber" && "bg-accent",
                        item.tone === "neutral" && "bg-subtle-foreground",
                        item.tone === "gray" && "bg-subtle-foreground",
                      )}
                    />
                    <p className="text-[12.5px] leading-snug text-foreground">
                      <span className="font-medium">{item.actor}</span>{" "}
                      <span className="text-muted-foreground">{item.action}</span>{" "}
                      <span className="font-mono text-[11.5px] text-foreground">{item.target}</span>
                    </p>
                    <span className="font-mono text-[10.5px] text-subtle-foreground">{item.time} ago</span>
                  </li>
                ))}
              </ol>
            </Panel>
          </section>

          <section>
            <SectionHead title="Recently analyzed" />
            <Panel className="p-4">
              <div className="flex items-center gap-2.5">
                <div className="flex h-9 w-9 items-center justify-center rounded-[var(--radius-md)] bg-foreground text-canvas">
                  <Boxes className="h-4.5 w-4.5" strokeWidth={2} />
                </div>
                <div className="min-w-0">
                  <div className="truncate font-mono text-[12.5px] font-medium text-foreground">
                    {repoFullName}
                  </div>
                  <div className="font-mono text-[11px] text-subtle-foreground">
                    {repository.language} · {repository.loc.toLocaleString()} LOC
                  </div>
                </div>
              </div>
              <div className="mt-3.5 space-y-2.5">
                <MiniStat label="Symbols indexed" value="8,421" pct={100} />
                <MiniStat label="Types resolved" value="1,140" pct={92} />
                <MiniStat label="References mapped" value="26,308" pct={78} />
              </div>
              <Link
                to="/projects"
                className="mt-4 flex items-center justify-center gap-1.5 rounded-[var(--radius-md)] border border-border bg-surface-2 py-2 text-[12.5px] font-medium text-foreground transition-colors hover:bg-surface-3"
              >
                Open project workspace
                <ArrowRight className="h-3.5 w-3.5" />
              </Link>
            </Panel>
          </section>

          <section>
            <SectionHead title="Shipped recently" />
            <Panel className="divide-y divide-border overflow-hidden">
              <div className="flex items-center gap-2.5 px-4 py-3">
                <GitPullRequest className="h-4 w-4 text-success" />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[12.5px] font-medium text-foreground">Enforce role claims on customer endpoints</div>
                  <div className="font-mono text-[11px] text-subtle-foreground">#412 · merged 2h ago</div>
                </div>
                <Badge tone="green">Merged</Badge>
              </div>
            </Panel>
          </section>
        </div>
      </div>
    </PageContainer>
  )
}

function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: string }) {
  return (
    <div className="bg-surface px-4 py-3">
      <div className="flex items-center gap-1.5 text-subtle-foreground">
        {icon}
        <span className="tech-label">{label}</span>
      </div>
      <div className="mt-1 font-mono text-[15px] font-semibold text-foreground">{value}</div>
    </div>
  )
}

function ApprovalRow({ id, title, branch, files }: { id: string; title: string; branch: string; files: number }) {
  return (
    <div className="flex items-center gap-3 border-b border-border px-4 py-3 last:border-b-0">
      <StatusDot tone="amber" />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="font-mono text-[11px] text-subtle-foreground">{id}</span>
          <span className="truncate text-[13px] font-medium text-foreground">{title}</span>
        </div>
        <div className="font-mono text-[11px] text-subtle-foreground">
          {branch} · {files} files
        </div>
      </div>
      <Link to={`/tasks/${id}`}>
        <Button variant="default" size="sm" className="gap-1.5">
          Review plan
          <ArrowRight className="h-3.5 w-3.5" />
        </Button>
      </Link>
    </div>
  )
}

function MiniStat({ label, value, pct }: { label: string; value: string; pct: number }) {
  return (
    <div>
      <div className="mb-1 flex items-center justify-between text-[12px]">
        <span className="text-muted-foreground">{label}</span>
        <span className="font-mono text-foreground">{value}</span>
      </div>
      <Meter value={pct} tone="blue" />
    </div>
  )
}

function Empty({ text }: { text: string }) {
  return <div className="px-4 py-6 text-center text-[13px] text-subtle-foreground">{text}</div>
}
