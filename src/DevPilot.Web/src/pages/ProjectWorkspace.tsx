import { useCallback, useEffect, useState } from "react"
import { Link } from "react-router-dom"
import {
  ChevronRight,
  Folder,
  FileCode2,
  GitBranch,
  RotateCcw,
  Layers,
  Database,
  Server,
  Boxes,
  Lock,
  Globe,
  CircleCheck,
} from "lucide-react"
import { PageContainer, PageHeading, SectionHead } from "@/components/shared"
import { Button, Panel, StatusDot } from "@/components/ui/primitives"
import { getRepositoryWorkspaceAnalysis } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import type { WorkspaceAnalysis, WorkspaceFileNode } from "@/types"
import { cn } from "@/lib/utils"

const methodTone: Record<string, string> = {
  GET: "text-primary",
  POST: "text-success",
  PUT: "text-accent",
  DELETE: "text-danger",
  PATCH: "text-accent",
}

const layerToneMap: Record<string, "blue" | "amber" | "green" | "neutral" | "gray"> = {
  Web: "blue",
  Application: "amber",
  Domain: "green",
  Infrastructure: "neutral",
  Tests: "gray",
}

const techToneMap: Record<string, "blue" | "neutral"> = {
  runtime: "blue",
  framework: "blue",
  frontend: "blue",
  orm: "neutral",
  database: "neutral",
  library: "neutral",
  testing: "neutral",
  tooling: "neutral",
  styling: "neutral",
}

function formatRelativeTime(dateStr?: string): string {
  if (!dateStr) return "—"
  try {
    const d = new Date(dateStr)
    if (isNaN(d.getTime())) return "—"
    const now = new Date()
    const diffSec = Math.floor((now.getTime() - d.getTime()) / 1000)
    if (diffSec < 60) return "Just now"
    const diffMin = Math.floor(diffSec / 60)
    if (diffMin < 60) return `${diffMin}m ago`
    const diffHours = Math.floor(diffMin / 60)
    if (diffHours < 24) return `${diffHours}h ago`
    return d.toLocaleDateString()
  } catch {
    return "—"
  }
}

import { getCachedWorkspaceAnalysis, setCachedWorkspaceAnalysis } from "@/lib/workspaceCache"

export function ProjectWorkspace() {
  const { activeWorkspace, activeWorkspaceId } = useWorkspace()
  const cached = activeWorkspaceId ? getCachedWorkspaceAnalysis(activeWorkspaceId) : { data: null, isStale: true }
  const [analysis, setAnalysis] = useState<WorkspaceAnalysis | null>(cached.data)
  const [isLoading, setIsLoading] = useState(!cached.data && !!activeWorkspaceId)
  const [isRefreshing, setIsRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const fetchAnalysis = useCallback(async (workspaceId: string, isManual = false) => {
    if (isManual) {
      setIsRefreshing(true)
    } else {
      const c = getCachedWorkspaceAnalysis(workspaceId)
      if (c.data) {
        setAnalysis(c.data)
        setIsLoading(false)
      } else {
        setIsLoading(true)
      }
    }
    setError(null)
    try {
      const data = await getRepositoryWorkspaceAnalysis(workspaceId)
      setAnalysis(data)
      setCachedWorkspaceAnalysis(workspaceId, data)
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to load workspace analysis."
      setError(msg)
      setAnalysis((prev) => prev)
    } finally {
      setIsLoading(false)
      setIsRefreshing(false)
    }
  }, [])

  useEffect(() => {
    if (activeWorkspaceId) {
      fetchAnalysis(activeWorkspaceId)
    } else {
      setAnalysis(null)
      setError(null)
    }
  }, [activeWorkspaceId, fetchAnalysis])

  const handleReanalyze = () => {
    if (activeWorkspaceId && !isLoading && !isRefreshing) {
      fetchAnalysis(activeWorkspaceId, true)
    }
  }

  const repoFullName = activeWorkspace
    ? `${activeWorkspace.owner}/${activeWorkspace.repository}`
    : analysis?.repository.fullName ?? "No workspace selected"
  const branchName = activeWorkspace
    ? activeWorkspace.branch
    : analysis?.repository.branch ?? "—"
  const repoName = activeWorkspace
    ? activeWorkspace.repository
    : analysis?.repository.repository ?? "Repository"
  const commitSha = analysis?.repository.commitSha
    ? (analysis.repository.commitSha.length > 7 ? analysis.repository.commitSha.slice(0, 7) : analysis.repository.commitSha)
    : activeWorkspace?.commitSha
      ? activeWorkspace.commitSha.slice(0, 7)
      : "—"

  type Tone = "neutral" | "blue" | "amber" | "green" | "red" | "gray"
  const statusTone: Tone = !activeWorkspaceId
    ? "gray"
    : error
      ? "red"
      : analysis?.summary.status === "Ready"
        ? "green"
        : analysis?.summary.status === "Partial"
          ? "amber"
          : isLoading
            ? "blue"
            : "gray"

  const statusText = !activeWorkspaceId
    ? "No workspace selected"
    : isLoading
      ? "Analyzing workspace..."
      : error
        ? "Analysis error"
        : analysis?.summary.status === "Ready"
          ? "Analysis ready"
          : analysis?.summary.status === "Partial"
            ? "Analysis ready (partial)"
            : "Analysis complete"

  const engineText = analysis?.summary.engine ?? "Roslyn workspace analysis"
  const symbolsText = analysis ? analysis.summary.symbolsCount.toLocaleString() : "—"
  const typesText = analysis ? analysis.summary.typesCount.toLocaleString() : "—"
  const referencesText = analysis ? analysis.summary.referencesCount.toLocaleString() : "—"
  const lastRunText = analysis ? formatRelativeTime(analysis.summary.analyzedAt) : "—"
  const steps = analysis?.summary.steps ?? []

  const fileTree = analysis?.fileTree ?? []
  const projects = analysis?.projects ?? []
  const technologies = analysis?.technologies ?? []
  const endpoints = analysis?.endpoints ?? []

  return (
    <PageContainer>
      <PageHeading
        eyebrow="Project workspace"
        title={repoFullName}
        description="Structure, detected technologies and analyzer state derived from Roslyn workspace analysis of the master branch."
        actions={
          <>
            <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5 font-mono text-[12px] text-muted-foreground">
              <GitBranch className="h-3.5 w-3.5" />
              {branchName}
            </div>
            <Button
              variant="default"
              size="md"
              className="gap-1.5"
              onClick={handleReanalyze}
              disabled={!activeWorkspaceId || isLoading || isRefreshing}
            >
              <RotateCcw className={cn("h-3.5 w-3.5", (isLoading || isRefreshing) && "animate-spin")} />
              Re-analyze
            </Button>
          </>
        }
      />

      {/* Analyzer state strip */}
      <Panel className="mb-6 overflow-hidden">
        <div className="flex flex-wrap items-center gap-x-8 gap-y-3 px-4 py-3.5">
          <div className="flex items-center gap-2">
            <StatusDot tone={statusTone} />
            <div>
              <div className="text-[13px] font-medium text-foreground">{statusText}</div>
              <div className="font-mono text-[11px] text-subtle-foreground">{engineText}</div>
            </div>
          </div>
          <div className="hidden h-8 w-px bg-border sm:block" />
          <IndexStat label="Symbols" value={symbolsText} />
          <IndexStat label="Types" value={typesText} />
          <IndexStat label="References" value={referencesText} />
          <IndexStat label="Last run" value={lastRunText} mono />
          <div className="ml-auto flex items-center gap-1.5">
            {steps.map((s) => (
              <div key={s.label} className="flex items-center gap-1 rounded-full bg-success-soft px-2 py-0.5" title={s.label}>
                <CircleCheck className="h-3 w-3 text-success" />
                <span className="hidden font-mono text-[10.5px] text-success lg:inline">{s.label}</span>
              </div>
            ))}
          </div>
        </div>
      </Panel>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[300px_1fr]">
        {/* File tree */}
        <div>
          <SectionHead title="Repository structure" />
          <Panel className="overflow-hidden">
            <div className="flex items-center gap-2 border-b border-border bg-surface-2 px-3 py-2 font-mono text-[11px] text-subtle-foreground">
              <Folder className="h-3.5 w-3.5" />
              {repoName}
              <span className="ml-auto">{commitSha}</span>
            </div>
            <div className="max-h-[520px] overflow-y-auto p-1.5">
              {fileTree.length > 0 ? (
                fileTree.map((node) => (
                  <TreeNode key={node.path} node={node} depth={0} />
                ))
              ) : (
                <div className="p-3 font-mono text-[11.5px] text-subtle-foreground">
                  {isLoading ? "Loading repository files..." : error ? "Failed to load files" : "No files available"}
                </div>
              )}
            </div>
          </Panel>
        </div>

        <div className="flex flex-col gap-6">
          {/* Solution projects */}
          <section>
            <SectionHead title="Solution & projects" count={projects.length} />
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
              {projects.map((p) => {
                const tone = layerToneMap[p.layer] ?? "neutral"
                return (
                  <Panel key={p.name} className="p-3.5">
                    <div className="flex items-start justify-between">
                      <LayerIcon layer={p.layer} tone={tone} />
                      <span className="font-mono text-[11px] text-subtle-foreground">{p.fileCount} files</span>
                    </div>
                    <div className="mt-3 font-mono text-[13px] font-medium text-foreground">{p.name}</div>
                    <div className="mt-0.5 flex items-center gap-2 text-[11.5px] text-muted-foreground">
                      <span>{p.projectType}</span>
                      <span className="text-border-strong">·</span>
                      <span>{p.layer}</span>
                    </div>
                  </Panel>
                )
              })}
              {projects.length === 0 && !isLoading && (
                <Panel className="col-span-full p-4 font-mono text-[12px] text-subtle-foreground">
                  No solution projects discovered
                </Panel>
              )}
            </div>
          </section>

          {/* Technologies */}
          <section>
            <SectionHead title="Detected technologies" count={technologies.length} />
            <Panel className="flex flex-wrap gap-2 p-4">
              {technologies.map((t) => {
                const tone = techToneMap[t.kind] ?? "neutral"
                return (
                  <div
                    key={t.name}
                    className="flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface-2 py-1.5 pl-2.5 pr-3"
                  >
                    <span className={cn("h-1.5 w-1.5 rounded-full", tone === "blue" ? "bg-primary" : "bg-subtle-foreground")} />
                    <span className="text-[12.5px] font-medium text-foreground">{t.name}</span>
                    {t.version && <span className="font-mono text-[11px] text-subtle-foreground">{t.version}</span>}
                  </div>
                )
              })}
              {technologies.length === 0 && !isLoading && (
                <span className="font-mono text-[12px] text-subtle-foreground">No technologies detected</span>
              )}
            </Panel>
          </section>

          {/* Endpoints */}
          <section>
            <SectionHead title="Controllers & endpoints" count={endpoints.length} />
            <Panel className="overflow-hidden">
              {endpoints.map((e, i) => (
                <div
                  key={i}
                  className="flex items-center gap-3 border-b border-border px-4 py-2.5 font-mono text-[12px] last:border-b-0"
                >
                  <span className={cn("w-14 shrink-0 font-semibold", methodTone[e.method] ?? "text-primary")}>{e.method}</span>
                  <span className="flex-1 text-foreground">{e.route}</span>
                  <span className="hidden text-subtle-foreground md:inline">
                    {e.controller}.{e.action}
                  </span>
                  {e.auth ? (
                    <Lock className="h-3.5 w-3.5 text-muted-foreground" />
                  ) : (
                    <Globe className="h-3.5 w-3.5 text-subtle-foreground" />
                  )}
                </div>
              ))}
              {endpoints.length === 0 && !isLoading && (
                <div className="px-4 py-3 font-mono text-[12px] text-subtle-foreground">No controller endpoints discovered</div>
              )}
            </Panel>
          </section>

          {/* Recent tasks */}
          <section>
            <SectionHead
              title="Recent tasks in this repository"
              action={
                <Link to="/tasks" className="text-[12px] font-medium text-primary hover:underline">
                  All tasks
                </Link>
              }
            />
            <Panel className="overflow-hidden">
              <div className="px-4 py-3 font-mono text-[12px] text-subtle-foreground">
                No recent tasks in this repository
              </div>
            </Panel>
          </section>
        </div>
      </div>
    </PageContainer>
  )
}

function IndexStat({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <div className="tech-label">{label}</div>
      <div className={cn("text-[13.5px] font-semibold text-foreground", mono && "font-mono text-[12.5px]")}>{value}</div>
    </div>
  )
}

function LayerIcon({ layer, tone }: { layer: string; tone: string }) {
  const map: Record<string, React.ReactNode> = {
    Web: <Server className="h-4 w-4" />,
    Application: <Layers className="h-4 w-4" />,
    Domain: <Boxes className="h-4 w-4" />,
    Infrastructure: <Database className="h-4 w-4" />,
    Tests: <CircleCheck className="h-4 w-4" />,
  }
  return (
    <span
      className={cn(
        "flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)]",
        tone === "blue" && "bg-primary-soft text-primary",
        tone === "amber" && "bg-accent-soft text-accent",
        tone === "green" && "bg-success-soft text-success",
        (tone === "neutral" || tone === "gray") && "bg-surface-3 text-muted-foreground",
      )}
    >
      {map[layer] ?? <Folder className="h-4 w-4" />}
    </span>
  )
}

function TreeNode({ node, depth }: { node: WorkspaceFileNode; depth: number }) {
  const [open, setOpen] = useState(depth < 2)
  const pad = { paddingLeft: `${depth * 14 + 8}px` }

  if (node.type === "file") {
    return (
      <div
        style={pad}
        className="flex items-center gap-1.5 rounded-[var(--radius-sm)] py-[3px] pr-2 font-mono text-[12px] text-muted-foreground transition-colors hover:bg-surface-3 hover:text-foreground"
      >
        <FileCode2 className={cn("h-3.5 w-3.5 shrink-0", node.lang === "tsx" || node.lang === "ts" ? "text-primary/70" : "text-subtle-foreground")} />
        <span className="truncate">{node.name}</span>
      </div>
    )
  }

  return (
    <div>
      <button
        style={pad}
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-1 rounded-[var(--radius-sm)] py-[3px] pr-2 font-mono text-[12px] font-medium text-foreground transition-colors hover:bg-surface-3"
      >
        <ChevronRight className={cn("h-3.5 w-3.5 shrink-0 text-subtle-foreground transition-transform", open && "rotate-90")} />
        <Folder className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        <span className="truncate">{node.name}</span>
      </button>
      {open && node.children?.map((child) => <TreeNode key={child.path} node={child} depth={depth + 1} />)}
    </div>
  )
}
