import { Link } from "react-router-dom"
import { Activity, Clock, Coins, Cpu, ArrowUpRight } from "lucide-react"
import { PageContainer, PageHeading } from "@/components/shared"
import { Panel, Badge, StatusDot, Meter } from "@/components/ui/primitives"
import { execution, stages } from "@/data/mock"

interface Run {
  id: string
  taskId: string
  title: string
  stage: string
  tone: "blue" | "green" | "red" | "amber"
  progress: number
  elapsed: string
  live?: boolean
}

const runs: Run[] = [
  { id: "EXEC-142", taskId: "TASK-142", title: "Add status filtering to the orders endpoint", stage: "Review", tone: "blue", progress: 80, elapsed: "00:41", live: true },
  { id: "EXEC-141", taskId: "TASK-141", title: "Add pagination metadata to product listing", stage: "Implement", tone: "blue", progress: 52, elapsed: "00:18", live: true },
  { id: "EXEC-140", taskId: "TASK-140", title: "Cache product catalog responses", stage: "Blocked · cache invalidation", tone: "amber", progress: 44, elapsed: "02:05" },
  { id: "EXEC-139", taskId: "TASK-139", title: "Enforce role claims on customer endpoints", stage: "Merged", tone: "green", progress: 100, elapsed: "01:12" },
  { id: "EXEC-138", taskId: "TASK-138", title: "Migrate customer notifications to outbox", stage: "Failed · missing migration", tone: "red", progress: 61, elapsed: "00:54" },
]

export function Executions() {
  return (
    <PageContainer>
      <PageHeading
        eyebrow="Executions"
        title="Execution runs"
        description="Autonomous runs of approved plans. Each run streams the agents' activity, build and test results, and stops for review before opening a pull request."
      />

      {/* Live summary strip */}
      <div className="mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
        {[
          { icon: Activity, label: "Running", value: "2", tone: "blue" as const },
          { icon: Clock, label: "Avg. run time", value: "01:07", tone: "neutral" as const },
          { icon: Cpu, label: "Model", value: execution.model, tone: "neutral" as const, mono: true },
          { icon: Coins, label: "Spend today", value: "$4.86", tone: "green" as const },
        ].map((m) => (
          <Panel key={m.label} className="p-3.5">
            <div className="flex items-center gap-1.5">
              <m.icon className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="tech-label">{m.label}</span>
            </div>
            <div className={"mt-1.5 text-[18px] font-semibold text-foreground " + (m.mono ? "font-mono text-[14px]" : "")}>
              {m.value}
            </div>
          </Panel>
        ))}
      </div>

      <div className="space-y-3">
        {runs.map((run) => (
          <Link key={run.id} to={`/executions/${run.id}`}>
            <Panel className="group p-4 transition-colors hover:border-border-strong hover:bg-surface-2">
              <div className="flex items-center gap-3">
                <StatusDot tone={run.tone} pulse={run.live} />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="font-mono text-[11px] text-subtle-foreground">{run.id}</span>
                    <span className="truncate text-[13.5px] font-medium text-foreground">{run.title}</span>
                    {run.live && <Badge tone="blue">live</Badge>}
                  </div>
                  <div className="mt-0.5 font-mono text-[11px] text-subtle-foreground">
                    {run.taskId} · {run.stage}
                  </div>
                </div>
                <div className="hidden items-center gap-1.5 font-mono text-[11px] text-muted-foreground sm:flex">
                  <Clock className="h-3 w-3" />
                  {run.elapsed}
                </div>
                <ArrowUpRight className="h-4 w-4 text-subtle-foreground opacity-0 transition-opacity group-hover:opacity-100" />
              </div>
              <div className="mt-3 flex items-center gap-3">
                <Meter value={run.progress} tone={run.tone} className="flex-1" />
                <span className="font-mono text-[10.5px] text-subtle-foreground">{run.progress}%</span>
              </div>
              {/* stage rail */}
              <div className="mt-3 flex items-center gap-1">
                {stages.map((st, i) => {
                  const reached = (run.progress / 100) * stages.length > i
                  return (
                    <div key={st.key} className="flex flex-1 items-center gap-1">
                      <div
                        className={
                          "h-1 flex-1 rounded-full " +
                          (reached
                            ? run.tone === "red"
                              ? "bg-danger"
                              : run.tone === "amber"
                                ? "bg-accent"
                                : run.tone === "green"
                                  ? "bg-success"
                                  : "bg-primary"
                            : "bg-surface-3")
                        }
                      />
                    </div>
                  )
                })}
              </div>
            </Panel>
          </Link>
        ))}
      </div>
    </PageContainer>
  )
}
