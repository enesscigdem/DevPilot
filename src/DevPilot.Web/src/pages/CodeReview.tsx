import { useEffect, useState } from "react"
import { Link as RouterLink, useParams as useReactParams } from "react-router-dom"
import {
  ArrowLeft,
  GitBranch,
  GitPullRequest,
  Hammer,
  FlaskConical,
  FileCode2,
  Loader2,
  AlertCircle,
  AlertTriangle,
  Info,
} from "lucide-react"
import { PageContainer } from "@/components/shared"
import { Button, Badge, Panel } from "@/components/ui/primitives"
import { getExecutionReview } from "@/api"
import {
  getExecutionStatusMeta,
  type ExecutionReview,
  type ExecutionReviewFile,
} from "@/types"
import { cn } from "@/lib/utils"

interface ParsedLine {
  id: number
  type: "header" | "hunk" | "add" | "del" | "context" | "info"
  content: string
  oldNo?: number
  newNo?: number
  filePath?: string
}

function parseGitDiff(diffText: string): ParsedLine[] {
  if (!diffText) return []
  const rawLines = diffText.split("\n")
  const parsed: ParsedLine[] = []

  let currentOldLine: number | undefined = undefined
  let currentNewLine: number | undefined = undefined
  let currentFile: string | undefined = undefined

  for (let i = 0; i < rawLines.length; i++) {
    const line = rawLines[i]

    if (line.startsWith("diff --git ")) {
      const match = line.match(/b\/(.+)$/)
      if (match) {
        currentFile = match[1]
      }
    }

    if (
      line.startsWith("diff --git") ||
      line.startsWith("index ") ||
      line.startsWith("--- ") ||
      line.startsWith("+++ ") ||
      line.startsWith("old mode") ||
      line.startsWith("new mode") ||
      line.startsWith("new file") ||
      line.startsWith("deleted file") ||
      line.startsWith("similarity index") ||
      line.startsWith("rename from") ||
      line.startsWith("rename to")
    ) {
      parsed.push({
        id: i,
        type: "header",
        content: line,
        filePath: currentFile,
      })
      continue
    }

    if (line.startsWith("@@ ")) {
      const hunkMatch = line.match(/^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/)
      if (hunkMatch) {
        currentOldLine = parseInt(hunkMatch[1], 10)
        currentNewLine = parseInt(hunkMatch[2], 10)
      } else {
        currentOldLine = undefined
        currentNewLine = undefined
      }
      parsed.push({
        id: i,
        type: "hunk",
        content: line,
        filePath: currentFile,
      })
      continue
    }

    if (
      line.startsWith("[Redacted sensitive file content:") ||
      line.startsWith("[Binary file diff not shown:")
    ) {
      parsed.push({
        id: i,
        type: "info",
        content: line,
        filePath: currentFile,
      })
      continue
    }

    if (line.startsWith("+")) {
      parsed.push({
        id: i,
        type: "add",
        content: line,
        oldNo: undefined,
        newNo: currentNewLine !== undefined ? currentNewLine++ : undefined,
        filePath: currentFile,
      })
      continue
    }

    if (line.startsWith("-")) {
      parsed.push({
        id: i,
        type: "del",
        content: line,
        oldNo: currentOldLine !== undefined ? currentOldLine++ : undefined,
        newNo: undefined,
        filePath: currentFile,
      })
      continue
    }

    parsed.push({
      id: i,
      type: "context",
      content: line,
      oldNo: currentOldLine !== undefined ? currentOldLine++ : undefined,
      newNo: currentNewLine !== undefined ? currentNewLine++ : undefined,
      filePath: currentFile,
    })
  }

  return parsed
}

function DiffRow({ line }: { line: ParsedLine }) {
  if (line.type === "hunk") {
    return (
      <div className="border-y border-primary/20 bg-primary-soft/40 px-3 py-1 font-mono text-[11px] text-primary">
        {line.content}
      </div>
    )
  }

  if (line.type === "header") {
    return (
      <div
        id={line.content.startsWith("diff --git") && line.filePath ? `file-diff-${line.filePath}` : undefined}
        className="border-b border-border/40 bg-surface-2 px-3 py-1 font-mono text-[11px] text-subtle-foreground"
      >
        {line.content}
      </div>
    )
  }

  if (line.type === "info") {
    return (
      <div className="flex items-center gap-2 border-y border-accent/20 bg-amber-soft/60 px-4 py-2 font-mono text-[12px] font-medium text-accent">
        <Info className="h-3.5 w-3.5 shrink-0" />
        <span>{line.content}</span>
      </div>
    )
  }

  const tone = line.type === "add" ? "bg-success-soft/60" : line.type === "del" ? "bg-danger-soft/60" : ""
  const sign = line.type === "add" ? "+" : line.type === "del" ? "−" : " "
  const signColor = line.type === "add" ? "text-success" : line.type === "del" ? "text-danger" : "text-subtle-foreground"

  const displayCode =
    line.content.length > 0 && (line.content[0] === "+" || line.content[0] === "-" || line.content[0] === " ")
      ? line.content.slice(1)
      : line.content

  return (
    <div className={"flex font-mono text-[12px] leading-[1.6] " + tone}>
      <span className="w-10 shrink-0 select-none border-r border-border/60 px-2 text-right text-subtle-foreground">
        {line.oldNo ?? ""}
      </span>
      <span className="w-10 shrink-0 select-none border-r border-border/60 px-2 text-right text-subtle-foreground">
        {line.newNo ?? ""}
      </span>
      <span className={"w-5 shrink-0 select-none text-center " + signColor}>{sign}</span>
      <code className="whitespace-pre pr-4 text-foreground">{displayCode || " "}</code>
    </div>
  )
}

export function CodeReview() {
  const { id } = useReactParams<{ id: string }>()

  const [review, setReview] = useState<ExecutionReview | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selectedFile, setSelectedFile] = useState<string | null>(null)

  useEffect(() => {
    if (!id) return
    const controller = new AbortController()
    let isCancelled = false

    setIsLoading(true)
    setError(null)

    getExecutionReview(id, { signal: controller.signal })
      .then((data) => {
        if (!isCancelled) {
          setReview(data)
          setIsLoading(false)
        }
      })
      .catch((err) => {
        if (!isCancelled && err.name !== "AbortError") {
          setError(err instanceof Error ? err.message : "Failed to load execution review.")
          setIsLoading(false)
        }
      })

    return () => {
      isCancelled = true
      controller.abort()
    }
  }, [id])

  if (isLoading) {
    return (
      <PageContainer className="flex h-[calc(100vh-100px)] w-full items-center justify-center">
        <div className="flex flex-col items-center gap-3 text-center">
          <Loader2 className="h-6 w-6 animate-spin text-subtle-foreground" />
          <p className="text-[13.5px] font-medium text-foreground">Loading execution review…</p>
        </div>
      </PageContainer>
    )
  }

  if (error || !review) {
    const isNotFound = error?.toLowerCase().includes("not found") || error?.includes("404")
    const isConflict =
      error?.toLowerCase().includes("cannot be reviewed") ||
      error?.toLowerCase().includes("currently") ||
      error?.includes("409")

    return (
      <PageContainer className="max-w-none px-6 py-12">
        <div className="mx-auto max-w-[700px]">
          <Panel className="flex flex-col items-center justify-center gap-3 p-8 text-center">
            <AlertCircle className="h-8 w-8 text-danger" />
            <div>
              <h2 className="text-[16px] font-semibold text-foreground">
                {isNotFound ? "Execution Not Found" : isConflict ? "Review Unavailable" : "Failed to Load Code Review"}
              </h2>
              <p className="mt-1 text-[13px] text-muted-foreground">
                {error || `Review for execution "${id}" could not be retrieved.`}
              </p>
            </div>
            <div className="mt-2 flex items-center gap-3">
              <RouterLink to="/executions">
                <Button variant="default" size="sm">
                  <ArrowLeft className="h-3.5 w-3.5" />
                  Back to Executions
                </Button>
              </RouterLink>
            </div>
          </Panel>
        </div>
      </PageContainer>
    )
  }

  const statusMeta = getExecutionStatusMeta(review.executionStatus)
  const diffLines = parseGitDiff(review.diff)

  const handleSelectFile = (filePath: string) => {
    setSelectedFile(filePath)
    const el = document.getElementById(`file-diff-${filePath}`)
    if (el) {
      el.scrollIntoView({ behavior: "smooth", block: "start" })
    }
  }

  return (
    <PageContainer className="max-w-none px-0 py-0">
      {/* Header */}
      <div className="sticky top-0 z-10 border-b border-border bg-canvas/85 px-6 py-3 backdrop-blur-sm">
        <div className="mx-auto flex max-w-[1600px] items-center gap-3">
          <RouterLink
            to={`/executions/${review.executionId}`}
            className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] text-muted-foreground hover:bg-surface-3 hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </RouterLink>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="font-mono text-[11px] text-subtle-foreground">{review.taskId ? `TASK-${review.taskId.slice(0, 8)}` : review.executionId.slice(0, 8)}</span>
              <h1 className="truncate text-[14.5px] font-semibold text-foreground">{review.taskTitle || "Code review"}</h1>
              <Badge tone={statusMeta.tone}>{review.executionStatus}</Badge>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              {review.branchName}
            </div>
          </div>
          <Button variant="default" size="sm" disabled className="opacity-50 cursor-not-allowed" title="Action unavailable in read-only review mode">
            Request changes
          </Button>
          <Button variant="default" size="sm" disabled className="opacity-50 cursor-not-allowed text-muted-foreground" title="Action unavailable in read-only review mode">
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
            <span className="font-mono text-[11px] text-subtle-foreground">{review.changedFileCount}</span>
          </div>
          <div className="space-y-1">
            {review.changedFiles.length === 0 ? (
              <div className="p-2 text-[12px] text-subtle-foreground">No changed files</div>
            ) : (
              review.changedFiles.map((f: ExecutionReviewFile) => {
                const isActive = selectedFile === f.path
                const fileName = f.path.split("/").pop() || f.path

                return (
                  <button
                    key={f.path}
                    onClick={() => handleSelectFile(f.path)}
                    className={
                      "flex w-full items-center gap-2 rounded-[var(--radius-md)] px-2.5 py-2 text-left transition-colors " +
                      (isActive
                        ? "bg-primary-soft text-primary"
                        : "text-muted-foreground hover:bg-surface-2 hover:text-foreground")
                    }
                  >
                    <FileCode2 className="h-3.5 w-3.5 shrink-0" />
                    <span className="truncate text-[12.5px] font-medium" title={f.path}>
                      {fileName}
                    </span>
                    <span className="ml-auto flex shrink-0 items-center gap-1 font-mono text-[10px]">
                      {f.additions !== null && <span className="text-success">+{f.additions}</span>}
                      {f.deletions !== null && <span className="text-danger">−{f.deletions}</span>}
                    </span>
                  </button>
                )
              })
            )}
          </div>
        </aside>

        {/* CENTER — combined git diff */}
        <section className="min-w-0 border-b border-border lg:border-b-0">
          <div className="flex items-center justify-between border-b border-border px-4 py-2.5">
            <span className="font-mono text-[12px] text-foreground truncate">
              {selectedFile ? selectedFile : "Combined Git Diff"}
            </span>
            <span className="font-mono text-[11px] text-subtle-foreground">
              {review.changedFileCount} {review.changedFileCount === 1 ? "file" : "files"} changed
            </span>
          </div>

          {review.diffTruncated && (
            <div className="flex items-center gap-2 border-b border-amber/30 bg-amber-soft/80 px-4 py-2 font-mono text-[11.5px] text-accent">
              <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
              <span>Diff is truncated because it exceeded maximum size limits.</span>
            </div>
          )}

          <div className="overflow-x-auto bg-surface">
            {diffLines.length === 0 ? (
              <div className="p-8 text-center text-[13px] text-subtle-foreground">
                No file changes detected in this execution.
              </div>
            ) : (
              diffLines.map((line) => <DiffRow key={line.id} line={line} />)
            )}
          </div>
        </section>

        {/* RIGHT — stage status */}
        <aside className="p-5 lg:border-l lg:border-border">
          <div className="tech-label mb-3">Reviewer verdict</div>

          <div className="grid grid-cols-2 gap-2.5">
            <Panel className="p-3">
              <div className="flex items-center gap-1.5">
                <Hammer
                  className={cn(
                    "h-3.5 w-3.5",
                    review.build.status === "Passed"
                      ? "text-success"
                      : review.build.status === "Failed"
                        ? "text-danger"
                        : "text-subtle-foreground",
                  )}
                />
                <span className="tech-label">Build</span>
              </div>
              <div
                className={cn(
                  "mt-1 text-[13px] font-semibold",
                  review.build.status === "Passed"
                    ? "text-success"
                    : review.build.status === "Failed"
                      ? "text-danger"
                      : "text-muted-foreground",
                )}
              >
                {review.build.status}
              </div>
            </Panel>
            <Panel className="p-3">
              <div className="flex items-center gap-1.5">
                <FlaskConical
                  className={cn(
                    "h-3.5 w-3.5",
                    review.test.status === "Passed"
                      ? "text-success"
                      : review.test.status === "Failed"
                        ? "text-danger"
                        : "text-subtle-foreground",
                  )}
                />
                <span className="tech-label">Tests</span>
              </div>
              <div
                className={cn(
                  "mt-1 text-[13px] font-semibold",
                  review.test.status === "Passed"
                    ? "text-success"
                    : review.test.status === "Failed"
                      ? "text-danger"
                      : "text-muted-foreground",
                )}
              >
                {review.test.status}
              </div>
            </Panel>
          </div>

          <Button variant="default" size="lg" disabled className="mt-5 w-full opacity-50 cursor-not-allowed text-muted-foreground">
            Approve &amp; open PR (Read-only)
          </Button>
        </aside>
      </div>
    </PageContainer>
  )
}
