import { useEffect, useRef, useState } from "react"
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
  CheckCircle2,
  XCircle,
  UploadCloud,
  RotateCw,
  Sparkles,
} from "lucide-react"
import { PageContainer } from "@/components/shared"
import { Button, Badge, Panel } from "@/components/ui/primitives"
import { approveExecutionReview, commitExecution, createPullRequest, pushExecution, getExecutionReview, rejectExecutionReview, syncPullRequest, mergeExecution, getExecutionActivity, getGitHubConnectUrl } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import {
  getExecutionStatusMeta,
  type ExecutionReview,
  type ExecutionReviewFile,
  type ExecutionActivityItem,
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
  const { selectWorkspace, activeWorkspaceId } = useWorkspace()

  const [review, setReview] = useState<ExecutionReview | null>(null)
  const [activities, setActivities] = useState<ExecutionActivityItem[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selectedFile, setSelectedFile] = useState<string | null>(null)
  const [isSubmittingDecision, setIsSubmittingDecision] = useState(false)
  const [decisionError, setDecisionError] = useState<string | null>(null)
  const [showRejectModal, setShowRejectModal] = useState(false)
  const [rejectionReasonInput, setRejectionReasonInput] = useState("")

  const activeRequestIdRef = useRef(0)
  const hasSyncedSidebarWorkspaceRef = useRef<string | null>(null)

  useEffect(() => {
    if (!id) {
      setIsLoading(true)
      return
    }

    const currentRequestId = ++activeRequestIdRef.current
    const controller = new AbortController()
    let isCancelled = false

    setIsLoading(true)
    setError(null)
    setReview(null)

    Promise.all([
      getExecutionReview(id, undefined, { signal: controller.signal }),
      getExecutionActivity(id, undefined, { signal: controller.signal }).catch(() => []),
    ])
      .then(([reviewData, actData]) => {
        if (!isCancelled && currentRequestId === activeRequestIdRef.current) {
          setReview(reviewData)
          setActivities(actData)
          setError(null)
          setIsLoading(false)

          // Synchronize sidebar active workspace to execution workspace once when entering the route
          if (reviewData.repositoryWorkspaceId && hasSyncedSidebarWorkspaceRef.current !== id) {
            hasSyncedSidebarWorkspaceRef.current = id
            selectWorkspace(reviewData.repositoryWorkspaceId)
          }
        }
      })
      .catch((err) => {
        if (!isCancelled && err.name !== "AbortError" && currentRequestId === activeRequestIdRef.current) {
          setError(err instanceof Error ? err.message : "Failed to load execution review.")
          setReview(null)
          setIsLoading(false)
        }
      })

    return () => {
      isCancelled = true
      controller.abort()
    }
  }, [id, selectWorkspace])

  const [isSubmittingCommit, setIsSubmittingCommit] = useState(false)
  const [isSubmittingPush, setIsSubmittingPush] = useState(false)

  const handleApprove = async () => {
    if (!id || !review || isSubmittingDecision) return
    setIsSubmittingDecision(true)
    setDecisionError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const decision = await approveExecutionReview(id, review.changeFingerprint, wsId)
      try {
        const fresh = await getExecutionReview(id, wsId)
        setReview(fresh)
      } catch {
        setReview((prev) => prev ? {
          ...prev,
          reviewStatus: decision.reviewStatus,
          decidedAt: decision.decidedAt,
          rejectionReason: decision.rejectionReason,
          commitEligible: true,
          approvedSnapshotMatchesCurrent: true,
        } : null)
      }
    } catch (err) {
      setDecisionError(err instanceof Error ? err.message : "Failed to approve review.")
    } finally {
      setIsSubmittingDecision(false)
    }
  }

  const handleCommit = async () => {
    if (!id || !review || isSubmittingCommit) return
    setIsSubmittingCommit(true)
    setDecisionError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const res = await commitExecution(id, wsId)
      try {
        const fresh = await getExecutionReview(id, wsId)
        setReview(fresh)
      } catch {
        setReview((prev) => prev ? {
          ...prev,
          commitStatus: res.commitStatus,
          commitSha: res.commitSha,
          committedAt: res.committedAt,
          commitEligible: false,
          canRequestPush: true,
        } : null)
      }
    } catch (err) {
      setDecisionError(err instanceof Error ? err.message : "Failed to commit changes.")
    } finally {
      setIsSubmittingCommit(false)
    }
  }

  const handlePush = async () => {
    if (!id || !review || isSubmittingPush) return
    setIsSubmittingPush(true)
    setDecisionError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const res = await pushExecution(id, wsId)
      try {
        const fresh = await getExecutionReview(id, wsId)
        setReview(fresh)
      } catch {
        setReview((prev) => prev ? {
          ...prev,
          pushStatus: res.pushStatus,
          remoteBranchName: res.branchName,
          remoteCommitSha: res.remoteCommitSha,
          pushedAt: res.pushedAt,
          canRequestPush: false,
          canRequestPullRequest: true,
        } : null)
      }
    } catch (err) {
      setDecisionError(err instanceof Error ? err.message : "Failed to push execution branch.")
    } finally {
      setIsSubmittingPush(false)
    }
  }

  const [isSubmittingPr, setIsSubmittingPr] = useState(false)
  const [isSyncingPr, setIsSyncingPr] = useState(false)
  const [syncError, setSyncError] = useState<string | null>(null)
  const [isSubmittingMerge, setIsSubmittingMerge] = useState(false)
  const [showMergeConfirmModal, setShowMergeConfirmModal] = useState(false)
  const [mergeError, setMergeError] = useState<string | null>(null)

  const handleConfirmMerge = async () => {
    if (!id || !review || isSubmittingMerge) return
    setIsSubmittingMerge(true)
    setMergeError(null)
    setDecisionError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const res = await mergeExecution(id, wsId)
      try {
        const fresh = await getExecutionReview(id, wsId)
        setReview(fresh)
      } catch {
        setReview((prev) => prev ? {
          ...prev,
          mergeStatus: res.mergeStatus,
          mergeCommitSha: res.mergeCommitSha,
          mergedAt: res.mergedAt,
          canRequestMerge: false,
          mergeBlockedReason: null,
          pullRequestRemoteState: "Merged",
        } : null)
      }
      setShowMergeConfirmModal(false)
    } catch (err) {
      const errorMsg = err instanceof Error ? err.message : "Failed to merge pull request."
      setMergeError(errorMsg)
      setDecisionError(errorMsg)
    } finally {
      setIsSubmittingMerge(false)
    }
  }

  const handleSyncPr = async () => {
    if (!id || !review || isSyncingPr) return
    setIsSyncingPr(true)
    setSyncError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const res = await syncPullRequest(id, wsId)
      setReview((prev) => {
        if (!prev) return null
        return {
          ...prev,
          pullRequestNumber: res.pullRequestNumber ?? prev.pullRequestNumber,
          pullRequestUrl: res.pullRequestUrl ?? prev.pullRequestUrl,
          pullRequestRemoteState: res.pullRequestRemoteState,
          pullRequestIntegrityStatus: res.pullRequestIntegrityStatus,
          pullRequestLastSyncedAt: res.lastSyncedAt,
          ciStatus: res.ciStatus,
          ciChecks: res.ciChecks,
          canRequestMerge: res.canRequestMerge !== undefined ? res.canRequestMerge : prev.canRequestMerge,
          mergeBlockedReason: res.mergeBlockedReason !== undefined ? res.mergeBlockedReason : prev.mergeBlockedReason,
        }
      })
      if (res.syncError) {
        setSyncError(res.syncError)
      }
    } catch (err) {
      setSyncError(err instanceof Error ? err.message : "GitHub sync failed.")
    } finally {
      setIsSyncingPr(false)
    }
  }

  const handleCreatePullRequest = async () => {
    if (!id || !review || isSubmittingPr) return
    setIsSubmittingPr(true)
    setDecisionError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const res = await createPullRequest(id, wsId)
      try {
        const fresh = await getExecutionReview(id, wsId)
        setReview(fresh)
      } catch {
        setReview((prev) => prev ? {
          ...prev,
          pullRequestStatus: res.pullRequestStatus,
          pullRequestNumber: res.pullRequestNumber,
          pullRequestUrl: res.pullRequestUrl,
          pullRequestCreatedAt: res.createdAt,
          canRequestPullRequest: false,
        } : null)
      }
    } catch (err) {
      setDecisionError(err instanceof Error ? err.message : "Failed to open pull request.")
    } finally {
      setIsSubmittingPr(false)
    }
  }

  const handleConnectGitHubFromReview = async () => {
    try {
      const { url } = await getGitHubConnectUrl(window.location.pathname)
      window.location.href = url
    } catch {
      // ignore
    }
  }

  const handleRejectSubmit = async () => {
    if (!id || !review || isSubmittingDecision) return
    setIsSubmittingDecision(true)
    setDecisionError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const decision = await rejectExecutionReview(id, rejectionReasonInput, wsId)
      try {
        const fresh = await getExecutionReview(id, wsId)
        setReview(fresh)
      } catch {
        setReview((prev) => prev ? {
          ...prev,
          reviewStatus: decision.reviewStatus,
          decidedAt: decision.decidedAt,
          rejectionReason: decision.rejectionReason,
          commitEligible: false,
        } : null)
      }
      setShowRejectModal(false)
      setRejectionReasonInput("")
    } catch (err) {
      setDecisionError(err instanceof Error ? err.message : "Failed to reject review.")
    } finally {
      setIsSubmittingDecision(false)
    }
  }

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

  const isPendingDecision = review.reviewStatus === "Pending"
  const isApproved = review.reviewStatus === "Approved"
  const isRejected = review.reviewStatus === "Rejected"

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
              {isApproved && <Badge tone="green">Approved</Badge>}
              {isRejected && <Badge tone="red">Rejected</Badge>}
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              {review.branchName}
            </div>
          </div>
          {isPendingDecision && (
            <>
              <Button
                variant="default"
                size="sm"
                disabled={isSubmittingDecision}
                onClick={() => setShowRejectModal(true)}
                className="border-danger/30 text-danger hover:bg-danger-soft"
              >
                Reject changes
              </Button>
              <Button
                variant="primary"
                size="sm"
                disabled={isSubmittingDecision}
                onClick={handleApprove}
              >
                {isSubmittingDecision ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <CheckCircle2 className="h-3.5 w-3.5" />
                )}
                Approve changes
              </Button>
            </>
          )}
          {review.pullRequestStatus === "Open" && review.pullRequestUrl ? (
            <a
              href={review.pullRequestUrl}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1.5 rounded-[var(--radius-md)] bg-success-soft px-3 py-1.5 font-mono text-[12px] font-semibold text-success hover:bg-success-soft/80"
            >
              <GitPullRequest className="h-3.5 w-3.5" />
              PR #{review.pullRequestNumber}
            </a>
          ) : (
            <Button
              variant="default"
              size="sm"
              disabled={!review.canRequestPullRequest || isSubmittingPr}
              onClick={handleCreatePullRequest}
              className={cn(!review.canRequestPullRequest && "opacity-50 cursor-not-allowed text-muted-foreground")}
            >
              {isSubmittingPr ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
              ) : (
                <GitPullRequest className="h-3.5 w-3.5" />
              )}
              {isSubmittingPr ? "Opening pull request..." : "Open pull request"}
            </Button>
          )}
        </div>
      </div>

      {decisionError && (
        <div className="mx-auto max-w-[1600px] px-6 pt-3">
          <div className="flex items-center justify-between gap-3 rounded-[var(--radius-md)] border border-danger/30 bg-danger-soft/80 px-4 py-2.5 text-[12.5px] text-danger">
            <div className="flex items-center gap-2">
              <AlertCircle className="h-4 w-4 shrink-0" />
              <span>{decisionError}</span>
            </div>
            <div className="flex items-center gap-2">
              {decisionError.toLowerCase().includes("connect github") && (
                <button
                  type="button"
                  onClick={handleConnectGitHubFromReview}
                  className="rounded bg-danger px-2.5 py-1 text-xs font-medium text-white hover:opacity-90 transition-opacity"
                >
                  Connect GitHub
                </button>
              )}
              {(decisionError.toLowerCase().includes("update repository") || decisionError.toLowerCase().includes("permissions")) && (
                <a
                  href="https://github.com/settings/installations"
                  target="_blank"
                  rel="noreferrer"
                  className="rounded border border-danger/40 bg-surface px-2.5 py-1 text-xs font-medium text-danger hover:bg-danger-soft transition-colors"
                >
                  Update GitHub Access ↗
                </a>
              )}
              {decisionError.toLowerCase().includes("reconnect") && (
                <button
                  type="button"
                  onClick={handleConnectGitHubFromReview}
                  className="rounded bg-danger px-2.5 py-1 text-xs font-medium text-white hover:opacity-90 transition-opacity"
                >
                  Reconnect GitHub
                </button>
              )}
              <button onClick={() => setDecisionError(null)} className="text-subtle-foreground hover:text-foreground">
                &times;
              </button>
            </div>
          </div>
        </div>
      )}

      {review.predictedVsActual && (
        <div className="mx-auto max-w-[1600px] px-6 pt-3">
          <div className="rounded-[var(--radius-lg)] border border-border bg-surface p-3.5 shadow-sm">
            <div className="flex items-center justify-between gap-2 border-b border-border/40 pb-2 mb-2">
              <div className="flex items-center gap-2">
                <Sparkles className="h-4 w-4 text-primary" />
                <span className="text-[12.5px] font-semibold text-foreground">Predicted vs Actual Execution</span>
              </div>
              <div className="flex items-center gap-2 font-mono text-[11px]">
                <span className="text-success font-medium">{review.predictedVsActual.matchedFiles.length} matched</span>
                {review.predictedVsActual.unexpectedFiles.length > 0 && (
                  <span className="text-amber-500 font-medium">· {review.predictedVsActual.unexpectedFiles.length} unexpected</span>
                )}
                {review.predictedVsActual.missingPredictedFiles.length > 0 && (
                  <span className="text-muted-foreground">· {review.predictedVsActual.missingPredictedFiles.length} untouched</span>
                )}
              </div>
            </div>

            <div className="grid gap-2 sm:grid-cols-2 text-[11.5px]">
              <div>
                <span className="tech-label text-[10px]">Verification & Checks</span>
                <div className="mt-1 flex items-center gap-1.5 text-muted-foreground">
                  <CheckCircle2 className="h-3.5 w-3.5 text-success shrink-0" />
                  <span>
                    {review.predictedVsActual.allExpectedChecksExecuted
                      ? `All ${review.predictedVsActual.expectedChecks.length || review.predictedVsActual.executedChecks.length} expected checks executed`
                      : `${review.predictedVsActual.executedChecks.length} check(s) executed`}
                  </span>
                </div>
              </div>

              {review.predictedVsActual.dimensionObservations.length > 0 && (
                <div>
                  <span className="tech-label text-[10px]">Grounding Observations</span>
                  <ul className="mt-1 space-y-0.5 text-muted-foreground">
                    {review.predictedVsActual.dimensionObservations.map((obs, idx) => (
                      <li key={idx} className="flex items-start gap-1">
                        <span className="text-primary mt-0.5">•</span>
                        <span className="break-words">{obs}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          </div>
        </div>
      )}

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

        {/* RIGHT — stage status & decision */}
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

          <div className="mt-5 space-y-3">
            <div className="tech-label">Review decision</div>
            {isPendingDecision && (() => {
              const buildPassed = activities.some((a) => a.stage === "Build" && a.status === "Completed")
              const buildFailed = activities.some((a) => a.stage === "Build" && a.status === "Failed")
              const testPassed = activities.some((a) => a.stage === "Test" && a.status === "Completed")
              const testFailed = activities.some((a) => a.stage === "Test" && a.status === "Failed")
              const hasValidationResults = activities.some((a) => a.stage === "Build" || a.stage === "Test")
              const validationPassed = !hasValidationResults || (buildPassed && !buildFailed && testPassed && !testFailed)

              return (
                <div className="space-y-2">
                  <Panel className="p-3 text-[12px] text-muted-foreground">
                    Review is pending developer decision. You may inspect the diff and approve or reject the changes.
                  </Panel>
                  {!validationPassed && (
                    <div className="flex items-center gap-2 rounded-[var(--radius-md)] border border-danger/30 bg-danger/10 p-2.5 text-[12px] text-danger">
                      <AlertCircle className="h-4 w-4 shrink-0" />
                      <span>Build or Test validation did not pass. Approval is blocked.</span>
                    </div>
                  )}
                  <div className="grid grid-cols-2 gap-2">
                    <Button
                      variant="default"
                      size="md"
                      disabled={isSubmittingDecision}
                      onClick={() => setShowRejectModal(true)}
                      className="w-full border-danger/30 text-danger hover:bg-danger-soft"
                    >
                      Reject
                    </Button>
                    <Button
                      variant="primary"
                      size="md"
                      disabled={isSubmittingDecision || !validationPassed}
                      onClick={handleApprove}
                      className="w-full"
                    >
                      {isSubmittingDecision ? <Loader2 className="h-4 w-4 animate-spin" /> : "Approve"}
                    </Button>
                  </div>
                </div>
              )
            })()}

            {isApproved && (
              <div className="space-y-3">
                <Panel className="border-success/30 bg-success-soft/30 p-4 space-y-2">
                  <div className="flex items-center gap-2 text-success font-semibold text-[13.5px]">
                    <CheckCircle2 className="h-4 w-4 shrink-0" />
                    <span>Review Approved</span>
                  </div>
                  {review.decidedAt && (
                    <div className="font-mono text-[11px] text-subtle-foreground">
                      Decided at {new Date(review.decidedAt).toLocaleString()}
                    </div>
                  )}
                  <p className="text-[12px] text-muted-foreground">
                    Execution changes approved.
                  </p>
                </Panel>

                {review.commitStatus === "Committed" ? (
                  <div className="space-y-3">
                    <Panel className="border-primary/30 bg-primary-soft/30 p-4 space-y-2">
                      <div className="flex items-center gap-2 text-primary font-semibold text-[13.5px]">
                        <GitBranch className="h-4 w-4 shrink-0" />
                        <span>Committed locally</span>
                        <span className="font-mono text-[11px] text-muted-foreground">({review.commitSha?.slice(0, 7)})</span>
                      </div>
                      {review.committedAt && (
                        <div className="font-mono text-[11px] text-subtle-foreground">
                          Committed at {new Date(review.committedAt).toLocaleString()}
                        </div>
                      )}
                    </Panel>

                    {review.pushStatus === "Pushed" ? (
                      <div className="space-y-3">
                        <Panel className="border-success/30 bg-success-soft/30 p-4 space-y-2">
                          <div className="flex items-center gap-2 text-success font-semibold text-[13.5px]">
                            <UploadCloud className="h-4 w-4 shrink-0" />
                            <span>Pushed remotely</span>
                          </div>
                          <div className="font-mono text-[11.5px] text-foreground">
                            {review.remoteBranchName || review.branchName} <span className="text-subtle-foreground">({review.remoteCommitSha?.slice(0, 7)})</span>
                          </div>
                          {review.pushedAt && (
                            <div className="font-mono text-[11px] text-subtle-foreground">
                              Pushed at {new Date(review.pushedAt).toLocaleString()}
                            </div>
                          )}
                        </Panel>

                        {review.pullRequestStatus === "Open" ? (
                          <Panel className="border-success/30 bg-success-soft/30 p-4 space-y-3">
                            <div className="flex items-center justify-between">
                              <div className="flex items-center gap-2 text-success font-semibold text-[13.5px]">
                                <GitPullRequest className="h-4 w-4 shrink-0" />
                                <span>PR #{review.pullRequestNumber}</span>
                                <Badge tone={review.pullRequestRemoteState === "Merged" ? "green" : review.pullRequestRemoteState === "Closed" ? "red" : "blue"}>
                                  {review.pullRequestRemoteState ?? "Open"}
                                </Badge>
                              </div>
                              <Button
                                variant="default"
                                size="sm"
                                disabled={isSyncingPr}
                                onClick={handleSyncPr}
                                className="h-7 px-2 font-mono text-[11px]"
                              >
                                {isSyncingPr ? (
                                  <Loader2 className="h-3 w-3 animate-spin" />
                                ) : (
                                  <RotateCw className="h-3 w-3" />
                                )}
                                Refresh
                              </Button>
                            </div>

                            {/* Integrity Badge */}
                            {review.pullRequestIntegrityStatus && review.pullRequestIntegrityStatus !== "Unknown" && (
                              <div className="flex items-center gap-1.5 text-[11.5px] font-mono">
                                <span className="text-subtle-foreground">Integrity:</span>
                                {review.pullRequestIntegrityStatus === "Valid" ? (
                                  <span className="text-success font-medium">✓ Approved commit</span>
                                ) : review.pullRequestIntegrityStatus === "HeadChanged" ? (
                                  <span className="text-danger font-medium">⚠ PR head changed after approval</span>
                                ) : (
                                  <span className="text-danger font-medium">⚠ Identity mismatch</span>
                                )}
                              </div>
                            )}

                            {/* CI Aggregate Status */}
                            {review.ciStatus && (
                              <div className="flex items-center justify-between border-t border-border/40 pt-2 text-[12px]">
                                <span className="text-subtle-foreground">CI Status:</span>
                                <Badge
                                  tone={
                                    review.ciStatus === "Success"
                                      ? "green"
                                      : review.ciStatus === "Failure"
                                        ? "red"
                                        : review.ciStatus === "Pending"
                                          ? "amber"
                                          : "neutral"
                                  }
                                >
                                  {review.ciStatus}
                                </Badge>
                              </div>
                            )}

                            {/* CI Checks List */}
                            {review.ciChecks && review.ciChecks.length > 0 && (
                              <div className="space-y-1.5 border-t border-border/40 pt-2 font-mono text-[11px]">
                                <div className="text-subtle-foreground font-semibold text-[10px] uppercase">Checks ({review.ciChecks.length})</div>
                                {review.ciChecks.map((check) => (
                                  <div key={check.id} className="flex items-center justify-between text-foreground">
                                    <span className="truncate max-w-[180px]" title={check.name}>{check.name}</span>
                                    <span
                                      className={cn(
                                        "font-semibold text-[10px]",
                                        check.conclusion === "success" || check.status === "success"
                                          ? "text-success"
                                          : check.conclusion === "failure" || check.status === "failure" || check.conclusion === "error"
                                            ? "text-danger"
                                            : "text-amber-500"
                                      )}
                                    >
                                      {check.conclusion || check.status}
                                    </span>
                                  </div>
                                ))}
                              </div>
                            )}

                            {syncError && (
                              <div className="text-[11px] text-danger bg-danger-soft/60 p-2 rounded-[var(--radius-md)]">
                                Refresh failed: {syncError}
                              </div>
                            )}

                            {review.pullRequestLastSyncedAt && (
                              <div className="font-mono text-[10.5px] text-subtle-foreground">
                                Last synced {new Date(review.pullRequestLastSyncedAt).toLocaleTimeString()}
                              </div>
                            )}

                            {review.pullRequestUrl && (
                              <a
                                href={review.pullRequestUrl}
                                target="_blank"
                                rel="noreferrer"
                                className="inline-flex items-center gap-1 font-mono text-[12px] text-primary hover:underline pt-1"
                              >
                                View on GitHub &rarr;
                              </a>
                            )}

                            {review.mergeStatus === "Merged" ? (
                              <div className="mt-3 rounded-[var(--radius-md)] border border-emerald-500/30 bg-emerald-500/10 p-3 space-y-1.5 font-mono text-[11px]">
                                <div className="flex items-center justify-between text-emerald-400 font-semibold text-[12.5px]">
                                  <div className="flex items-center gap-1.5">
                                    <CheckCircle2 className="h-4 w-4 shrink-0 text-emerald-400" />
                                    <span>Pull Request Merged</span>
                                  </div>
                                  <Badge tone="green">Merged</Badge>
                                </div>
                                {review.mergeCommitSha && (
                                  <div className="text-subtle-foreground truncate">
                                    Commit: <span className="text-foreground font-semibold">{review.mergeCommitSha.slice(0, 7)}</span>
                                  </div>
                                )}
                                {review.mergedAt && (
                                  <div className="text-subtle-foreground">
                                    Merged: <span className="text-foreground">{new Date(review.mergedAt).toLocaleString()}</span>
                                  </div>
                                )}
                              </div>
                            ) : review.canRequestMerge ? (
                              <Button
                                variant="primary"
                                size="md"
                                disabled={isSubmittingMerge}
                                onClick={() => setShowMergeConfirmModal(true)}
                                className="w-full mt-3 bg-emerald-600 hover:bg-emerald-500 text-white"
                              >
                      {isSubmittingMerge ? (
                                  <Loader2 className="h-4 w-4 animate-spin" />
                                ) : (
                                  <GitPullRequest className="h-4 w-4" />
                                )}
                                Merge pull request
                              </Button>
                            ) : review.mergeBlockedReason ? (
                              <div className="mt-3 rounded-[var(--radius-md)] border border-amber-500/20 bg-amber-500/10 p-2.5 text-[11.5px] text-amber-400 flex items-start gap-2">
                                <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                                <span>{review.mergeBlockedReason}</span>
                              </div>
                            ) : null}
                          </Panel>
                        ) : (
                          <Button
                            variant="primary"
                            size="md"
                            disabled={!review.canRequestPullRequest || isSubmittingPr}
                            onClick={handleCreatePullRequest}
                            className="w-full"
                          >
                            {isSubmittingPr ? (
                              <Loader2 className="h-4 w-4 animate-spin" />
                            ) : (
                              <GitPullRequest className="h-4 w-4" />
                            )}
                            Open pull request
                          </Button>
                        )}
                      </div>
                    ) : (
                      <Button
                        variant="primary"
                        size="md"
                        disabled={!review.canRequestPush || isSubmittingPush}
                        onClick={handlePush}
                        className="w-full"
                      >
                        {isSubmittingPush ? (
                          <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                          <UploadCloud className="h-4 w-4" />
                        )}
                        Push branch
                      </Button>
                    )}
                  </div>
                ) : !review.approvedSnapshotMatchesCurrent ? (
                  <Panel className="border-amber/30 bg-amber-soft/40 p-4 space-y-2">
                    <div className="flex items-center gap-2 text-accent font-semibold text-[13px]">
                      <AlertTriangle className="h-4 w-4 shrink-0" />
                      <span>Approved snapshot changed</span>
                    </div>
                    <p className="text-[12px] text-muted-foreground">
                      Worktree content differs from the approved review snapshot.
                    </p>
                    <Button variant="default" size="md" disabled className="w-full opacity-50 cursor-not-allowed">
                      Commit changes
                    </Button>
                  </Panel>
                ) : (
                  <Button
                    variant="primary"
                    size="md"
                    disabled={!review.commitEligible || isSubmittingCommit}
                    onClick={handleCommit}
                    className="w-full"
                  >
                    {isSubmittingCommit ? (
                      <Loader2 className="h-4 w-4 animate-spin" />
                    ) : (
                      <GitBranch className="h-4 w-4" />
                    )}
                    Commit changes
                  </Button>
                )}
              </div>
            )}

            {isRejected && (
              <Panel className="border-danger/30 bg-danger-soft/30 p-4 space-y-2">
                <div className="flex items-center gap-2 text-danger font-semibold text-[13.5px]">
                  <XCircle className="h-4 w-4 shrink-0" />
                  <span>Review Rejected</span>
                </div>
                {review.decidedAt && (
                  <div className="font-mono text-[11px] text-subtle-foreground">
                    Decided at {new Date(review.decidedAt).toLocaleString()}
                  </div>
                )}
                {review.rejectionReason && (
                  <div className="mt-2 rounded-[var(--radius-md)] border border-danger/20 bg-surface p-2.5 text-[12px] text-foreground">
                    <span className="font-semibold block mb-0.5 text-danger text-[11px] uppercase tracking-wider">Reason</span>
                    {review.rejectionReason}
                  </div>
                )}
              </Panel>
            )}
          </div>
        </aside>
      </div>

      {/* Reject Modal */}
      {showRejectModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-xs p-4">
          <div className="w-full max-w-[480px] rounded-[var(--radius-lg)] border border-border bg-canvas p-6 shadow-xl space-y-4">
            <h3 className="text-[15px] font-semibold text-foreground">Reject Execution Changes</h3>
            <p className="text-[12.5px] text-muted-foreground">
              Optional: provide a short reason for rejecting these changes. This will be persisted with the audit record.
            </p>
            <textarea
              className="w-full h-24 rounded-[var(--radius-md)] border border-border bg-surface p-3 font-sans text-[13px] text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
              placeholder="Reason for rejection (optional, max 1000 characters)"
              maxLength={1000}
              value={rejectionReasonInput}
              onChange={(e) => setRejectionReasonInput(e.target.value)}
            />
            <div className="flex items-center justify-end gap-3 pt-2">
              <Button
                variant="default"
                size="sm"
                disabled={isSubmittingDecision}
                onClick={() => {
                  setShowRejectModal(false)
                  setRejectionReasonInput("")
                }}
              >
                Cancel
              </Button>
              <Button
                variant="default"
                size="sm"
                disabled={isSubmittingDecision}
                onClick={handleRejectSubmit}
                className="bg-danger text-white hover:bg-danger/90 border-transparent"
              >
                {isSubmittingDecision ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : "Confirm Rejection"}
              </Button>
            </div>
          </div>
        </div>
      )}

      {/* Merge Confirmation Modal */}
      {showMergeConfirmModal && review && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-xs p-4">
          <div className="w-full max-w-[520px] rounded-[var(--radius-lg)] border border-border bg-canvas p-6 shadow-xl space-y-4">
            <div className="flex items-center gap-2 text-foreground font-semibold text-[16px]">
              <GitPullRequest className="h-5 w-5 text-emerald-500" />
              <span>Merge Pull Request #{review.pullRequestNumber}?</span>
            </div>

            {mergeError && (
              <div className="rounded-[var(--radius-md)] border border-danger/30 bg-danger-soft/80 p-3 text-[12.5px] text-danger flex items-start gap-2">
                <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                <span>{mergeError}</span>
              </div>
            )}

            <div className="rounded-[var(--radius-md)] border border-border/60 bg-surface p-3.5 space-y-2 font-mono text-[12px]">
              <div className="flex justify-between text-subtle-foreground">
                <span>Base branch:</span>
                <span className="text-foreground font-semibold">master</span>
              </div>
              <div className="flex justify-between text-subtle-foreground">
                <span>Head branch:</span>
                <span className="text-foreground font-semibold">{review.remoteBranchName}</span>
              </div>
              <div className="flex justify-between text-subtle-foreground">
                <span>Approved commit:</span>
                <span className="text-foreground font-semibold">{review.remoteCommitSha?.slice(0, 7)}</span>
              </div>
              <div className="flex justify-between text-subtle-foreground border-t border-border/40 pt-1.5">
                <span>CI Status:</span>
                <span className="text-emerald-400 font-semibold">{review.ciStatus}</span>
              </div>
            </div>
            <p className="text-[12.5px] text-muted-foreground">
              This will merge the execution's approved pull request into the base branch on GitHub using standard merge method.
            </p>
            <div className="flex items-center justify-end gap-3 pt-2">
              <Button
                variant="default"
                size="sm"
                disabled={isSubmittingMerge}
                onClick={() => {
                  setShowMergeConfirmModal(false)
                  setMergeError(null)
                }}
              >
                Cancel
              </Button>
              <Button
                variant="primary"
                size="sm"
                disabled={isSubmittingMerge}
                onClick={handleConfirmMerge}
                className="bg-emerald-600 hover:bg-emerald-500 text-white"
              >
                {isSubmittingMerge ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : "Confirm merge"}
              </Button>
            </div>
          </div>
        </div>
      )}
    </PageContainer>
  )
}
