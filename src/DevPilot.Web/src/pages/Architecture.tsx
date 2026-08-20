import { useEffect, useState, useMemo, useCallback } from "react"
import { Layers, FileCode2, ArrowRight, Zap, Filter, Loader2, AlertCircle } from "lucide-react"
import { PageContainer, PageHeading } from "@/components/shared"
import { Button, Panel, Badge, StatusDot } from "@/components/ui/primitives"
import { useWorkspace } from "@/lib/workspace"
import { getRepositoryWorkspaceArchitecture } from "@/api"
import { getCachedWorkspaceArchitecture, setCachedWorkspaceArchitecture } from "@/lib/workspaceCache"
import type { WorkspaceArchitecture, WorkspaceArchitectureNode, Tone } from "@/types"

const NODE_W = 210
const NODE_H = 80
const CANVAS_W = 660
const CANVAS_H = 660

interface LayoutNode extends WorkspaceArchitectureNode {
  x: number
  y: number
  tone: Tone
  files: string[]
}

function mapLayerToTone(layer: string): Tone {
  const lower = layer.toLowerCase()
  if (lower.includes("presentation") || lower.includes("web") || lower.includes("api") || lower.includes("ui")) {
    return "blue"
  }
  if (lower.includes("application") || lower.includes("core") || lower.includes("service")) {
    return "amber"
  }
  if (lower.includes("domain")) {
    return "green"
  }
  if (lower.includes("infrastructure") || lower.includes("persistence")) {
    return "neutral"
  }
  if (lower.includes("data") || lower.includes("database") || lower.includes("test")) {
    return "gray"
  }
  return "neutral"
}

function toneBorder(tone: Tone, impacted?: boolean) {
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

function computeNodeLayout(rawNodes: WorkspaceArchitectureNode[]): LayoutNode[] {
  const occupied = new Set<string>()

  // Position heuristics by layer
  return rawNodes.map((n) => {
    const tone = mapLayerToTone(n.layer)
    const lower = n.layer.toLowerCase()

    let row = 0
    let col = 0

    if (lower.includes("presentation") || lower.includes("frontend")) {
      row = 0
      col = 0
    } else if (lower.includes("web") || lower.includes("api")) {
      row = 1
      col = 0
    } else if (lower.includes("application")) {
      row = 2
      col = 0
    } else if (lower.includes("domain")) {
      row = 2
      col = 1
    } else if (lower.includes("infrastructure")) {
      row = 3
      col = 0
    } else if (lower.includes("data") || lower.includes("database")) {
      row = 3
      col = 1
    } else if (lower.includes("test")) {
      row = 1
      col = 1
    } else {
      row = 2
      col = 0
    }

    // Resolve slot collisions
    while (occupied.has(`${row},${col}`)) {
      if (col === 0) {
        col = 1
      } else {
        col = 0
        row += 1
      }
    }

    occupied.add(`${row},${col}`)

    const x = col === 0 ? 40 : 350
    const y = 50 + row * 150

    return {
      ...n,
      x,
      y,
      tone,
      files: n.keyFiles ?? [],
    }
  })
}

export function Architecture() {
  const { activeWorkspaceId } = useWorkspace()
  const cached = activeWorkspaceId ? getCachedWorkspaceArchitecture(activeWorkspaceId) : { data: null, isStale: true }
  const [architecture, setArchitecture] = useState<WorkspaceArchitecture | null>(cached.data)
  const [isLoading, setIsLoading] = useState(!cached.data && !!activeWorkspaceId)
  const [error, setError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(() => {
    if (cached.data?.nodes && cached.data.nodes.length > 0) {
      const defaultNode =
        cached.data.nodes.find((n) => n.layer.toLowerCase() === "application") ??
        cached.data.nodes.find((n) => n.layer.toLowerCase() === "domain") ??
        cached.data.nodes.find((n) => n.layer.toLowerCase() === "web") ??
        cached.data.nodes[0]
      return defaultNode?.id ?? null
    }
    return null
  })
  const [onlyImpacted, setOnlyImpacted] = useState(false)

  const fetchArchitecture = useCallback(async (workspaceId: string) => {
    const c = getCachedWorkspaceArchitecture(workspaceId)
    if (c.data) {
      setArchitecture(c.data)
      setIsLoading(false)
    } else {
      setIsLoading(true)
    }
    setError(null)
    try {
      const data = await getRepositoryWorkspaceArchitecture(workspaceId)
      setArchitecture(data)
      setCachedWorkspaceArchitecture(workspaceId, data)
      if (data.nodes.length > 0) {
        setSelectedId((prev) => {
          if (prev && data.nodes.some((n) => n.id === prev)) return prev
          const defaultNode =
            data.nodes.find((n) => n.layer.toLowerCase() === "application") ??
            data.nodes.find((n) => n.layer.toLowerCase() === "domain") ??
            data.nodes.find((n) => n.layer.toLowerCase() === "web") ??
            data.nodes[0]
          return defaultNode.id
        })
      } else {
        setSelectedId(null)
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to load architecture graph."
      setError(msg)
      setArchitecture((prev) => prev)
    } finally {
      setIsLoading(false)
    }
  }, [])

  useEffect(() => {
    if (activeWorkspaceId) {
      fetchArchitecture(activeWorkspaceId)
    } else {
      setArchitecture(null)
      setSelectedId(null)
      setError(null)
    }
  }, [activeWorkspaceId, fetchArchitecture])

  const layoutNodes = useMemo(() => {
    if (!architecture || !architecture.nodes) return []
    return computeNodeLayout(architecture.nodes)
  }, [architecture])

  const nodeMap = useMemo(() => {
    const map = new Map<string, LayoutNode>()
    for (const n of layoutNodes) {
      map.set(n.id, n)
    }
    return map
  }, [layoutNodes])

  const nodeById = useCallback((id: string): LayoutNode | undefined => nodeMap.get(id), [nodeMap])

  const selected = useMemo(() => {
    if (!selectedId && layoutNodes.length > 0) return layoutNodes[0]
    return layoutNodes.find((n) => n.id === selectedId) ?? layoutNodes[0] ?? null
  }, [layoutNodes, selectedId])

  const relatedIds = useMemo(() => {
    if (!selected) return new Set<string>()
    return new Set<string>([selected.id, ...(selected.incoming ?? []), ...(selected.outgoing ?? [])])
  }, [selected])

  const edges = useMemo(() => {
    if (!architecture?.edges) return []
    return architecture.edges.filter((e) => nodeMap.has(e.from) && nodeMap.has(e.to))
  }, [architecture, nodeMap])

  // Calculate dynamic canvas height if nodes span beyond standard canvas
  const canvasHeight = useMemo(() => {
    if (layoutNodes.length === 0) return CANVAS_H
    const maxY = Math.max(...layoutNodes.map((n) => n.y + NODE_H + 40))
    return Math.max(CANVAS_H, maxY)
  }, [layoutNodes])

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

          {isLoading ? (
            <div className="relative flex h-[560px] flex-col items-center justify-center gap-3 p-6 text-center">
              <Loader2 className="h-6 w-6 animate-spin text-primary" />
              <div className="text-[13px] text-muted-foreground">Analyzing solution architecture graph...</div>
            </div>
          ) : error ? (
            <div className="relative flex h-[560px] flex-col items-center justify-center gap-3 p-6 text-center">
              <AlertCircle className="h-6 w-6 text-danger" />
              <div className="text-[13px] font-medium text-foreground">Failed to load architecture</div>
              <div className="max-w-sm text-[12px] text-muted-foreground">{error}</div>
            </div>
          ) : !activeWorkspaceId ? (
            <div className="relative flex h-[560px] flex-col items-center justify-center gap-2 p-6 text-center">
              <div className="text-[13px] font-medium text-foreground">No repository workspace selected</div>
              <div className="text-[12px] text-muted-foreground">
                Select or create a workspace in the sidebar to inspect its architecture.
              </div>
            </div>
          ) : layoutNodes.length === 0 ? (
            <div className="relative flex h-[560px] flex-col items-center justify-center gap-2 p-6 text-center">
              <div className="text-[13px] font-medium text-foreground">No projects found</div>
              <div className="text-[12px] text-muted-foreground">
                The selected workspace does not contain any analyzable .NET projects or frontend modules.
              </div>
            </div>
          ) : (
            <div className="relative overflow-x-auto p-6">
              <svg
                viewBox={`0 0 ${CANVAS_W} ${canvasHeight}`}
                className="h-auto w-full min-w-[560px]"
                style={{ maxHeight: canvasHeight }}
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
                {edges.map((e, i) => {
                  const from = nodeById(e.from)
                  const to = nodeById(e.to)
                  if (!from || !to) return null

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
                {layoutNodes.map((n) => {
                  const isSel = selected && n.id === selected.id
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
                      onClick={() => setSelectedId(n.id)}
                    >
                      <div
                        className={
                          "flex h-full flex-col justify-center rounded-[var(--radius-md)] border px-3 shadow-[var(--shadow-sm)] transition-all " +
                          toneBorder(n.tone, n.impacted) +
                          (isSel ? " ring-2 ring-primary ring-offset-2 ring-offset-surface" : "")
                        }
                      >
                        <div className="flex items-center gap-1.5 min-w-0">
                          <span className="truncate text-[12.5px] font-semibold text-foreground" title={n.label}>{n.label}</span>
                          {n.impacted && <Zap className="h-3 w-3 shrink-0 text-accent" />}
                        </div>
                        <div className="mt-0.5 truncate font-mono text-[10px] text-subtle-foreground" title={n.sub}>{n.sub}</div>
                        <div className="mt-1 tech-label text-[9px]">{n.layer}</div>
                      </div>
                    </foreignObject>
                  )
                })}
              </svg>
            </div>
          )}

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
            {selected ? (
              <>
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

                {selected.impacted && selected.why && (
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
                      {selected.incoming.map((id) => {
                        const target = nodeById(id)
                        if (!target) return null
                        return (
                          <button
                            key={id}
                            onClick={() => setSelectedId(id)}
                            className="mb-1 flex w-full items-center gap-2 rounded-[var(--radius-sm)] border border-border bg-surface px-2.5 py-1.5 text-left hover:bg-surface-2"
                          >
                            <StatusDot tone={target.tone} />
                            <span className="text-[12px] font-medium text-foreground">{target.label}</span>
                            <ArrowRight className="ml-auto h-3 w-3 rotate-180 text-subtle-foreground" />
                          </button>
                        )
                      })}
                    </div>
                  )}
                  {selected.outgoing.length > 0 && (
                    <div>
                      <div className="mb-1 text-[11px] text-subtle-foreground">Depends on</div>
                      {selected.outgoing.map((id) => {
                        const target = nodeById(id)
                        if (!target) return null
                        return (
                          <button
                            key={id}
                            onClick={() => setSelectedId(id)}
                            className="mb-1 flex w-full items-center gap-2 rounded-[var(--radius-sm)] border border-border bg-surface px-2.5 py-1.5 text-left hover:bg-surface-2"
                          >
                            <StatusDot tone={target.tone} />
                            <span className="text-[12px] font-medium text-foreground">{target.label}</span>
                            <ArrowRight className="ml-auto h-3 w-3 text-subtle-foreground" />
                          </button>
                        )
                      })}
                    </div>
                  )}
                  {selected.incoming.length === 0 && selected.outgoing.length === 0 && (
                    <div className="text-[11.5px] text-muted-foreground">No project dependencies</div>
                  )}
                </div>

                <div className="tech-label mb-2 mt-4">Key files</div>
                <div className="space-y-1 max-h-52 overflow-y-auto pr-1">
                  {selected.files.length > 0 ? (
                    selected.files.map((f) => (
                      <div key={f} className="flex items-center gap-2 font-mono text-[11.5px] text-muted-foreground min-w-0" title={f}>
                        <FileCode2 className="h-3.5 w-3.5 shrink-0 text-subtle-foreground" />
                        <span className="truncate">{f}</span>
                      </div>
                    ))
                  ) : (
                    <div className="text-[11.5px] text-muted-foreground">No source files detected</div>
                  )}
                </div>
              </>
            ) : (
              <div className="py-6 text-center text-[12px] text-muted-foreground">
                Select a node to inspect its dependencies and key files.
              </div>
            )}
          </Panel>
        </aside>
      </div>
    </PageContainer>
  )
}
