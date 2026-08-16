import { useState } from "react"
import { Link, useNavigate } from "react-router-dom"
import {
  ArrowLeft,
  Check,
  ChevronRight,
  FileCode2,
  GitBranch,
  Play,
  Pencil,
  Sparkles,
  ShieldCheck,
  Database,
  Network,
  FlaskConical,
  Plus,
} from "lucide-react"
import { PageContainer } from "@/components/shared"
import { Button, Panel, Badge, Meter, StatusDot, IconChip } from "@/components/ui/primitives"
import {
  activeTask,
  affectedFiles,
  impactSummary,
  statusMeta,
  riskMeta,
  type AffectedFile,
} from "@/data/mock"

const impactGroups = [
  { key: "apiChanges", label: "API surface", icon: Network, items: impactSummary.apiChanges },
  { key: "database", label: "Database", icon: Database, items: impactSummary.database },
  { key: "integrations", label: "Integrations", icon: ShieldCheck, items: impactSummary.integrations },
  { key: "tests", label: "Tests", icon: FlaskConical, items: impactSummary.tests },
] as const

export function TaskImpact() {
  const navigate = useNavigate()
  const [selected, setSelected] = useState<AffectedFile>(affectedFiles[0])
  const s = statusMeta[activeTask.status]
  const r = riskMeta[activeTask.risk]

  return (
    <PageContainer className="max-w-none px-0 py-0">
      {/* Sticky task header */}
      <div className="sticky top-0 z-10 border-b border-border bg-canvas/85 backdrop-blur-sm">
        <div className="mx-auto flex max-w-[1600px] items-center gap-3 px-6 py-3">
          <Link
            to="/tasks"
            className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] text-muted-foreground hover:bg-surface-3 hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="font-mono text-[11px] text-subtle-foreground">{activeTask.id}</span>
              <h1 className="truncate text-[15px] font-semibold text-foreground">{activeTask.title}</h1>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              {activeTask.branch}
            </div>
          </div>
          <Badge tone={s.tone}>{s.label}</Badge>
          <Badge tone={r.tone}>Risk: {r.label}</Badge>
          <div className="hidden items-center gap-1.5 md:flex">
            <span className="tech-label">Confidence</span>
            <span className="font-mono text-[13px] font-semibold text-foreground">{activeTask.confidence}%</span>
          </div>
        </div>
      </div>

      {/* Three-pane analysis */}
      <div className="mx-auto grid max-w-[1600px] grid-cols-1 gap-0 lg:grid-cols-[340px_minmax(0,1fr)_380px]">
        {/* LEFT — Requirement + plan */}
        <aside className="border-b border-border p-5 lg:border-b-0 lg:border-r">
          <div className="tech-label mb-2">Requirement</div>
          <p className="text-[13.5px] leading-relaxed text-foreground text-pretty">{activeTask.requirement}</p>

          <div className="tech-label mb-2 mt-6">Acceptance criteria</div>
          <ul className="space-y-2">
            {activeTask.acceptance.map((a, i) => (
              <li key={i} className="flex gap-2 text-[12.5px] leading-relaxed text-muted-foreground">
                <Check className="mt-0.5 h-3.5 w-3.5 shrink-0 text-success" />
                <span>{a}</span>
              </li>
            ))}
          </ul>

          <div className="tech-label mb-3 mt-6 flex items-center gap-1.5">
            <Sparkles className="h-3 w-3" />
            Proposed plan
          </div>
          <ol className="relative space-y-0 border-l border-border pl-0">
            {activeTask.planSteps.map((step, i) => (
              <li key={i} className="relative pb-4 pl-5 last:pb-0">
                <span className="absolute -left-[6.5px] top-1 flex h-3 w-3 items-center justify-center rounded-full border border-primary-ring bg-surface">
                  <span className="h-1.5 w-1.5 rounded-full bg-primary" />
                </span>
                <div className="text-[12.5px] font-semibold text-foreground">{step.title}</div>
                <p className="mt-0.5 text-[12px] leading-relaxed text-muted-foreground">{step.detail}</p>
                <div className="mt-1.5 flex flex-wrap gap-1">
                  {step.files.map((f) => (
                    <span key={f} className="font-mono text-[10.5px] text-subtle-foreground">
                      {f}
                    </span>
                  ))}
                </div>
              </li>
            ))}
          </ol>
        </aside>

        {/* CENTER — Affected files + inspector */}
        <section className="border-b border-border p-5 lg:border-b-0">
          <div className="mb-3 flex items-center justify-between">
            <div className="flex items-center gap-2">
              <h2 className="text-[13px] font-semibold text-foreground">Impact analysis</h2>
              <span className="rounded-full bg-surface-3 px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                {affectedFiles.length} files
              </span>
            </div>
            <div className="flex items-center gap-1.5 font-mono text-[11px]">
              <span className="text-success">
                +{affectedFiles.reduce((s, f) => s + f.additions, 0)}
              </span>
              <span className="text-danger">
                −{affectedFiles.reduce((s, f) => s + f.deletions, 0)}
              </span>
            </div>
          </div>

          <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,300px)]">
            {/* file list */}
            <div className="overflow-hidden rounded-[var(--radius-lg)] border border-border">
              {affectedFiles.map((f) => {
                const isSel = f.path === selected.path
                return (
                  <button
                    key={f.path}
                    onClick={() => setSelected(f)}
                    className={
                      "flex w-full items-center gap-2.5 border-b border-border px-3 py-2.5 text-left transition-colors last:border-b-0 " +
                      (isSel ? "bg-primary-soft/70" : "hover:bg-surface-2")
                    }
                  >
                    {f.changeType === "added" ? (
                      <Plus className="h-3.5 w-3.5 shrink-0 text-success" />
                    ) : (
                      <Pencil className="h-3.5 w-3.5 shrink-0 text-accent" />
                    )}
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <span className={"truncate text-[12.5px] font-medium " + (isSel ? "text-primary" : "text-foreground")}>
                          {f.name}
                        </span>
                      </div>
                      <div className="truncate font-mono text-[10.5px] text-subtle-foreground">{f.path}</div>
                    </div>
                    <div className="flex items-center gap-1.5 font-mono text-[10.5px]">
                      <span className="text-success">+{f.additions}</span>
                      <span className="text-danger">−{f.deletions}</span>
                    </div>
                    {isSel && <ChevronRight className="h-4 w-4 shrink-0 text-primary" />}
                  </button>
                )
              })}
            </div>

            {/* inspector */}
            <Panel className="h-fit p-4">
              <div className="mb-3 flex items-center gap-2">
                <IconChip tone="blue">
                  <FileCode2 className="h-4 w-4" />
                </IconChip>
                <div className="min-w-0">
                  <div className="truncate text-[13px] font-semibold text-foreground">{selected.name}</div>
                  <div className="font-mono text-[10.5px] text-subtle-foreground">{selected.project}</div>
                </div>
              </div>
              <div className="tech-label mb-1.5">Why it changes</div>
              <p className="text-[12.5px] leading-relaxed text-muted-foreground text-pretty">{selected.reason}</p>

              <div className="mt-4 flex items-center justify-between">
                <span className="tech-label">Confidence</span>
                <span className="font-mono text-[12px] font-semibold text-foreground">{selected.confidence}%</span>
              </div>
              <Meter
                value={selected.confidence}
                tone={selected.confidence >= 90 ? "green" : selected.confidence >= 80 ? "blue" : "amber"}
                className="mt-1.5"
              />

              <div className="mt-4 flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-inset px-2.5 py-2">
                <Badge tone={selected.changeType === "added" ? "green" : "amber"}>
                  {selected.changeType === "added" ? "New file" : "Modified"}
                </Badge>
                <span className="font-mono text-[11px] text-subtle-foreground">
                  +{selected.additions} −{selected.deletions}
                </span>
              </div>
            </Panel>
          </div>
        </section>

        {/* RIGHT — System impact + decision */}
        <aside className="p-5 lg:border-l lg:border-border">
          <div className="tech-label mb-3">System impact</div>
          <div className="space-y-2.5">
            {impactGroups.map((g) => {
              const item = g.items[0]
              const Icon = g.icon
              return (
                <div key={g.key} className="rounded-[var(--radius-md)] border border-border bg-surface p-3">
                  <div className="mb-1.5 flex items-center gap-2">
                    <Icon className="h-3.5 w-3.5 text-subtle-foreground" />
                    <span className="text-[12px] font-semibold text-foreground">{g.label}</span>
                    <StatusDot tone={item.tone} className="ml-auto" />
                  </div>
                  <div className="text-[12px] font-medium text-foreground">{item.label}</div>
                  <p className="mt-0.5 text-[11.5px] leading-relaxed text-muted-foreground">{item.detail}</p>
                </div>
              )
            })}
          </div>

          <div className="mt-6 rounded-[var(--radius-lg)] border border-primary-ring/50 bg-primary-soft/50 p-4">
            <div className="flex items-center gap-2">
              <ShieldCheck className="h-4 w-4 text-primary" />
              <span className="text-[13px] font-semibold text-foreground">Ready for your approval</span>
            </div>
            <p className="mt-1.5 text-[12px] leading-relaxed text-muted-foreground">
              DevPilot will implement the plan on branch{" "}
              <span className="font-mono text-foreground">{activeTask.branch}</span>, run the build and tests, then hand
              the diff back for review. Nothing merges without you.
            </p>
            <div className="mt-3 flex flex-col gap-2">
              <Button variant="primary" size="lg" className="w-full" onClick={() => navigate("/executions/EXEC-142")}>
                <Play className="h-4 w-4" />
                Approve &amp; execute
              </Button>
              <div className="flex gap-2">
                <Button variant="default" size="md" className="flex-1">
                  <Pencil className="h-3.5 w-3.5" />
                  Edit plan
                </Button>
                <Button variant="danger" size="md" className="flex-1">
                  Reject
                </Button>
              </div>
            </div>
          </div>
        </aside>
      </div>
    </PageContainer>
  )
}
