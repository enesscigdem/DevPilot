import { useState } from "react"
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
import { Badge, Button, Panel, StatusDot } from "@/components/ui/primitives"
import {
  repository,
  indexer,
  technologies,
  projects,
  endpoints,
  fileTree,
  tasks,
  type FileNode,
} from "@/data/mock"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"

const methodTone: Record<string, string> = {
  GET: "text-primary",
  POST: "text-success",
  PUT: "text-accent",
  DELETE: "text-danger",
  PATCH: "text-accent",
}

export function ProjectWorkspace() {
  const { activeWorkspace } = useWorkspace()
  const repoFullName = activeWorkspace
    ? `${activeWorkspace.owner}/${activeWorkspace.repository}`
    : repository.fullName
  const branchName = activeWorkspace ? activeWorkspace.branch : repository.branch
  const repoName = activeWorkspace ? activeWorkspace.repository : repository.name

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
            <Button variant="default" size="md" className="gap-1.5">
              <RotateCcw className="h-3.5 w-3.5" />
              Re-analyze
            </Button>
          </>
        }
      />

      {/* Analyzer state strip */}
      <Panel className="mb-6 overflow-hidden">
        <div className="flex flex-wrap items-center gap-x-8 gap-y-3 px-4 py-3.5">
          <div className="flex items-center gap-2">
            <StatusDot tone="green" />
            <div>
              <div className="text-[13px] font-medium text-foreground">Analysis ready</div>
              <div className="font-mono text-[11px] text-subtle-foreground">{indexer.engine}</div>
            </div>
          </div>
          <div className="hidden h-8 w-px bg-border sm:block" />
          <IndexStat label="Symbols" value={indexer.symbols.toLocaleString()} />
          <IndexStat label="Types" value={indexer.types.toLocaleString()} />
          <IndexStat label="References" value={indexer.references.toLocaleString()} />
          <IndexStat label="Last run" value={indexer.lastRun} mono />
          <div className="ml-auto flex items-center gap-1.5">
            {indexer.steps.map((s) => (
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
              <span className="ml-auto">{repository.commit}</span>
            </div>
            <div className="max-h-[520px] overflow-y-auto p-1.5">
              {fileTree.map((node) => (
                <TreeNode key={node.path} node={node} depth={0} />
              ))}
            </div>
          </Panel>
        </div>

        <div className="flex flex-col gap-6">
          {/* Solution projects */}
          <section>
            <SectionHead title="Solution & projects" count={projects.length} />
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
              {projects.map((p) => (
                <Panel key={p.name} className="p-3.5">
                  <div className="flex items-start justify-between">
                    <LayerIcon layer={p.layer} tone={p.tone} />
                    <span className="font-mono text-[11px] text-subtle-foreground">{p.files} files</span>
                  </div>
                  <div className="mt-3 font-mono text-[13px] font-medium text-foreground">{p.name}</div>
                  <div className="mt-0.5 flex items-center gap-2 text-[11.5px] text-muted-foreground">
                    <span>{p.kind}</span>
                    <span className="text-border-strong">·</span>
                    <span>{p.layer}</span>
                  </div>
                </Panel>
              ))}
            </div>
          </section>

          {/* Technologies */}
          <section>
            <SectionHead title="Detected technologies" count={technologies.length} />
            <Panel className="flex flex-wrap gap-2 p-4">
              {technologies.map((t) => (
                <div
                  key={t.name}
                  className="flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface-2 py-1.5 pl-2.5 pr-3"
                >
                  <span className={cn("h-1.5 w-1.5 rounded-full", t.tone === "blue" ? "bg-primary" : "bg-subtle-foreground")} />
                  <span className="text-[12.5px] font-medium text-foreground">{t.name}</span>
                  <span className="font-mono text-[11px] text-subtle-foreground">{t.version}</span>
                </div>
              ))}
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
                  <span className={cn("w-14 shrink-0 font-semibold", methodTone[e.method])}>{e.method}</span>
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
              {tasks.slice(0, 4).map((t) => (
                <Link
                  key={t.id}
                  to={`/tasks/${t.id}`}
                  className="flex items-center gap-3 border-b border-border px-4 py-2.5 transition-colors last:border-b-0 hover:bg-surface-2"
                >
                  <span className="font-mono text-[11px] text-subtle-foreground">{t.id}</span>
                  <span className="flex-1 truncate text-[12.5px] text-foreground">{t.title}</span>
                  <Badge tone={t.status === "done" ? "green" : t.status === "failed" || t.status === "blocked" ? "red" : t.status === "executing" ? "blue" : "amber"}>
                    {t.status.replace("-", " ")}
                  </Badge>
                </Link>
              ))}
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

function TreeNode({ node, depth }: { node: FileNode; depth: number }) {
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
