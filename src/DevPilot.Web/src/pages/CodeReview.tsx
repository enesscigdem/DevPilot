import { useEffect, useRef, useState } from "react"
import { useTranslation } from "react-i18next"
import { fmt } from "@/i18n"
import { Link as RouterLink, useNavigate, useParams as useReactParams } from "react-router-dom"
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
  RotateCcw,
  Sparkles,
  MessageSquareWarning,
} from "lucide-react"
import { PageContainer } from "@/components/shared"
import { ExecutionTabs } from "@/components/ExecutionTabs"
import { UsagePanel, VerdictCard } from "@/components/VerdictCard"
import { Button, Badge, Panel } from "@/components/ui/primitives"
import { approveExecutionReview, commitExecution, createPullRequest, pushExecution, getExecutionReview, rejectExecutionReview, syncPullRequest, mergeExecution, getExecutionActivity, getExecutionVisual, getGitHubConnectUrl, retryExecution, requestExecutionChanges } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import {
  getExecutionStatusMeta,
  type ExecutionReview,
  type ExecutionReviewFile,
  type ExecutionActivityItem,
  type VisualCaptureManifest,
} from "@/types"
import { cn } from "@/lib/utils"
import { DiffRow, parseGitDiff } from "@/components/DiffView"
import { RevisionPanel } from "@/components/RevisionPanel"
import { RequestChangesModal } from "@/components/RequestChangesModal"
import { VisualReviewPanel } from "@/components/VisualReviewPanel"

export function CodeReview() {
  const { t } = useTranslation()
  const statText = (s?: string | null) => t(`review.stat.${s}`, { defaultValue: s ?? "" })
  const ciText = (s?: string | null) => t(`review.ci.${String(s).toLowerCase()}`, { defaultValue: String(s ?? "") })
  const { id } = useReactParams<{ id: string }>()
  const navigate = useNavigate()
  const { selectWorkspace, activeWorkspaceId, workspaces } = useWorkspace()

  const [review, setReview] = useState<ExecutionReview | null>(null)
  const noPrSupport =
    workspaces.find((w) => w.id === (review?.repositoryWorkspaceId ?? activeWorkspaceId))?.provider === "Generic"
  const [activities, setActivities] = useState<ExecutionActivityItem[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selectedFile, setSelectedFile] = useState<string | null>(null)
  const [isSubmittingDecision, setIsSubmittingDecision] = useState(false)
  const [isRetrying, setIsRetrying] = useState(false)
  const [decisionError, setDecisionError] = useState<string | null>(null)
  const [showRejectModal, setShowRejectModal] = useState(false)
  const [rejectionReasonInput, setRejectionReasonInput] = useState("")
  const [showChangesModal, setShowChangesModal] = useState(false)
  const [isRequestingChanges, setIsRequestingChanges] = useState(false)
  const [changesError, setChangesError] = useState<string | null>(null)
  const [visual, setVisual] = useState<VisualCaptureManifest | null>(null)
  const [visualAcknowledged, setVisualAcknowledged] = useState(false)

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
    setVisual(null)
    setVisualAcknowledged(false)

    Promise.all([
      getExecutionReview(id, undefined, { signal: controller.signal }),
      getExecutionActivity(id, undefined, { signal: controller.signal }).catch(() => []),
      getExecutionVisual(id, { signal: controller.signal }).catch(() => null),
    ])
      .then(([reviewData, actData, visualData]) => {
        if (!isCancelled && currentRequestId === activeRequestIdRef.current) {
          setReview(reviewData)
          setActivities(actData)
          setVisual(visualData)
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
          setError(err instanceof Error ? err.message : t("review.errLoad"))
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

  const handleRetryExecution = async () => {
    if (!review || isRetrying) return
    setIsRetrying(true)
    setDecisionError(null)

    try {
      const created = await retryExecution(review.taskId, review.repositoryWorkspaceId ?? activeWorkspaceId)
      navigate(`/executions/${created.id}`)
    } catch (err) {
      setDecisionError(err instanceof Error ? err.message : t("review.errRetry"))
    } finally {
      setIsRetrying(false)
    }
  }

  const handleApprove = async () => {
    if (!id || !review || isSubmittingDecision) return
    setIsSubmittingDecision(true)
    setDecisionError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      const decision = await approveExecutionReview(id, review.changeFingerprint, wsId, undefined, visualAcknowledged)
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
      setDecisionError(err instanceof Error ? err.message : t("review.errApprove"))
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
      setDecisionError(err instanceof Error ? err.message : t("review.errCommit"))
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

      // A fix pushed to the branch of an open pull request updates that same pull request on GitHub:
      // refresh its state so CI and integrity are shown for the new head instead of the old one.
      if (review.pullRequestStatus === "Open") {
        void handleSyncPr()
      }
    } catch (err) {
      setDecisionError(err instanceof Error ? err.message : t("review.errPush"))
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
      const errorMsg = err instanceof Error ? err.message : t("review.errMerge")
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
      setSyncError(err instanceof Error ? err.message : t("review.errSync"))
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
      setDecisionError(err instanceof Error ? err.message : t("review.errPr"))
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

  // The fix runs in the background on the same branch; the execution page shows its progress, and the
  // review page comes back to life once it is done.
  const handleRequestChanges = async (feedback: string) => {
    if (!id || !review || isRequestingChanges) return
    setIsRequestingChanges(true)
    setChangesError(null)

    try {
      const wsId = review.repositoryWorkspaceId ?? activeWorkspaceId
      await requestExecutionChanges(id, feedback, wsId)
      setShowChangesModal(false)
      navigate(`/executions/${id}`)
    } catch (err) {
      setChangesError(err instanceof Error ? err.message : t("revision.modal.error"))
    } finally {
      setIsRequestingChanges(false)
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
      setDecisionError(err instanceof Error ? err.message : t("review.errReject"))
    } finally {
      setIsSubmittingDecision(false)
    }
  }

  if (isLoading) {
    return (
      <PageContainer className="flex h-[calc(100vh-100px)] w-full items-center justify-center">
        <div className="flex flex-col items-center gap-3 text-center">
          <Loader2 className="h-6 w-6 animate-spin text-subtle-foreground" />
          <p className="text-[13.5px] font-medium text-foreground">{t("review.loading")}</p>
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
                {isNotFound ? t("review.notFound") : isConflict ? t("review.unavailable") : t("review.failedLoad")}
              </h2>
              <p className="mt-1 text-[13px] text-muted-foreground">
                {error || t("review.notRetrieved", { id })}
              </p>
            </div>
            <div className="mt-2 flex items-center gap-3">
              <RouterLink to="/executions">
                <Button variant="default" size="sm">
                  <ArrowLeft className="h-3.5 w-3.5" />
                  {t("review.backToExecutions")}
                </Button>
              </RouterLink>
            </div>
          </Panel>
        </div>
      </PageContainer>
    )
  }

  const statusMeta = getExecutionStatusMeta(review.executionStatus, review.verificationOutcome)
  const diffLines = parseGitDiff(review.diff)

  const isPendingDecision = review.reviewStatus === "Pending"
  const isApproved = review.reviewStatus === "Approved"
  const showVisual = !!visual && visual.status !== "None" && (visual.requiresReview || (visual.shots?.length ?? 0) > 0)
  const visualBlocksApproval = !!visual?.requiresReview && !visualAcknowledged
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
              <Badge tone={statusMeta.tone}>{statusMeta.label}</Badge>
              {review.verificationOutcome === "Verified" && <Badge tone="green">{t("review.badge.Verified")}</Badge>}
              {review.verificationOutcome === "NoNewRegressions" && <Badge tone="green">{t("review.badge.NoNewRegressions")}</Badge>}
              {review.verificationOutcome === "PartiallyVerified" && <Badge tone="amber">{t("review.badge.PartiallyVerified")}</Badge>}
              {review.verificationOutcome === "VerificationUnavailable" && <Badge tone="gray">{t("review.badge.VerificationUnavailable")}</Badge>}
              {review.verificationOutcome === "VerificationInfrastructureError" && <Badge tone="red">{t("review.badge.VerificationInfrastructureError")}</Badge>}
              {review.verificationOutcome === "NeedsReview" && <Badge tone="amber">{t("review.badge.NeedsReview")}</Badge>}
              {isApproved && <Badge tone="green">{t("review.badge.approved")}</Badge>}
              {isRejected && <Badge tone="red">{t("review.badge.rejected")}</Badge>}
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              {review.branchName}
            </div>
          </div>
          {review.canRequestChanges && (
            <Button
              variant="default"
              size="sm"
              disabled={isSubmittingDecision || isRequestingChanges}
              onClick={() => {
                setChangesError(null)
                setShowChangesModal(true)
              }}
            >
              <MessageSquareWarning className="h-3.5 w-3.5" />
              {t("review.requestChanges")}
            </Button>
          )}
          {isPendingDecision && (
            <>
              <Button
                variant="default"
                size="sm"
                disabled={isSubmittingDecision}
                onClick={() => setShowRejectModal(true)}
                className="border-danger/30 text-danger hover:bg-danger-soft"
              >
                {t("review.rejectChanges")}
              </Button>
              <Button
                variant="primary"
                size="sm"
                disabled={isSubmittingDecision || visualBlocksApproval}
                onClick={handleApprove}
              >
                {isSubmittingDecision ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <CheckCircle2 className="h-3.5 w-3.5" />
                )}
                {t("review.approveChanges")}
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
              {t("review.prNumber", { n: review.pullRequestNumber })}
            </a>
          ) : (
            <Button
              variant="default"
              size="sm"
              disabled={!review.canRequestPullRequest || isSubmittingPr || noPrSupport}
              title={noPrSupport ? t("review.noPrHost") : undefined}
              onClick={handleCreatePullRequest}
              className={cn(!review.canRequestPullRequest && "opacity-50 cursor-not-allowed text-muted-foreground")}
            >
              {isSubmittingPr ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
              ) : (
                <GitPullRequest className="h-3.5 w-3.5" />
              )}
              {isSubmittingPr ? t("review.openingPr") : t("review.openPr")}
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
                  {t("review.connectGitHub")}
                </button>
              )}
              {(decisionError.toLowerCase().includes("update repository") || decisionError.toLowerCase().includes("permissions")) && (
                <a
                  href="https://github.com/settings/installations"
                  target="_blank"
                  rel="noreferrer"
                  className="rounded border border-danger/40 bg-surface px-2.5 py-1 text-xs font-medium text-danger hover:bg-danger-soft transition-colors"
                >
                  {t("review.updateAccess")}
                </a>
              )}
              {decisionError.toLowerCase().includes("reconnect") && (
                <button
                  type="button"
                  onClick={handleConnectGitHubFromReview}
                  className="rounded bg-danger px-2.5 py-1 text-xs font-medium text-white hover:opacity-90 transition-opacity"
                >
                  {t("review.reconnectGitHub")}
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
                <span className="text-[12.5px] font-semibold text-foreground">{t("review.predicted")}</span>
              </div>
              <div className="flex items-center gap-2 font-mono text-[11px]">
                <span className="text-success font-medium">{t("review.matched", { n: review.predictedVsActual.matchedFiles.length })}</span>
                {review.predictedVsActual.unexpectedFiles.length > 0 && (
                  <span className="text-amber-500 font-medium">{t("review.unexpected", { n: review.predictedVsActual.unexpectedFiles.length })}</span>
                )}
                {review.predictedVsActual.missingPredictedFiles.length > 0 && (
                  <span className="text-muted-foreground">{t("review.untouched", { n: review.predictedVsActual.missingPredictedFiles.length })}</span>
                )}
              </div>
            </div>

            <div className="grid gap-2 sm:grid-cols-2 text-[11.5px]">
              <div>
                <span className="tech-label text-[10px]">{t("review.verifChecks")}</span>
                <div className="mt-1 flex items-center gap-1.5 text-muted-foreground">
                  <CheckCircle2 className="h-3.5 w-3.5 text-success shrink-0" />
                  <span>
                    {review.predictedVsActual.allExpectedChecksExecuted
                      ? t("review.allChecks", { n: review.predictedVsActual.expectedChecks.length || review.predictedVsActual.executedChecks.length })
                      : t("review.someChecks", { n: review.predictedVsActual.executedChecks.length })}
                  </span>
                </div>
              </div>

              {review.predictedVsActual.dimensionObservations.length > 0 && (
                <div>
                  <span className="tech-label text-[10px]">{t("review.grounding")}</span>
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

      <ExecutionTabs executionId={review.executionId} taskId={review.taskId} active="review" reviewAvailable />

      <div className="mx-auto grid max-w-[1600px] grid-cols-1 gap-0 lg:grid-cols-[260px_minmax(0,1fr)_360px]">
        {/* LEFT — file tree */}
        <aside className="border-b border-border p-4 lg:border-b-0 lg:border-r">
          <div className="mb-3 flex items-center justify-between">
            <span className="tech-label">{t("review.changedFiles")}</span>
            <span className="font-mono text-[11px] text-subtle-foreground">{review.changedFileCount}</span>
          </div>
          <div className="space-y-1">
            {review.changedFiles.length === 0 ? (
              <div className="p-2 text-[12px] text-subtle-foreground">{t("review.noChangedFiles")}</div>
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
                        : "text-muted-foreground hover:bg-surface-3 hover:text-foreground")
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
          {showVisual && visual && id && (
            <div className="border-b border-border p-4">
              <VisualReviewPanel
                executionId={id}
                manifest={visual}
                acknowledged={visualAcknowledged}
                onAcknowledgedChange={setVisualAcknowledged}
                decided={review.reviewStatus !== "Pending"}
              />
            </div>
          )}
          <div className="flex items-center justify-between border-b border-border px-4 py-2.5">
            <span className="font-mono text-[12px] text-foreground truncate">
              {selectedFile ? selectedFile : t("review.combinedDiff")}
            </span>
            <span className="font-mono text-[11px] text-subtle-foreground">
              {t("review.filesChanged", { count: review.changedFileCount })}
            </span>
          </div>

          {review.diffTruncated && (
            <div className="flex items-center gap-2 border-b border-amber/30 bg-amber-soft/80 px-4 py-2 font-mono text-[11.5px] text-accent">
              <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
              <span>{t("review.diffTruncated")}</span>
            </div>
          )}

          <div className="overflow-x-auto bg-surface">
            {diffLines.length === 0 ? (
              <div className="p-8 text-center text-[13px] text-subtle-foreground">
                {t("review.noChanges")}
              </div>
            ) : (
              diffLines.map((line) => <DiffRow key={line.id} line={line} />)
            )}
          </div>
        </section>

        {/* RIGHT — stage status & decision */}
        <aside className="p-5 lg:border-l lg:border-border">
          <div className="tech-label mb-3">{t("review.verdictTitle")}</div>

          {review.verdict && (
            <div className="mb-3">
              <VerdictCard
                verdict={review.verdict}
                onFixTests={review.canRequestChanges ? handleRequestChanges : undefined}
                isFixingTests={isRequestingChanges}
              />
            </div>
          )}

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
                <span className="tech-label">{t("review.build")}</span>
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
                {statText(review.build.status)}
              </div>
            </Panel>
            <Panel className="p-3">
              <div className="flex items-center gap-1.5">
                <FlaskConical
                  className={cn(
                    "h-3.5 w-3.5",
                    review.test.status === "Passed"
                      ? "text-success"
                      : review.test.status === "NoNewRegressions"
                        ? "text-amber-500"
                        : review.test.status === "Failed"
                          ? "text-danger"
                          : "text-subtle-foreground",
                  )}
                />
                <span className="tech-label">{t("review.tests")}</span>
              </div>
              <div
                className={cn(
                  "mt-1 text-[13px] font-semibold",
                  review.test.status === "Passed"
                    ? "text-success"
                    : review.test.status === "NoNewRegressions"
                      ? "text-amber-500"
                      : review.test.status === "Failed"
                        ? "text-danger"
                        : "text-muted-foreground",
                )}
              >
                {review.test.status === "NoNewRegressions"
                  ? t("review.noNewRegressions")
                  : review.verificationOutcome === "PartiallyVerified" && review.test.status === "Unknown"
                    ? t("review.noSuite")
                    : statText(review.test.status)}
              </div>
              {review.test.detailSummary && (
                <div className="mt-0.5 text-[11px] text-muted-foreground">
                  {review.test.detailSummary}
                </div>
              )}
            </Panel>
          </div>

          {review.usage && (
            <div className="mt-3">
              <UsagePanel usage={review.usage} />
            </div>
          )}

          <div className="mt-5 space-y-3">
            <div className="tech-label">{t("review.decision")}</div>
            {review.revision && (
              <RevisionPanel
                compact
                revision={review.revision}
                executionId={review.executionId}
                workspaceId={review.repositoryWorkspaceId ?? activeWorkspaceId}
                canRequestChanges={Boolean(review.canRequestChanges)}
                onRequestChanges={() => {
                  setChangesError(null)
                  setShowChangesModal(true)
                }}
              />
            )}
            {isPendingDecision && (() => {
              const isBlocked = review.verificationOutcome === "NeedsReview" || review.verificationOutcome === "Failed" || review.verificationOutcome === "Blocked"
              const validationPassed = !isBlocked

              return (
                <div className="space-y-2">
                  <Panel className="p-3 text-[12px] text-muted-foreground">
                    {t("review.pendingDecision")}
                  </Panel>
                  {review.verificationOutcome === "NeedsReview" && (
                    <div className="space-y-2">
                      <div className="flex items-center gap-2 rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/10 p-2.5 text-[12px] text-amber-600 dark:text-amber-400">
                        <AlertCircle className="h-4 w-4 shrink-0" />
                        <span>{t("review.needsReviewBlocked")}</span>
                      </div>
                      {review.canRetry && (
                        <Button
                          variant="primary"
                          size="sm"
                          disabled={isSubmittingDecision || isRetrying}
                          onClick={handleRetryExecution}
                          className="w-full"
                        >
                          {isRetrying ? (
                            <Loader2 className="h-3.5 w-3.5 animate-spin" />
                          ) : (
                            <RotateCcw className="h-3.5 w-3.5" />
                          )}
                          {isRetrying ? t("review.retrying") : t("review.retryExecution")}
                        </Button>
                      )}
                    </div>
                  )}
                  {review.verificationOutcome === "NoNewRegressions" && (
                    <div className="flex items-center gap-2 rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/10 p-2.5 text-[12px] text-amber-600 dark:text-amber-400">
                      <Info className="h-4 w-4 shrink-0" />
                      <span>{t("review.preExisting")}</span>
                    </div>
                  )}
                  {review.verificationOutcome === "PartiallyVerified" && (
                    <div className="flex items-center gap-2 rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/10 p-2.5 text-[12px] text-amber-600 dark:text-amber-400">
                      <Info className="h-4 w-4 shrink-0" />
                      <span>{t("review.partial")}</span>
                    </div>
                  )}
                  {review.verificationOutcome === "VerificationUnavailable" && (
                    <div className="flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface-2 p-2.5 text-[12px] text-muted-foreground">
                      <Info className="h-4 w-4 shrink-0" />
                      <span>{t("review.unavailableMsg")}</span>
                    </div>
                  )}
                  {review.verificationOutcome === "VerificationInfrastructureError" && (
                    <div className="flex items-center gap-2 rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/10 p-2.5 text-[12px] text-amber-600 dark:text-amber-400">
                      <AlertCircle className="h-4 w-4 shrink-0" />
                      <span>{t("review.infraError")}</span>
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
                      {t("review.reject")}
                    </Button>
                    <Button
                      variant="primary"
                      size="md"
                      disabled={isSubmittingDecision || !validationPassed || visualBlocksApproval}
                      onClick={handleApprove}
                      className="w-full"
                    >
                      {isSubmittingDecision ? <Loader2 className="h-4 w-4 animate-spin" /> : t("review.approve")}
                    </Button>
                  </div>
                  {visualBlocksApproval && (
                    <div className="text-[12px] text-muted-foreground">{t("review.visual.required")}</div>
                  )}
                  {review.canRequestChanges && (
                    <Button
                      variant="default"
                      size="md"
                      disabled={isSubmittingDecision || isRequestingChanges}
                      onClick={() => {
                        setChangesError(null)
                        setShowChangesModal(true)
                      }}
                      className="w-full"
                    >
                      <MessageSquareWarning className="h-4 w-4" />
                      {t("review.requestChanges")}
                    </Button>
                  )}
                </div>
              )
            })()}

            {isApproved && (
              <div className="space-y-3">
                <Panel className="border-success/30 bg-success-soft/30 p-4 space-y-2">
                  <div className="flex items-center gap-2 text-success font-semibold text-[13.5px]">
                    <CheckCircle2 className="h-4 w-4 shrink-0" />
                    <span>{t("review.reviewApproved")}</span>
                  </div>
                  {review.decidedAt && (
                    <div className="font-mono text-[11px] text-subtle-foreground">
                      {t("review.decidedAt", { when: fmt.dateTime(review.decidedAt) })}
                    </div>
                  )}
                  <p className="text-[12px] text-muted-foreground">
                    {t("review.changesApproved")}
                  </p>
                </Panel>

                {review.commitStatus === "Committed" ? (
                  <div className="space-y-3">
                    <Panel className="border-primary/30 bg-primary-soft/30 p-4 space-y-2">
                      <div className="flex items-center gap-2 text-primary font-semibold text-[13.5px]">
                        <GitBranch className="h-4 w-4 shrink-0" />
                        <span>{t("review.committedLocally")}</span>
                        <span className="font-mono text-[11px] text-muted-foreground">({review.commitSha?.slice(0, 7)})</span>
                      </div>
                      {review.committedAt && (
                        <div className="font-mono text-[11px] text-subtle-foreground">
                          {t("review.committedAt", { when: fmt.dateTime(review.committedAt) })}
                        </div>
                      )}
                    </Panel>

                    {review.pushStatus === "Pushed" ? (
                      <div className="space-y-3">
                        <Panel className="border-success/30 bg-success-soft/30 p-4 space-y-2">
                          <div className="flex items-center gap-2 text-success font-semibold text-[13.5px]">
                            <UploadCloud className="h-4 w-4 shrink-0" />
                            <span>{t("review.pushedRemotely")}</span>
                          </div>
                          <div className="font-mono text-[11.5px] text-foreground">
                            {review.remoteBranchName || review.branchName} <span className="text-subtle-foreground">({review.remoteCommitSha?.slice(0, 7)})</span>
                          </div>
                          {review.pushedAt && (
                            <div className="font-mono text-[11px] text-subtle-foreground">
                              {t("review.pushedAt", { when: fmt.dateTime(review.pushedAt) })}
                            </div>
                          )}
                        </Panel>

                        {review.pullRequestStatus === "Open" ? (
                          <Panel className="border-success/30 bg-success-soft/30 p-4 space-y-3">
                            <div className="flex items-center justify-between">
                              <div className="flex items-center gap-2 text-success font-semibold text-[13.5px]">
                                <GitPullRequest className="h-4 w-4 shrink-0" />
                                <span>{t("review.prNumber", { n: review.pullRequestNumber })}</span>
                                <Badge tone={review.pullRequestRemoteState === "Merged" ? "green" : review.pullRequestRemoteState === "Closed" ? "red" : "blue"}>
                                  {ciText(review.pullRequestRemoteState ?? "Open")}
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
                                {t("review.refresh")}
                              </Button>
                            </div>

                            {/* Integrity Badge */}
                            {review.pullRequestIntegrityStatus && review.pullRequestIntegrityStatus !== "Unknown" && (
                              <div className="flex items-center gap-1.5 text-[11.5px] font-mono">
                                <span className="text-subtle-foreground">{t("review.integrity")}</span>
                                {review.pullRequestIntegrityStatus === "Valid" ? (
                                  <span className="text-success font-medium">{t("review.integrityValid")}</span>
                                ) : review.pullRequestIntegrityStatus === "HeadChanged" ? (
                                  <span className="text-danger font-medium">{t("review.integrityHead")}</span>
                                ) : (
                                  <span className="text-danger font-medium">{t("review.integrityMismatch")}</span>
                                )}
                              </div>
                            )}

                            {/* CI Aggregate Status */}
                            {review.ciStatus && (
                              <div className="flex items-center justify-between border-t border-border/40 pt-2 text-[12px]">
                                <span className="text-subtle-foreground">{t("review.ciStatus")}</span>
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
                                  {ciText(review.ciStatus)}
                                </Badge>
                              </div>
                            )}

                            {/* CI Checks List */}
                            {review.ciChecks && review.ciChecks.length > 0 && (
                              <div className="space-y-1.5 border-t border-border/40 pt-2 font-mono text-[11px]">
                                <div className="text-subtle-foreground font-semibold text-[10px] uppercase">{t("review.checks", { n: review.ciChecks.length })}</div>
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
                                      {ciText(check.conclusion || check.status)}
                                    </span>
                                  </div>
                                ))}
                              </div>
                            )}

                            {syncError && (
                              <div className="text-[11px] text-danger bg-danger-soft/60 p-2 rounded-[var(--radius-md)]">
                                {t("review.refreshFailed", { error: syncError })}
                              </div>
                            )}

                            {review.pullRequestLastSyncedAt && (
                              <div className="font-mono text-[10.5px] text-subtle-foreground">
                                {t("review.lastSynced", { when: fmt.time(review.pullRequestLastSyncedAt) })}
                              </div>
                            )}

                            {review.pullRequestUrl && (
                              <a
                                href={review.pullRequestUrl}
                                target="_blank"
                                rel="noreferrer"
                                className="inline-flex items-center gap-1 font-mono text-[12px] text-primary hover:underline pt-1"
                              >
                                {t("review.viewGitHub")}
                              </a>
                            )}

                            {review.mergeStatus === "Merged" ? (
                              <div className="mt-3 rounded-[var(--radius-md)] border border-emerald-500/30 bg-emerald-500/10 p-3 space-y-1.5 font-mono text-[11px]">
                                <div className="flex items-center justify-between text-emerald-400 font-semibold text-[12.5px]">
                                  <div className="flex items-center gap-1.5">
                                    <CheckCircle2 className="h-4 w-4 shrink-0 text-emerald-400" />
                                    <span>{t("review.prMerged")}</span>
                                  </div>
                                  <Badge tone="green">{t("review.merged")}</Badge>
                                </div>
                                {review.mergeCommitSha && (
                                  <div className="text-subtle-foreground truncate">
                                    {t("review.commitLabel")}<span className="text-foreground font-semibold">{review.mergeCommitSha.slice(0, 7)}</span>
                                  </div>
                                )}
                                {review.mergedAt && (
                                  <div className="text-subtle-foreground">
                                    {t("review.mergedLabel")}<span className="text-foreground">{fmt.dateTime(review.mergedAt)}</span>
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
                                {t("review.mergePr")}
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
                            disabled={!review.canRequestPullRequest || isSubmittingPr || noPrSupport}
              title={noPrSupport ? t("review.noPrHost") : undefined}
                            onClick={handleCreatePullRequest}
                            className="w-full"
                          >
                            {isSubmittingPr ? (
                              <Loader2 className="h-4 w-4 animate-spin" />
                            ) : (
                              <GitPullRequest className="h-4 w-4" />
                            )}
                            {t("review.openPr")}
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
                        {t("review.pushBranch")}
                      </Button>
                    )}
                  </div>
                ) : !review.approvedSnapshotMatchesCurrent ? (
                  <Panel className="border-amber/30 bg-amber-soft/40 p-4 space-y-2">
                    <div className="flex items-center gap-2 text-accent font-semibold text-[13px]">
                      <AlertTriangle className="h-4 w-4 shrink-0" />
                      <span>{t("review.snapshotChanged")}</span>
                    </div>
                    <p className="text-[12px] text-muted-foreground">
                      {t("review.snapshotChangedDesc")}
                    </p>
                    <Button variant="default" size="md" disabled className="w-full opacity-50 cursor-not-allowed">
                      {t("review.commitChanges")}
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
                    {t("review.commitChanges")}
                  </Button>
                )}
              </div>
            )}

            {isRejected && (
              <Panel className="border-danger/30 bg-danger-soft/30 p-4 space-y-2">
                <div className="flex items-center gap-2 text-danger font-semibold text-[13.5px]">
                  <XCircle className="h-4 w-4 shrink-0" />
                  <span>{t("review.reviewRejected")}</span>
                </div>
                {review.decidedAt && (
                  <div className="font-mono text-[11px] text-subtle-foreground">
                    {t("review.decidedAt", { when: fmt.dateTime(review.decidedAt) })}
                  </div>
                )}
                {review.rejectionReason && (
                  <div className="mt-2 rounded-[var(--radius-md)] border border-danger/20 bg-surface p-2.5 text-[12px] text-foreground">
                    <span className="font-semibold block mb-0.5 text-danger text-[11px] uppercase tracking-wider">{t("review.reason")}</span>
                    {review.rejectionReason}
                  </div>
                )}
              </Panel>
            )}
          </div>
        </aside>
      </div>

      {/* Request Changes Modal */}
      {showChangesModal && (
        <RequestChangesModal
          pullRequestNumber={review.pullRequestStatus === "Open" ? review.pullRequestNumber : null}
          isSubmitting={isRequestingChanges}
          error={changesError}
          onClose={() => setShowChangesModal(false)}
          onSubmit={handleRequestChanges}
        />
      )}

      {/* Reject Modal */}
      {showRejectModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-xs p-4">
          <div className="w-full max-w-[480px] rounded-[var(--radius-lg)] border border-border bg-canvas p-6 shadow-xl space-y-4">
            <h3 className="text-[15px] font-semibold text-foreground">{t("review.rejectModalTitle")}</h3>
            <p className="text-[12.5px] text-muted-foreground">
              {t("review.rejectModalDesc")}
            </p>
            <textarea
              className="w-full h-24 rounded-[var(--radius-md)] border border-border bg-surface p-3 font-sans text-[13px] text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
              placeholder={t("review.rejectPlaceholder")}
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
                {t("review.cancel")}
              </Button>
              <Button
                variant="default"
                size="sm"
                disabled={isSubmittingDecision}
                onClick={handleRejectSubmit}
                className="bg-danger text-white hover:bg-danger/90 border-transparent"
              >
                {isSubmittingDecision ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : t("review.confirmRejection")}
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
              <span>{t("review.mergeTitle", { n: review.pullRequestNumber })}</span>
            </div>

            {mergeError && (
              <div className="rounded-[var(--radius-md)] border border-danger/30 bg-danger-soft/80 p-3 text-[12.5px] text-danger flex items-start gap-2">
                <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                <span>{mergeError}</span>
              </div>
            )}

            <div className="rounded-[var(--radius-md)] border border-border/60 bg-surface p-3.5 space-y-2 font-mono text-[12px]">
              <div className="flex justify-between text-subtle-foreground">
                <span>{t("review.baseBranch")}</span>
                <span className="text-foreground font-semibold">master</span>
              </div>
              <div className="flex justify-between text-subtle-foreground">
                <span>{t("review.headBranch")}</span>
                <span className="text-foreground font-semibold">{review.remoteBranchName}</span>
              </div>
              <div className="flex justify-between text-subtle-foreground">
                <span>{t("review.approvedCommit")}</span>
                <span className="text-foreground font-semibold">{review.remoteCommitSha?.slice(0, 7)}</span>
              </div>
              <div className="flex justify-between text-subtle-foreground border-t border-border/40 pt-1.5">
                <span>{t("review.ciStatus")}</span>
                <span className="text-emerald-400 font-semibold">{ciText(review.ciStatus)}</span>
              </div>
            </div>
            <p className="text-[12.5px] text-muted-foreground">
              {t("review.mergeDesc")}
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
                {t("review.cancel")}
              </Button>
              <Button
                variant="primary"
                size="sm"
                disabled={isSubmittingMerge}
                onClick={handleConfirmMerge}
                className="bg-emerald-600 hover:bg-emerald-500 text-white"
              >
                {isSubmittingMerge ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : t("review.confirmMerge")}
              </Button>
            </div>
          </div>
        </div>
      )}
    </PageContainer>
  )
}
