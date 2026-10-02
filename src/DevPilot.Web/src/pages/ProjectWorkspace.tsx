import { useCallback, useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { fmt, relativeTime, srv } from "@/i18n"
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
  if (!dateStr || isNaN(new Date(dateStr).getTime())) return "—"
  return relativeTime(dateStr)
}

import { getCachedWorkspaceAnalysis, setCachedWorkspaceAnalysis } from "@/lib/workspaceCache"

export function ProjectWorkspace() {
  const { t } = useTranslation()
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
      const msg = err instanceof Error ? err.message : t("repo.errLoad")
      const currentCache = getCachedWorkspaceAnalysis(workspaceId)
      if (!currentCache.data && !analysis) {
        setError(msg)
      }
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
    : analysis?.repository.fullName ?? t("repo.noWorkspace")
  const branchName = activeWorkspace
    ? activeWorkspace.branch
    : analysis?.repository.branch ?? "—"
  const repoName = activeWorkspace
    ? activeWorkspace.repository
    : analysis?.repository.repository ?? t("repo.repositoryFallback")
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
    ? t("repo.noWorkspace")
    : isLoading
      ? t("repo.analyzing")
      : error
        ? t("repo.analysisError")
        : analysis?.summary.status === "Ready"
          ? t("repo.analysisReady")
          : analysis?.summary.status === "Partial"
            ? t("repo.analysisPartial")
            : t("repo.analysisComplete")

  const engineText = analysis?.summary.engine ?? t("repo.engineFallback")
  const symbolsText = analysis ? fmt.number(analysis.summary.symbolsCount) : "—"
  const typesText = analysis ? fmt.number(analysis.summary.typesCount) : "—"
  const referencesText = analysis ? fmt.number(analysis.summary.referencesCount) : "—"
  const lastRunText = analysis ? formatRelativeTime(analysis.summary.analyzedAt) : "—"
  const steps = analysis?.summary.steps ?? []

  const fileTree = analysis?.fileTree ?? []
  const projects = analysis?.projects ?? []
  const technologies = analysis?.technologies ?? []
  const endpoints = analysis?.endpoints ?? []

  return (
    <PageContainer>
      <PageHeading
        eyebrow={t("repo.eyebrow")}
        title={repoFullName}
        description={t("repo.description")}
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
              {t("repo.reanalyze")}
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
          <IndexStat label={t("repo.symbols")} value={symbolsText} />
          <IndexStat label={t("repo.types")} value={typesText} />
          <IndexStat label={t("repo.references")} value={referencesText} />
          <IndexStat label={t("repo.lastRun")} value={lastRunText} mono />
          <div className="ml-auto flex items-center gap-1.5">
            {steps.map((s) => (
              <div key={s.label} className="flex items-center gap-1 rounded-full bg-success-soft px-2 py-0.5" title={srv(s.label)}>
                <CircleCheck className="h-3 w-3 text-success" />
                <span className="hidden font-mono text-[10.5px] text-success lg:inline">{srv(s.label)}</span>
              </div>
            ))}
          </div>
        </div>
      </Panel>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[300px_1fr]">
        {/* File tree */}
        <div>
          <SectionHead title={t("repo.structure")} />
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
                  {isLoading ? t("repo.loadingFiles") : error ? t("repo.failedFiles") : t("repo.noFiles")}
                </div>
              )}
            </div>
          </Panel>
        </div>

        <div className="flex flex-col gap-6">
          {/* Solution projects */}
          <section>
            <SectionHead title={t("repo.solution")} count={projects.length} />
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
              {projects.map((p) => {
                const tone = layerToneMap[p.layer] ?? "neutral"
                return (
                  <Panel key={p.name} className="p-3.5">
                    <div className="flex items-start justify-between">
                      <LayerIcon layer={p.layer} tone={tone} />
                      <span className="font-mono text-[11px] text-subtle-foreground">{t("common.files", { count: p.fileCount })}</span>
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
                  {t("repo.noProjects")}
                </Panel>
              )}
            </div>
          </section>

          {/* Technologies */}
          <section>
            <SectionHead title={t("repo.technologies")} count={technologies.length} />
            <Panel className="flex flex-wrap gap-2 p-4">
              {technologies.map((tech) => {
                const tone = techToneMap[tech.kind] ?? "neutral"
                return (
                  <div
                    key={tech.name}
                    className="flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface-2 py-1.5 pl-2.5 pr-3"
                  >
                    <span className={cn("h-1.5 w-1.5 rounded-full", tone === "blue" ? "bg-primary" : "bg-subtle-foreground")} />
                    <span className="text-[12.5px] font-medium text-foreground">{tech.name}</span>
                    {tech.version && <span className="font-mono text-[11px] text-subtle-foreground">{tech.version}</span>}
                  </div>
                )
              })}
              {technologies.length === 0 && !isLoading && (
                <span className="font-mono text-[12px] text-subtle-foreground">{t("repo.noTechnologies")}</span>
              )}
            </Panel>
          </section>

          {/* Endpoints */}
          <section>
            <SectionHead title={t("repo.endpoints")} count={endpoints.length} />
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
                <div className="px-4 py-3 font-mono text-[12px] text-subtle-foreground">{t("repo.noEndpoints")}</div>
              )}
            </Panel>
          </section>

          {/* Recent tasks */}
          <section>
            <SectionHead
              title={t("repo.recentTasks")}
              action={
                <Link to="/tasks" className="text-[12px] font-medium text-primary hover:underline">
                  {t("repo.allTasks")}
                </Link>
              }
            />
            <Panel className="overflow-hidden">
              <div className="px-4 py-3 font-mono text-[12px] text-subtle-foreground">
                {t("repo.noRecentTasks")}
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
