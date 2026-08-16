import { useState } from "react"
import { Layers, FileCode2, ArrowRight, Zap, Filter } from "lucide-react"
import { PageContainer, PageHeading } from "@/components/shared"
import { Button, Panel, Badge, StatusDot } from "@/components/ui/primitives"
import { archNodes, archEdges, type ArchNode } from "@/data/mock"

const NODE_W = 180
const NODE_H = 76
const CANVAS_W = 600
const CANVAS_H = 640

function toneBorder(tone: ArchNode["tone"], impacted?: boolean) {
  if (impacted) return "border-accent-line bg-accent-soft"
  const map: Record<string, string> = {
    blue: "border-primary-ring/60 bg-primary-soft/50",
    amber: "border-accent-line/60 bg-accent-soft/50",
    green: "border-success/30 bg-success-soft/50",
    neutral: "border-border bg-surface",
    gray: "border-border bg-surface-3",
    red: "border-danger/30 bg-danger-soft/50",
  }
  return map[tone] ?? "border-border bg-surface"
}

export function Architecture() {
  const [selected, setSelected] = useState<ArchNode>(archNodes[2])
  const [onlyImpacted, setOnlyImpacted] = useState(false)

  const nodeById = (id: string) => archNodes.find((n) => n.id === id)!
  const relatedIds = new Set<string>([selected.id, ...selected.incoming, ...selected.outgoing])

  return (
    <PageContainer>
      <PageHeading
        eyebrow="Architecture"
        title="Impact map"
        description="A live dependency graph of the solution, derived from the Roslyn symbol graph. Highlighted nodes are touched by the active task — trace how a change ripples across layers."
        actions={
          <Button variant={onlyImpacted ? "primary" : "default"} size="sm" onClick={() => setOnlyImpacted((v) => !v)}>
            <Filter className="h-3.5 w-3.5" />
            {onlyImpacted ? "Showing impacted" : "Highlight impacted"}
          </Button>
        }
      />

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_340px]">
        {/* graph */}
        <Panel className="relative overflow-hidden">
          <div className="dot-grid absolute inset-0 opacity-40" />
          <div className="relative overflow-x-auto p-6">
            <svg
              viewBox={`0 0 ${CANVAS_W} ${CANVAS_H}`}
              className="h-auto w-full min-w-[560px]"
              style={{ maxHeight: 640 }}
            >
              <defs>
                <marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto">
                  <path d="M0,0 L8,4 L0,8 Z" fill="var(--border-strong)" />
                </marker>
                <marker id="arrow-active" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto">
                  <path d="M0,0 L8,4 L0,8 Z" fill="var(--primary)" />
                </marker>
              </defs>

              {/* edges */}
              {archEdges.map((e, i) => {
                const from = nodeById(e.from)
                const to = nodeById(e.to)
                const x1 = from.x + NODE_W / 2
                const y1 = from.y + NODE_H
                const x2 = to.x + NODE_W / 2
                const y2 = to.y
                const isActive = relatedIds.has(e.from) && relatedIds.has(e.to)
                const dimmed = onlyImpacted && !(from.impacted && to.impacted)
                const midY = (y1 + y2) / 2
                const path =
                  from.y === to.y
                    ? `M ${from.x + NODE_W} ${from.y + NODE_H / 2} L ${to.x} ${to.y + NODE_H / 2}`
                    : `M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}`
                return (
                  <path
                    key={i}
                    d={path}
                    fill="none"
                    stroke={isActive ? "var(--primary)" : "var(--border-strong)"}
                    strokeWidth={isActive ? 2 : 1.25}
                    strokeOpacity={dimmed ? 0.15 : isActive ? 1 : 0.6}
                    markerEnd={isActive ? "url(#arrow-active)" : "url(#arrow)"}
                  />
                )
              })}

              {/* nodes */}
              {archNodes.map((n) => {
                const isSel = n.id === selected.id
                const dimmed = onlyImpacted && !n.impacted
                return (
                  <foreignObject
                    key={n.id}
                    x={n.x}
                    y={n.y}
                    width={NODE_W}
                    height={NODE_H}
                    opacity={dimmed ? 0.3 : 1}
                    style={{ cursor: "pointer" }}
                    onClick={() => setSelected(n)}
                  >
                    <div
                      className={
                        "flex h-full flex-col justify-center rounded-[var(--radius-md)] border px-3 shadow-[var(--shadow-sm)] transition-all " +
                        toneBorder(n.tone, n.impacted) +
                        (isSel ? " ring-2 ring-primary ring-offset-2 ring-offset-surface" : "")
                      }
                    >
                      <div className="flex items-center gap-1.5">
                        <span className="truncate text-[12.5px] font-semibold text-foreground">{n.label}</span>
                        {n.impacted && <Zap className="h-3 w-3 shrink-0 text-accent" />}
                      </div>
                      <div className="mt-0.5 truncate font-mono text-[10px] text-subtle-foreground">{n.sub}</div>
                      <div className="mt-1 tech-label text-[9px]">{n.layer}</div>
                    </div>
                  </foreignObject>
                )
              })}
            </svg>
          </div>

          {/* legend */}
          <div className="relative flex items-center gap-4 border-t border-border px-4 py-2.5">
            <div className="flex items-center gap-1.5">
              <Zap className="h-3 w-3 text-accent" />
              <span className="text-[11px] text-muted-foreground">Impacted by active task</span>
            </div>
            <div className="flex items-center gap-1.5">
              <span className="h-0.5 w-5 rounded bg-primary" />
              <span className="text-[11px] text-muted-foreground">Selected dependency path</span>
            </div>
          </div>
        </Panel>

        {/* inspector */}
        <aside>
          <div className="tech-label mb-3">Node inspector</div>
          <Panel className="p-4">
            <div className="flex items-center gap-2">
              <div className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
                <Layers className="h-4 w-4" />
              </div>
              <div className="min-w-0">
                <div className="truncate text-[14px] font-semibold text-foreground">{selected.label}</div>
                <div className="font-mono text-[10.5px] text-subtle-foreground">{selected.sub}</div>
              </div>
              {selected.impacted && <Badge tone="amber" className="ml-auto">impacted</Badge>}
            </div>

            <div className="mt-3 flex items-center gap-2">
              <Badge tone={selected.tone}>{selected.layer}</Badge>
            </div>

            {selected.impacted && (
              <div className="mt-4 rounded-[var(--radius-md)] border border-accent-line/50 bg-accent-soft/50 p-3">
                <div className="tech-label mb-1 flex items-center gap-1.5 text-accent">
                  <Zap className="h-3 w-3" /> Why it&apos;s impacted
                </div>
                <p className="text-[12px] leading-relaxed text-foreground text-pretty">{selected.why}</p>
              </div>
            )}

            <div className="tech-label mb-2 mt-4">Dependencies</div>
            <div className="space-y-2.5">
              {selected.incoming.length > 0 && (
                <div>
                  <div className="mb-1 text-[11px] text-subtle-foreground">Depended on by</div>
                  {selected.incoming.map((id) => (
                    <button
                      key={id}
                      onClick={() => setSelected(nodeById(id))}
                      className="mb-1 flex w-full items-center gap-2 rounded-[var(--radius-sm)] border border-border bg-surface px-2.5 py-1.5 text-left hover:bg-surface-2"
                    >
                      <StatusDot tone={nodeById(id).tone} />
                      <span className="text-[12px] font-medium text-foreground">{nodeById(id).label}</span>
                      <ArrowRight className="ml-auto h-3 w-3 rotate-180 text-subtle-foreground" />
                    </button>
                  ))}
                </div>
              )}
              {selected.outgoing.length > 0 && (
                <div>
                  <div className="mb-1 text-[11px] text-subtle-foreground">Depends on</div>
                  {selected.outgoing.map((id) => (
                    <button
                      key={id}
                      onClick={() => setSelected(nodeById(id))}
                      className="mb-1 flex w-full items-center gap-2 rounded-[var(--radius-sm)] border border-border bg-surface px-2.5 py-1.5 text-left hover:bg-surface-2"
                    >
                      <StatusDot tone={nodeById(id).tone} />
                      <span className="text-[12px] font-medium text-foreground">{nodeById(id).label}</span>
                      <ArrowRight className="ml-auto h-3 w-3 text-subtle-foreground" />
                    </button>
                  ))}
                </div>
              )}
            </div>

            <div className="tech-label mb-2 mt-4">Key files</div>
            <div className="space-y-1">
              {selected.files.map((f) => (
                <div key={f} className="flex items-center gap-2 font-mono text-[11.5px] text-muted-foreground">
                  <FileCode2 className="h-3.5 w-3.5 text-subtle-foreground" />
                  {f}
                </div>
              ))}
            </div>
          </Panel>
        </aside>
      </div>
    </PageContainer>
  )
}
