import { useEffect, useMemo, useRef, useState } from "react"
import { Link, useNavigate, useParams } from "react-router-dom"
import {
  ArrowLeft,
  Check,
  CircleDot,
  Clock,
  Coins,
  Cpu,
  Eye,
  FileText,
  GitBranch,
  Hammer,
  Pause,
  Play,
  Pencil,
  FlaskConical,
  StickyNote,
  CircleCheck,
  CircleAlert,
  Terminal,
  X,
  RotateCcw,
  OctagonAlert,
  ShieldAlert,
} from "lucide-react"
import { Button, Badge, Panel, Meter, StatusDot } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"
import {
  runDetails,
  runStatusMeta,
  stages,
  type ActivityEntry,
  type ActivityKind,
  type RunDetail,
  type RunAgent,
  type Tone,
} from "@/data/mock"

const kindMeta: Record<ActivityKind, { icon: typeof Eye; tone: Tone }> = {
  read: { icon: Eye, tone: "neutral" },
  edit: { icon: Pencil, tone: "amber" },
  run: { icon: Terminal, tone: "blue" },
  build: { icon: Hammer, tone: "blue" },
  test: { icon: FlaskConical, tone: "blue" },
  note: { icon: StickyNote, tone: "neutral" },
  success: { icon: CircleCheck, tone: "green" },
  error: { icon: CircleAlert, tone: "red" },
}

const toneChip: Record<Tone, string> = {
  neutral: "bg-surface-3 text-muted-foreground",
  gray: "bg-surface-3 text-muted-foreground",
  blue: "bg-primary-soft text-primary",
  amber: "bg-accent-soft text-accent",
  green: "bg-success-soft text-success",
  red: "bg-danger-soft text-danger",
}

const agentTone: Record<RunAgent["status"], Tone> = {
  done: "green",
  active: "blue",
  idle: "neutral",
  failed: "red",
  blocked: "amber",
}

export function ExecutionWorkspace() {
  const navigate = useNavigate()
  const { id } = useParams()
  const run: RunDetail = useMemo(() => runDetails[id ?? "EXEC-142"] ?? runDetails["EXEC-142"], [id])

  const currentStageIndex = stages.findIndex((s) => s.key === run.currentStage)
  const streaming = run.stream
  const total = run.activity.length

  const [visible, setVisible] = useState(streaming ? 6 : total)
  const [paused, setPaused] = useState(false)
  const feedRef = useRef<HTMLDivElement>(null)

  // reset when the run changes
  useEffect(() => {
    setVisible(streaming ? 6 : total)
    setPaused(false)
  }, [id, streaming, total])

  useEffect(() => {
    if (!streaming || paused || visible >= total) return
    const t = setTimeout(() => setVisible((v) => Math.min(v + 1, total)), 1100)
    return () => clearTimeout(t)
  }, [visible, paused, streaming, total])

  useEffect(() => {
    feedRef.current?.scrollTo({ top: feedRef.current.scrollHeight, behavior: "smooth" })
  }, [visible])

  const shown: ActivityEntry[] = run.activity.slice(0, visible)
  const streamDone = !streaming || visible >= total

  // effective status: a streaming run that finished becomes review-ready
  const status = streaming && streamDone ? "review-ready" : run.status
  const sMeta = runStatusMeta[status]
  const isFailed = status === "failed"
  const isBlocked = status === "blocked"
  const isReviewable = status === "review-ready" || status === "merged"
  const isRunning = status === "running"

  return (
    <div className="w-full">
      {/* header */}
      <div className="sticky top-0 z-10 border-b border-border bg-canvas/85 px-6 py-3 backdrop-blur-sm">
        <div className="mx-auto flex max-w-[1500px] items-center gap-3">
          <Link
            to="/executions"
            className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] text-muted-foreground hover:bg-surface-3 hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <StatusDot tone={sMeta.tone} pulse={isRunning} />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="font-mono text-[11px] text-subtle-foreground">{run.id}</span>
              <h1 className="truncate text-[14.5px] font-semibold text-foreground">{run.title}</h1>
              <Badge tone={sMeta.tone}>{sMeta.label}</Badge>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              {run.branch}
            </div>
          </div>
          {isRunning && (
            <Button variant="default" size="sm" onClick={() => setPaused((p) => !p)}>
              {paused ? <Play className="h-3.5 w-3.5" /> : <Pause className="h-3.5 w-3.5" />}
              {paused ? "Resume" : "Pause"}
            </Button>
          )}
          {isFailed && (
            <Button variant="default" size="sm">
              <RotateCcw className="h-3.5 w-3.5" />
              Retry run
            </Button>
          )}
          <Button
            variant="primary"
            size="sm"
            disabled={!isReviewable}
            onClick={() => navigate(`/review/${run.taskId}`)}
          >
            <Eye className="h-3.5 w-3.5" />
            Review diff
          </Button>
        </div>
      </div>

      <div className="mx-auto grid max-w-[1500px] grid-cols-1 gap-0 lg:grid-cols-[240px_minmax(0,1fr)_320px]">
        {/* LEFT — stage rail */}
        <aside className="border-b border-border p-5 lg:border-b-0 lg:border-r">
          <div className="tech-label mb-3">Pipeline</div>
          <ol className="relative">
            {stages.map((st, i) => {
              let state: "done" | "active" | "todo" | "failed" | "blocked"
              if (status === "merged") state = "done"
              else if (i < currentStageIndex) state = "done"
              else if (i === currentStageIndex)
                state = isFailed ? "failed" : isBlocked ? "blocked" : isReviewable ? "done" : "active"
              else state = "todo"

              return (
                <li key={st.key} className="relative flex gap-3 pb-5 last:pb-0">
                  {i < stages.length - 1 && (
                    <span
                      className={cn(
                        "absolute left-[9px] top-5 h-full w-px",
                        state === "done" ? "bg-success/50" : "bg-border",
                      )}
                    />
                  )}
                  <span
                    className={cn(
                      "relative z-10 flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-full border",
                      state === "done"
                        ? "border-success bg-success text-primary-foreground"
                        : state === "active"
                          ? "border-primary bg-surface"
                          : state === "failed"
                            ? "border-danger bg-danger text-primary-foreground"
                            : state === "blocked"
                              ? "border-accent bg-accent text-primary-foreground"
                              : "border-border bg-surface",
                    )}
                  >
                    {state === "done" ? (
                      <Check className="h-2.5 w-2.5" />
                    ) : state === "active" ? (
                      <CircleDot className="h-3 w-3 animate-pulse-dot text-primary" />
                    ) : state === "failed" ? (
                      <X className="h-2.5 w-2.5" />
                    ) : state === "blocked" ? (
                      <Pause className="h-2 w-2" />
                    ) : (
                      <span className="h-1.5 w-1.5 rounded-full bg-subtle-foreground" />
                    )}
                  </span>
                  <div className="pt-px">
                    <div
                      className={cn(
                        "text-[12.5px] font-medium",
                        state === "todo" ? "text-subtle-foreground" : "text-foreground",
                      )}
                    >
                      {st.label}
                    </div>
                    {state === "active" && <span className="font-mono text-[10.5px] text-primary">in progress</span>}
                    {state === "failed" && <span className="font-mono text-[10.5px] text-danger">failed here</span>}
                    {state === "blocked" && <span className="font-mono text-[10.5px] text-accent">blocked here</span>}
                  </div>
                </li>
              )
            })}
          </ol>

          <div className="tech-label mb-2 mt-4">Agents</div>
          <div className="space-y-2">
            {run.agents.map((a) => (
              <div
                key={a.role}
                className="flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-2"
              >
                <StatusDot tone={agentTone[a.status]} pulse={a.status === "active"} />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-1.5">
                    <span className="text-[12px] font-medium text-foreground">{a.role}</span>
                  </div>
                  <div className="truncate font-mono text-[10px] text-subtle-foreground">{a.note}</div>
                </div>
              </div>
            ))}
          </div>
        </aside>

        {/* CENTER — activity stream */}
        <section className="flex min-h-[calc(100vh-113px)] flex-col border-b border-border lg:border-b-0">
          <div className="flex items-center justify-between border-b border-border px-5 py-3">
            <div className="flex items-center gap-2">
              <Terminal className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="text-[13px] font-semibold text-foreground">Live activity</span>
            </div>
            <span className="font-mono text-[11px] text-subtle-foreground">
              {shown.length}/{total} events
            </span>
          </div>
          <div ref={feedRef} className="flex-1 overflow-y-auto px-5 py-4">
            <div className="relative">
              {shown.map((entry, i) => {
                const meta = kindMeta[entry.kind]
                const Icon = meta.icon
                return (
                  <div key={entry.id} className="relative flex gap-3 pb-4 last:pb-0 animate-fade-rise">
                    {i < shown.length - 1 && <span className="absolute left-[13px] top-7 h-full w-px bg-border" />}
                    <span
                      className={cn(
                        "relative z-10 flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded-[var(--radius-md)] border border-border/60",
                        toneChip[meta.tone],
                      )}
                    >
                      <Icon className="h-3.5 w-3.5" />
                    </span>
                    <div className="min-w-0 flex-1 pt-0.5">
                      <div className="flex items-center gap-2">
                        <span className="text-[13px] font-medium text-foreground">{entry.message}</span>
                        <span className="ml-auto font-mono text-[10.5px] text-subtle-foreground">{entry.time}</span>
                      </div>
                      <div className="mt-0.5 flex items-center gap-2">
                        <Badge tone="neutral">{entry.agent}</Badge>
                        {entry.detail && (
                          <span className="truncate font-mono text-[11px] text-subtle-foreground">{entry.detail}</span>
                        )}
                      </div>
                    </div>
                  </div>
                )
              })}

              {isRunning && !paused && (
                <div className="flex items-center gap-2 pl-9 font-mono text-[11px] text-subtle-foreground">
                  <span className="h-1.5 w-1.5 animate-pulse-dot rounded-full bg-primary" />
                  working…
                </div>
              )}
              {isRunning && paused && (
                <div className="flex items-center gap-2 pl-9 font-mono text-[11px] text-accent">
                  <Pause className="h-3 w-3" />
                  paused by you
                </div>
              )}

              {/* terminal-state banners */}
              {isReviewable && (
                <div className="ml-9 mt-2 rounded-[var(--radius-md)] border border-success/25 bg-success-soft px-3 py-2.5">
                  <div className="flex items-center gap-2 text-[12.5px] font-semibold text-success">
                    <CircleCheck className="h-4 w-4" />
                    {status === "merged"
                      ? "Merged to master — pull request closed"
                      : "Execution complete — diff ready for review"}
                  </div>
                </div>
              )}

              {run.alert && (isFailed || isBlocked) && (
                <div
                  className={cn(
                    "ml-9 mt-2 overflow-hidden rounded-[var(--radius-lg)] border",
                    isFailed ? "border-danger/40 bg-danger-soft" : "border-accent-line/60 bg-accent-soft",
                  )}
                >
                  <div className="flex items-start gap-2.5 px-4 pt-3.5">
                    {isFailed ? (
                      <OctagonAlert className="mt-0.5 h-4 w-4 shrink-0 text-danger" />
                    ) : (
                      <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-accent" />
                    )}
                    <div className="min-w-0">
                      <div className={cn("text-[13px] font-semibold", isFailed ? "text-danger" : "text-accent")}>
                        {run.alert.title}
                      </div>
                      <div className="mt-0.5 font-mono text-[10.5px] text-subtle-foreground">{run.alert.at}</div>
                    </div>
                  </div>
                  <p className="px-4 pt-2 text-[12.5px] leading-relaxed text-foreground text-pretty">
                    {run.alert.detail}
                  </p>

                  {run.alert.logExcerpt && (
                    <pre className="mx-4 mt-3 overflow-x-auto rounded-[var(--radius-md)] border border-border bg-surface px-3 py-2.5 font-mono text-[11px] leading-relaxed text-muted-foreground">
                      <code>{run.alert.logExcerpt}</code>
                    </pre>
                  )}

                  <div className="flex flex-wrap gap-2 px-4 pb-4 pt-3">
                    {run.alert.remediations.map((r) => (
                      <Button
                        key={r.label}
                        variant={r.primary ? "primary" : r.label.toLowerCase().includes("cancel") || r.label.toLowerCase().includes("abandon") ? "danger" : "default"}
                        size="sm"
                      >
                        {r.label}
                      </Button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </div>
        </section>

        {/* RIGHT — run telemetry */}
        <aside className="p-5 lg:border-l lg:border-border">
          <div className="tech-label mb-3">Run telemetry</div>
          <div className="space-y-3">
            <Panel className="p-3.5">
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-1.5 tech-label">
                  <Clock className="h-3 w-3" /> Elapsed
                </span>
                <span className="font-mono text-[13px] font-semibold text-foreground">{run.elapsed}</span>
              </div>
            </Panel>

            <Panel className="p-3.5">
              <div className="mb-2 flex items-center justify-between">
                <span className="flex items-center gap-1.5 tech-label">
                  <Cpu className="h-3 w-3" /> Token budget
                </span>
                <span className="font-mono text-[11px] text-muted-foreground">
                  {(run.tokensUsed / 1000).toFixed(0)}k / {(run.tokenBudget / 1000).toFixed(0)}k
                </span>
              </div>
              <Meter
                value={(run.tokensUsed / run.tokenBudget) * 100}
                tone={run.tokensUsed / run.tokenBudget > 0.85 ? "amber" : "blue"}
              />
            </Panel>

            <div className="grid grid-cols-2 gap-3">
              <Panel className="p-3.5">
                <span className="flex items-center gap-1.5 tech-label">
                  <Coins className="h-3 w-3" /> Cost
                </span>
                <div className="mt-1.5 font-mono text-[15px] font-semibold text-foreground">
                  ${run.estCost.toFixed(2)}
                </div>
              </Panel>
              <Panel className="p-3.5">
                <span className="flex items-center gap-1.5 tech-label">
                  <Cpu className="h-3 w-3" /> Model
                </span>
                <div className="mt-1.5 truncate font-mono text-[11px] text-foreground">{run.model}</div>
              </Panel>
            </div>
          </div>

          <div className="tech-label mb-2 mt-5">Build &amp; test</div>
          <Panel className="p-3.5">
            <div className="flex items-center gap-2 text-[12.5px]">
              <Hammer
                className={cn(
                  "h-3.5 w-3.5",
                  run.build.status === "passed"
                    ? "text-success"
                    : run.build.status === "failed"
                      ? "text-danger"
                      : "text-subtle-foreground",
                )}
              />
              <span className="text-foreground">Build</span>
              <Badge
                tone={run.build.status === "passed" ? "green" : run.build.status === "failed" ? "red" : "neutral"}
                className="ml-auto"
              >
                {run.build.status}
              </Badge>
            </div>
            <div className="mt-1 pl-5.5 font-mono text-[10.5px] text-subtle-foreground">{run.build.detail}</div>

            <div className="mt-3 flex items-center gap-2 text-[12.5px]">
              <FlaskConical
                className={cn(
                  "h-3.5 w-3.5",
                  run.tests.status === "passed"
                    ? "text-success"
                    : run.tests.status === "failed"
                      ? "text-danger"
                      : "text-subtle-foreground",
                )}
              />
              <span className="text-foreground">Tests</span>
              {run.tests.status === "pending" ? (
                <Badge tone="neutral" className="ml-auto">
                  pending
                </Badge>
              ) : (
                <span className="ml-auto font-mono text-[11px]">
                  <span className="text-success">{run.tests.passed} passed</span>
                  <span className="text-subtle-foreground"> · </span>
                  <span className={run.tests.failed > 0 ? "text-danger" : "text-subtle-foreground"}>
                    {run.tests.failed} failed
                  </span>
                </span>
              )}
            </div>
            <div className="mt-1 pl-5.5 font-mono text-[10.5px] text-subtle-foreground">{run.tests.detail}</div>
          </Panel>

          <Button
            variant="default"
            size="md"
            className="mt-4 w-full"
            disabled={!isReviewable}
            onClick={() => navigate(`/review/${run.taskId}`)}
          >
            <FileText className="h-3.5 w-3.5" />
            Open diff review
          </Button>
        </aside>
      </div>
    </div>
  )
}
