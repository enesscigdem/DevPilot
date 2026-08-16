import { useState } from "react"
import { Link } from "react-router-dom"
import {
  ArrowLeft,
  Check,
  GitBranch,
  GitPullRequest,
  Hammer,
  FlaskConical,
  ShieldCheck,
  TriangleAlert,
  FileCode2,
} from "lucide-react"
import { PageContainer } from "@/components/shared"
import { Button, Badge, Panel, StatusDot } from "@/components/ui/primitives"
import { diffFiles, reviewSummary, type DiffLine } from "@/data/mock"

function DiffRow({ line }: { line: DiffLine }) {
  if (line.type === "hunk") {
    return (
      <div className="bg-primary-soft/40 px-3 py-1 font-mono text-[11px] text-primary">
        {line.content}
      </div>
    )
  }
  const tone =
    line.type === "add"
      ? "bg-success-soft/60"
      : line.type === "del"
        ? "bg-danger-soft/60"
        : ""
  const sign = line.type === "add" ? "+" : line.type === "del" ? "−" : " "
  const signColor =
    line.type === "add" ? "text-success" : line.type === "del" ? "text-danger" : "text-subtle-foreground"
  return (
    <div className={"flex font-mono text-[12px] leading-[1.6] " + tone}>
      <span className="w-10 shrink-0 select-none border-r border-border/60 px-2 text-right text-subtle-foreground">
        {line.oldNo ?? ""}
      </span>
      <span className="w-10 shrink-0 select-none border-r border-border/60 px-2 text-right text-subtle-foreground">
        {line.newNo ?? ""}
      </span>
      <span className={"w-5 shrink-0 select-none text-center " + signColor}>{sign}</span>
      <code className="whitespace-pre pr-4 text-foreground">{line.content || " "}</code>
    </div>
  )
}

export function CodeReview() {
  const [active, setActive] = useState(diffFiles[0].path)
  const file = diffFiles.find((f) => f.path === active) ?? diffFiles[0]

  return (
    <PageContainer className="max-w-none px-0 py-0">
      {/* header */}
      <div className="sticky top-0 z-10 border-b border-border bg-canvas/85 px-6 py-3 backdrop-blur-sm">
        <div className="mx-auto flex max-w-[1600px] items-center gap-3">
          <Link
            to="/executions/EXEC-142"
            className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] text-muted-foreground hover:bg-surface-3 hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="font-mono text-[11px] text-subtle-foreground">TASK-142</span>
              <h1 className="truncate text-[14.5px] font-semibold text-foreground">Code review</h1>
              <Badge tone="green">reviewer approved</Badge>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              feat/orders-status-filter → master
            </div>
          </div>
          <Button variant="default" size="sm">
            Request changes
          </Button>
          <Button variant="primary" size="sm">
            <GitPullRequest className="h-3.5 w-3.5" />
            Open pull request
          </Button>
        </div>
      </div>

      <div className="mx-auto grid max-w-[1600px] grid-cols-1 gap-0 lg:grid-cols-[260px_minmax(0,1fr)_360px]">
        {/* LEFT — file tree */}
        <aside className="border-b border-border p-4 lg:border-b-0 lg:border-r">
          <div className="mb-3 flex items-center justify-between">
            <span className="tech-label">Changed files</span>
            <span className="font-mono text-[11px] text-subtle-foreground">{diffFiles.length}</span>
          </div>
          <div className="space-y-1">
            {diffFiles.map((f) => {
              const isActive = f.path === active
              return (
                <button
                  key={f.path}
                  onClick={() => setActive(f.path)}
                  className={
                    "flex w-full items-center gap-2 rounded-[var(--radius-md)] px-2.5 py-2 text-left transition-colors " +
                    (isActive ? "bg-primary-soft text-primary" : "text-muted-foreground hover:bg-surface-2 hover:text-foreground")
                  }
                >
                  <FileCode2 className="h-3.5 w-3.5 shrink-0" />
                  <span className="truncate text-[12.5px] font-medium">{f.name}</span>
                  <span className="ml-auto flex shrink-0 items-center gap-1 font-mono text-[10px]">
                    <span className="text-success">+{f.additions}</span>
                    <span className="text-danger">−{f.deletions}</span>
                  </span>
                </button>
              )
            })}
          </div>

          <div className="mt-5 rounded-[var(--radius-md)] border border-border bg-inset p-3">
            <div className="tech-label mb-2">Coverage</div>
            <div className="flex items-end gap-2">
              <span className="font-mono text-[11px] text-subtle-foreground line-through">
                {reviewSummary.coverage.before}%
              </span>
              <span className="font-mono text-[18px] font-semibold text-success">
                {reviewSummary.coverage.after}%
              </span>
              <span className="mb-0.5 font-mono text-[11px] text-success">
                +{(reviewSummary.coverage.after - reviewSummary.coverage.before).toFixed(1)}
              </span>
            </div>
          </div>
        </aside>

        {/* CENTER — diff */}
        <section className="min-w-0 border-b border-border lg:border-b-0">
          <div className="flex items-center justify-between border-b border-border px-4 py-2.5">
            <span className="font-mono text-[12px] text-foreground">{file.path}</span>
            <span className="flex items-center gap-1.5 font-mono text-[11px]">
              <span className="text-success">+{file.additions}</span>
              <span className="text-danger">−{file.deletions}</span>
            </span>
          </div>
          <div className="border-b border-border bg-surface-2 px-4 py-2 text-[12px] text-muted-foreground">
            {file.note}
          </div>
          <div className="overflow-x-auto bg-surface">
            {file.lines.map((line, i) => (
              <DiffRow key={i} line={line} />
            ))}
          </div>
        </section>

        {/* RIGHT — reviewer verdict */}
        <aside className="p-5 lg:border-l lg:border-border">
          <div className="tech-label mb-3">Reviewer verdict</div>

          <div className="grid grid-cols-2 gap-2.5">
            <Panel className="p-3">
              <div className="flex items-center gap-1.5">
                <Hammer className="h-3.5 w-3.5 text-success" />
                <span className="tech-label">Build</span>
              </div>
              <div className="mt-1 text-[13px] font-semibold text-success">Passed</div>
              <div className="mt-0.5 font-mono text-[10px] text-subtle-foreground">0 warnings</div>
            </Panel>
            <Panel className="p-3">
              <div className="flex items-center gap-1.5">
                <FlaskConical className="h-3.5 w-3.5 text-success" />
                <span className="tech-label">Tests</span>
              </div>
              <div className="mt-1 text-[13px] font-semibold text-success">
                {reviewSummary.tests.passed} passed
              </div>
              <div className="mt-0.5 font-mono text-[10px] text-subtle-foreground">
                +{reviewSummary.tests.added} new
              </div>
            </Panel>
          </div>

          <div className="tech-label mb-2 mt-5 flex items-center gap-1.5">
            <ShieldCheck className="h-3 w-3" /> Analysis
          </div>
          <div className="space-y-2">
            {reviewSummary.reviewerNotes.map((n, i) => (
              <div key={i} className="rounded-[var(--radius-md)] border border-border bg-surface p-3">
                <div className="flex items-center gap-2">
                  <StatusDot tone={n.tone} />
                  <span className="text-[12.5px] font-semibold text-foreground">{n.title}</span>
                </div>
                <p className="mt-1 text-[11.5px] leading-relaxed text-muted-foreground">{n.body}</p>
              </div>
            ))}
          </div>

          <div className="tech-label mb-2 mt-5 flex items-center gap-1.5">
            <TriangleAlert className="h-3 w-3" /> Risks
          </div>
          <div className="space-y-1.5">
            {reviewSummary.risks.map((r, i) => (
              <div key={i} className="flex gap-2 rounded-[var(--radius-md)] border border-border bg-inset px-2.5 py-2">
                <StatusDot tone={r.tone} className="mt-1" />
                <span className="text-[11.5px] leading-relaxed text-muted-foreground">{r.text}</span>
              </div>
            ))}
          </div>

          <Button variant="primary" size="lg" className="mt-5 w-full">
            <Check className="h-4 w-4" />
            Approve &amp; open PR
          </Button>
        </aside>
      </div>
    </PageContainer>
  )
}
