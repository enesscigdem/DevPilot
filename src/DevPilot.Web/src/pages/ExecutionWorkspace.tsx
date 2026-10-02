import { useCallback, useEffect, useRef, useState } from "react"
import { Link, useNavigate, useParams } from "react-router-dom"
import { useTranslation } from "react-i18next"
import i18n, { fmt, srv } from "@/i18n"
import {
  ArrowLeft,
  Check,
  CircleDot,
  Clock,
  Eye,
  FileCode2,
  FileText,
  GitBranch,
  Hammer,
  FlaskConical,
  Play,
  Terminal,
  X,
  Loader2,
  AlertCircle,
  RotateCcw,
  ChevronDown,
  ChevronUp,
  Cpu,
} from "lucide-react"
import { Button, Badge, Panel, StatusDot } from "@/components/ui/primitives"
import { ExecutionTabs } from "@/components/ExecutionTabs"
import { UsagePanel, VerdictCard } from "@/components/VerdictCard"
import { cn } from "@/lib/utils"
import { getExecution, getExecutionActivity, retryExecution, cancelExecution, getExecutions } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import {
  TaskExecutionStatus,
  getExecutionStatusMeta,
  type ExecutionDetail,
  type ExecutionActivityItem,
  type ExecutionListItem,
} from "@/types"
import { stages } from "@/lib/executionStages"

function getStageState(
  stageIndex: number,
  status: number,
  reviewStatus?: string,
  pullRequestStatus?: string,
  verificationOutcome?: string,
): "done" | "active" | "todo" | "failed" | "blocked" | "needsreview" {
  if (stageIndex === 6) {
    const pr = String(pullRequestStatus || "").toLowerCase()
    if (pr === "open" || pr === "merged") return "done"
    if (pr === "inprogress") return "active"
    if (pr === "failed") return "failed"
    return "todo"
  }
  if (status === TaskExecutionStatus.Completed) {
    if (stageIndex === 4 && verificationOutcome === "NeedsReview") return "needsreview"
    if (stageIndex <= 4) return "done"
    if (stageIndex === 5) {
      const r = String(reviewStatus || "").toLowerCase()
      if (r === "approved") return "done"
      if (r === "rejected") return "failed"
      return "active"
    }
    return "todo"
  }
  if (status === TaskExecutionStatus.Failed) {
    if (stageIndex < 4) return "done"
    if (stageIndex === 4) return "failed"
    return "todo"
  }
  if (status === TaskExecutionStatus.Cancelled) {
    if (stageIndex < 3) return "done"
    if (stageIndex === 3) return "blocked"
    return "todo"
  }
  if (status === TaskExecutionStatus.Running) {
    if (stageIndex < 3) return "done"
    if (stageIndex === 3) return "active"
    return "todo"
  }
  // Pending (0) or default: execution has not started, all stages todo/pending
  return "todo"
}

function formatDateTime(dateStr: string | null): string {
  if (!dateStr) return i18n.t("execWs.notAvailable")
  try {
    return fmt.dateTime(dateStr)
  } catch {
    return dateStr
  }
}

function formatTimeOnly(dateStr: string): string {
  try {
    return fmt.time(dateStr, { hour: "2-digit", minute: "2-digit", second: "2-digit" })
  } catch {
    return dateStr
  }
}

function getMetadataDisplay(act: ExecutionActivityItem, verificationOutcome?: string | null): string | null {
  const t = i18n.t.bind(i18n)
  if (act.metadata) {
    const m = act.metadata
    if (m.eventKind === "ProviderCall") {
      const budget = m.requestedOutputTokens ? ` · ${t("execWs.meta.budget")} ${m.requestedOutputTokens}` : ""
      const actual = m.outputTokens !== undefined && m.outputTokens !== null ? ` · ${t("execWs.meta.output")} ${m.outputTokens}` : ""
      const duration = m.stageDurationMs !== undefined && m.stageDurationMs !== null ? ` · ${m.stageDurationMs}ms` : ""
      const contract = m.outputContract ? ` · ${m.outputContract}` : ""
      const retry = m.compactRetryReason ? ` · ${m.compactRetryReason}` : ""
      return `${m.providerCallKind ?? t("execWs.meta.providerCall")}${contract}${budget}${actual}${retry}${duration}`
    }
    if (m.eventKind === "CompactRetry") {
      const budget = m.requestedOutputTokens ? ` · ${t("execWs.meta.budget")} ${m.requestedOutputTokens}` : ""
      return `${t("execWs.meta.compactRetryLine", { reason: m.compactRetryReason ?? "TokenTruncation" })}${budget}`
    }
    if (m.eventKind === "GenerationSummary") {
      return t("execWs.meta.calls", {
        calls: m.logicalProviderCallCount ?? 0,
        compact: m.compactRetryCount ?? 0,
        applicability: m.applicabilityRepairCount ?? 0,
        ms: m.totalGenerationTimeMs ?? 0,
      })
    }
    if (m.eventKind === "ResolvedNoChange") {
      const reason = m.noChangeReason ? ` · ${m.noChangeReason}` : ""
      return `${t("execWs.meta.noChange", { target: m.targetFile ?? t("execWs.meta.target") })}${reason}`
    }
    if (m.eventKind === "RepositoryPreflight") {
      const ecosystems = m.detectedEcosystems?.join(", ") || t("execWs.meta.unknownEcosystem")
      const unresolved = m.verificationUnresolved ? ` · ${t("execWs.meta.partialDiscovery")}` : ""
      return `${t("execWs.meta.checks", { n: m.discoveredCheckCount ?? 0, eco: ecosystems })}${unresolved}`
    }
    if (m.repositoryCheckId && !(m.repairKind && m.repairRound)) {
      const duration = m.stageDurationMs !== undefined && m.stageDurationMs !== null ? ` · ${m.stageDurationMs}ms` : ""
      const exitCode = m.processExitCode !== undefined && m.processExitCode !== null ? ` · ${t("execWs.meta.exit")} ${m.processExitCode}` : ""
      const failure = m.verificationFailureCategory ? ` · ${m.verificationFailureCategory}` : ""
      return `${m.repositoryCheckKind ?? t("execWs.meta.check")} · ${m.repositoryCheckId}${exitCode}${failure}${duration}`
    }
    if (m.repairKind && m.repairRound) {
      const targets = m.repairFiles?.length ? ` · ${m.repairFiles.join(", ")}` : ""
      const progress = m.progressResult ? ` · ${m.progressResult}` : ""
      const reason = m.repairSelectionReason ? ` · ${m.repairSelectionReason}` : ""
      const testName = m.testName ? ` · ${m.testName}` : ""
      const evidence = m.diagnosticLines?.length ? ` · ${m.diagnosticLines.slice(0, 5).join(" | ")}` : ""
      const check = m.repositoryCheckKind ? ` · ${m.repositoryCheckKind}` : ""
      return `${t("execWs.meta.repair", { kind: m.repairKind, round: m.repairRound })}${targets}${reason}${testName}${progress}${check}${evidence}`
    }
    if (m.modifiedFileCount !== undefined && m.modifiedFileCount !== null) {
      return t("execWs.meta.filesModified", { count: m.modifiedFileCount })
    }
    if (m.branchName) {
      return t("execWs.meta.branch", { name: m.branchName })
    }
  }
  if (act.stage === "Execution" && act.status === "Completed") {
    return verificationOutcome === "NeedsReview" ? t("execWs.meta.needsReviewAuth") : t("execWs.meta.readyForReview")
  }
  return null
}

function getPrimaryActivityLabel(act: ExecutionActivityItem): string {
  const t = i18n.t.bind(i18n)
  switch (act.metadata?.eventKind) {
    case "GeneratingChange":
      return act.status === "Completed" ? t("execWs.act.generatingChangeDone") : t("execWs.act.generatingChange")
    case "VerifyingRepository":
    case "RepositoryPreflight":
      return t("execWs.act.verifying")
    case "FixingBuildIssue":
      return t("execWs.act.fixingBuild")
    case "FixingFailingTest":
      return t("execWs.act.fixingTest")
    case "ReadyForReview":
      return t("execWs.act.ready")
    case "StoppedWithEvidence":
      return t("execWs.act.stopped")
    case "ResolvedNoChange":
      return t("execWs.act.noChange")
    default:
      return srv(act.message)
  }
}

export function ExecutionWorkspace() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { id } = useParams<{ id: string }>()
  const { activeWorkspaceId, isLoading: isWorkspaceLoading, refreshOverview } = useWorkspace()
  const activeReqWorkspaceIdRef = useRef<string | null>(activeWorkspaceId)

  useEffect(() => {
    activeReqWorkspaceIdRef.current = activeWorkspaceId
  }, [activeWorkspaceId])

  const [execution, setExecution] = useState<ExecutionDetail | null>(null)
  const [activities, setActivities] = useState<ExecutionActivityItem[]>([])
  const [activeExecutionForTask, setActiveExecutionForTask] = useState<ExecutionListItem | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [isRetrying, setIsRetrying] = useState(false)
  const [retryError, setRetryError] = useState<string | null>(null)
  const [isCanceling, setIsCanceling] = useState(false)
  const [cancelError, setCancelError] = useState<string | null>(null)
  const [showGenDetails, setShowGenDetails] = useState(false)
  const [nowMs, setNowMs] = useState(() => Date.now())

  const isRunningExecution = execution?.status === TaskExecutionStatus.Running

  useEffect(() => {
    if (!isRunningExecution) return
    const timer = setInterval(() => {
      setNowMs(Date.now())
    }, 1000)
    return () => clearInterval(timer)
  }, [isRunningExecution])

  const handleRetryExecution = async () => {
    if (!execution || isRetrying) return
    setIsRetrying(true)
    setRetryError(null)

    try {
      const newExecution = await retryExecution(execution.developmentTaskId, activeWorkspaceId)
      refreshOverview(true)
      navigate(`/executions/${newExecution.id}`)
    } catch (err) {
      await fetchData(false)
      setRetryError(err instanceof Error ? err.message : t("execWs.errRetry"))
    } finally {
      setIsRetrying(false)
    }
  }

  const handleCancelExecution = async () => {
    if (!execution || isCanceling) return
    setIsCanceling(true)
    setCancelError(null)

    try {
      await cancelExecution(execution.id, activeWorkspaceId)
      refreshOverview(true)
      await fetchData(false)
    } catch (err) {
      setCancelError(err instanceof Error ? err.message : t("execWs.errCancel"))
    } finally {
      setIsCanceling(false)
    }
  }

  const activeRequestIdRef = useRef(0)

  const fetchData = useCallback(async (showLoadingSpinner = false, signal?: AbortSignal) => {
    if (!id || isWorkspaceLoading) return
    const currentRequestId = ++activeRequestIdRef.current

    if (showLoadingSpinner) {
      setIsLoading(true)
      setError(null)
    }

    try {
      const [execData, actData, allExecs] = await Promise.all([
        getExecution(id, activeWorkspaceId, { signal }),
        getExecutionActivity(id, activeWorkspaceId, { signal }).catch(() => []),
        getExecutions(activeWorkspaceId, { signal }).catch(() => []),
      ])

      if (currentRequestId === activeRequestIdRef.current && activeReqWorkspaceIdRef.current === activeWorkspaceId) {
        if (activeWorkspaceId && execData.repositoryWorkspaceId && execData.repositoryWorkspaceId !== activeWorkspaceId) {
          setError(t("execWs.wrongWorkspace", { id }))
          setExecution(null)
          setActiveExecutionForTask(null)
        } else {
          setExecution(execData)
          setActivities(actData)
          const activeForTask = allExecs.find(
            (e) =>
              e.developmentTaskId === execData.developmentTaskId &&
              (e.status === TaskExecutionStatus.Pending || e.status === TaskExecutionStatus.Running) &&
              e.id !== execData.id,
          )
          setActiveExecutionForTask(activeForTask ?? null)
          setError(null)
        }
      }
    } catch (err) {
      if (signal?.aborted) return
      if (currentRequestId === activeRequestIdRef.current && activeReqWorkspaceIdRef.current === activeWorkspaceId) {
        if (showLoadingSpinner) {
          setError(err instanceof Error ? err.message : t("execWs.errLoad"))
          setExecution(null)
          setActiveExecutionForTask(null)
        }
      }
    } finally {
      if (currentRequestId === activeRequestIdRef.current && activeReqWorkspaceIdRef.current === activeWorkspaceId) {
        if (showLoadingSpinner) {
          setIsLoading(false)
        }
      }
    }
  }, [id, isWorkspaceLoading, activeWorkspaceId])

  useEffect(() => {
    if (isWorkspaceLoading || !id) {
      setIsLoading(true)
      return
    }

    const controller = new AbortController()
    fetchData(true, controller.signal)
    return () => controller.abort()
  }, [id, isWorkspaceLoading, activeWorkspaceId, fetchData])

  // Polling loop while execution is Pending (0) or Running (1)
  useEffect(() => {
    if (isWorkspaceLoading || !execution) return
    const isRunningOrPending =
      execution.status === TaskExecutionStatus.Pending ||
      execution.status === TaskExecutionStatus.Running

    if (!isRunningOrPending) return

    const interval = setInterval(() => {
      fetchData(false)
    }, 2000)

    return () => clearInterval(interval)
  }, [isWorkspaceLoading, execution, fetchData])

  if (isWorkspaceLoading || isLoading) {
    return (
      <div className="flex h-[calc(100vh-100px)] w-full items-center justify-center">
        <div className="flex flex-col items-center gap-3 text-center">
          <Loader2 className="h-6 w-6 animate-spin text-subtle-foreground" />
          <p className="text-[13.5px] font-medium text-foreground">{t("execWs.loading")}</p>
        </div>
      </div>
    )
  }

  if (error || !execution) {
    return (
      <div className="mx-auto max-w-[800px] px-6 py-16">
        <Panel className="flex flex-col items-center justify-center gap-3 p-8 text-center">
          <AlertCircle className="h-8 w-8 text-danger" />
          <div>
            <h2 className="text-[16px] font-semibold text-foreground">{t("execWs.notFound")}</h2>
            <p className="mt-1 text-[13px] text-muted-foreground">
              {error || t("execWs.notFoundDesc", { id })}
            </p>
          </div>
          <div className="mt-2 flex items-center gap-3">
            <Button variant="default" size="sm" onClick={() => fetchData(true)}>
              {t("common.retry")}
            </Button>
            <Link to="/executions">
              <Button variant="primary" size="sm">
                <ArrowLeft className="h-3.5 w-3.5" />
                {t("execWs.backToExecutions")}
              </Button>
            </Link>
          </div>
        </Panel>
      </div>
    )
  }

  const statusMeta = getExecutionStatusMeta(execution.status, execution.verificationOutcome)
  const isRunning = execution.status === TaskExecutionStatus.Running
  const isPending = execution.status === TaskExecutionStatus.Pending
  const isFailed = execution.status === TaskExecutionStatus.Failed
  const isCancelled = execution.status === TaskExecutionStatus.Cancelled
  const canRetryExecution = Boolean(execution.canRetry) || isFailed || isCancelled

  // Authoritative build/test outcome derived from final validation activity and execution status
  const buildActivities = activities.filter((a) => a.stage === "Build" && (a.status === "Completed" || a.status === "Failed"))
  const lastBuildAct = buildActivities.length > 0 ? buildActivities[buildActivities.length - 1] : null
  const lastBuildMeta = activities.slice().reverse().find((a) => a.metadata?.buildPassed !== undefined && a.metadata?.buildPassed !== null)?.metadata?.buildPassed

  const buildFailed =
    lastBuildMeta === false ||
    (lastBuildAct ? lastBuildAct.status === "Failed" : false) ||
    (isFailed && activities.some((a) => a.stage === "Build" && a.status === "Failed"))

  const buildPassed =
    !buildFailed &&
    (lastBuildMeta === true ||
      (lastBuildAct ? lastBuildAct.status === "Completed" && !lastBuildAct.message.includes("Compile repair") : false))

  const testActivities = activities.filter((a) => a.stage === "Test" && (a.status === "Completed" || a.status === "Failed"))
  const lastTestAct = testActivities.length > 0 ? testActivities[testActivities.length - 1] : null
  const lastTestMeta = activities.slice().reverse().find((a) => a.metadata?.testPassed !== undefined && a.metadata?.testPassed !== null)?.metadata?.testPassed

  const testFailed =
    lastTestMeta === false ||
    (lastTestAct ? lastTestAct.status === "Failed" : false)

  const testPassed =
    !testFailed &&
    (lastTestMeta === true ||
      (lastTestAct ? lastTestAct.status === "Completed" : false))

  return (
    <div className="w-full">
      {/* Header */}
      <div className="sticky top-0 z-10 border-b border-border bg-canvas/85 px-6 py-3 backdrop-blur-sm">
        <div className="mx-auto flex max-w-[1500px] items-center gap-3">
          <Link
            to="/executions"
            className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] text-muted-foreground hover:bg-surface-3 hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <StatusDot tone={statusMeta.tone} pulse={isRunning} />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="font-mono text-[11px] text-subtle-foreground">{execution.id}</span>
              <h1 className="truncate text-[14.5px] font-semibold text-foreground">{execution.taskTitle}</h1>
              <Badge tone={statusMeta.tone}>{statusMeta.label}</Badge>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              {execution.repositoryOwner}/{execution.repositoryName}
            </div>
            {(retryError || cancelError) && (
              <div className="mt-1 flex items-center gap-1.5 text-[11.5px] font-medium text-danger">
                <AlertCircle className="h-3 w-3 shrink-0" />
                <span>{retryError || cancelError}</span>
              </div>
            )}
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="default"
              size="sm"
              onClick={() => navigate(`/tasks/${execution.developmentTaskId}`)}
            >
              <Eye className="h-3.5 w-3.5" />
              {t("execWs.viewTask")}
            </Button>
            {(isPending || isRunning) && (
              <Button
                variant="default"
                size="sm"
                disabled={isCanceling}
                onClick={handleCancelExecution}
                className="text-danger hover:bg-danger/10 hover:text-danger border-danger/30"
              >
                {isCanceling ? (
                  <>
                    <Loader2 className="h-3.5 w-3.5 animate-spin" />
                    {t("execWs.canceling")}
                  </>
                ) : (
                  <>
                    <X className="h-3.5 w-3.5" />
                    {t("execWs.cancelExecution")}
                  </>
                )}
              </Button>
            )}
            {canRetryExecution && (
              activeExecutionForTask ? (
                <Button
                  variant="default"
                  size="sm"
                  className="gap-1.5"
                  onClick={() => navigate(`/executions/${activeExecutionForTask.id}`)}
                >
                  <Play className="h-3.5 w-3.5 text-primary" />
                  {t("execWs.viewActive")}
                </Button>
              ) : (
                <Button
                  variant="primary"
                  size="sm"
                  disabled={isRetrying}
                  onClick={handleRetryExecution}
                >
                  {isRetrying ? (
                    <>
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      {t("execWs.retrying")}
                    </>
                  ) : (
                    <>
                      <RotateCcw className="h-3.5 w-3.5" />
                      {t("execWs.retryExecution")}
                    </>
                  )}
                </Button>
              )
            )}
            <Button
              variant={canRetryExecution ? "default" : "primary"}
              size="sm"
              disabled={isPending || isRunning || isCancelled}
              onClick={() => navigate(`/review/${execution.id}`)}
            >
              <FileCode2 className="h-3.5 w-3.5" />
              {t("execWs.codeReview")}
            </Button>
          </div>
        </div>
      </div>

      <ExecutionTabs
        executionId={execution.id}
        taskId={execution.developmentTaskId}
        active="run"
        reviewAvailable={!isPending && !isRunning && execution.status === TaskExecutionStatus.Completed}
      />

      <div className="mx-auto grid max-w-[1500px] grid-cols-1 gap-0 lg:grid-cols-[240px_minmax(0,1fr)_320px]">
        {/* LEFT — stage rail */}
        <aside className="border-b border-border p-5 lg:border-b-0 lg:border-r">
          <div className="tech-label mb-3">{t("execWs.pipeline")}</div>
          <ol className="relative">
            {stages.map((st, i) => {
              const backendState = execution.stages?.[i]?.state?.toLowerCase()
              const state = (backendState as "done" | "active" | "failed" | "blocked" | "todo" | "needsreview") ||
                getStageState(i, execution.status, execution.reviewStatus, execution.pullRequestStatus, execution.verificationOutcome)

              return (
                <li key={st.key} className="relative flex gap-3 pb-5 last:pb-0">
                  {i < stages.length - 1 && (
                    <span
                      className={cn(
                        "absolute left-[9px] top-5 h-full w-px",
                        state === "done" ? "bg-success/50" : "bg-border",
                      )}
                    />
                  )}
                  <span
                    className={cn(
                      "relative z-10 flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-full border",
                      state === "done"
                        ? "border-success bg-success text-primary-foreground"
                        : state === "active"
                          ? "border-primary bg-surface"
                          :                         state === "failed"
                            ? "border-danger bg-danger text-primary-foreground"
                            : state === "needsreview"
                              ? "border-amber-500 bg-amber-500 text-primary-foreground"
                            : state === "blocked"
                              ? "border-accent bg-accent text-primary-foreground"
                              : "border-border bg-surface",
                    )}
                  >
                    {state === "done" ? (
                      <Check className="h-2.5 w-2.5" />
                    ) : state === "active" ? (
                      <CircleDot className="h-3 w-3 animate-pulse-dot text-primary" />
                    ) : state === "failed" ? (
                      <X className="h-2.5 w-2.5" />
                    ) : state === "needsreview" ? (
                      <AlertCircle className="h-2.5 w-2.5" />
                    ) : state === "blocked" ? (
                      <X className="h-2 w-2" />
                    ) : (
                      <span className="h-1.5 w-1.5 rounded-full bg-subtle-foreground" />
                    )}
                  </span>
                  <div className="pt-px">
                    <div
                      className={cn(
                        "text-[12.5px] font-medium",
                        state === "todo" ? "text-subtle-foreground" : "text-foreground",
                      )}
                    >
                      {st.label}
                    </div>
                    {state === "active" && (
                      <span className="font-mono text-[10.5px] text-primary">
                        {i === 5 ? t("execWs.reviewReady") : t("execWs.inProgress")}
                      </span>
                    )}
                    {state === "done" && i === 5 && (
                      <span className="font-mono text-[10.5px] text-success">{t("execWs.approved")}</span>
                    )}
                    {state === "failed" && (
                      <span className="font-mono text-[10.5px] text-danger">
                        {i === 5 ? t("execWs.rejected") : t("execWs.failedHere")}
                      </span>
                    )}
                    {state === "needsreview" && (
                      <span className="font-mono text-[10.5px] text-amber-600 dark:text-amber-400">
                        {t("execWs.needsReview")}
                      </span>
                    )}
                    {state === "blocked" && <span className="font-mono text-[10.5px] text-accent">{t("execWs.cancelled")}</span>}
                  </div>
                </li>
              )
            })}
          </ol>
        </aside>

        {/* CENTER — activity stream */}
        <section className="flex min-h-[calc(100vh-113px)] flex-col border-b border-border lg:border-b-0">
          <div className="flex items-center justify-between border-b border-border px-5 py-3">
            <div className="flex items-center gap-2">
              <Terminal className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="text-[13px] font-semibold text-foreground">{t("execWs.activity")}</span>
            </div>
            <span className="font-mono text-[11px] text-subtle-foreground">
              {t("execWs.events", { count: activities.length })}
            </span>
          </div>

          <div className="flex-1 overflow-y-auto px-5 py-6">
            {activities.length === 0 ? (
              isPending || isRunning ? (
                <div className="flex flex-col items-center justify-center gap-2 py-16 text-center text-subtle-foreground">
                  <Clock className="h-6 w-6 animate-pulse text-primary" />
                  <p className="text-[13.5px] font-medium text-foreground">{t("execWs.waiting")}</p>
                  <p className="font-mono text-[11px]">
                    {isRunning
                      ? t("execWs.startedAt", { when: formatDateTime(execution.startedAt) })
                      : t("execWs.createdOn", { when: formatDateTime(execution.createdAt) })}
                  </p>
                </div>
              ) : (
                <div className="flex flex-col items-center justify-center gap-2 py-16 text-center text-subtle-foreground">
                  <Clock className="h-6 w-6 text-subtle-foreground" />
                  <p className="text-[13.5px] font-medium text-foreground">
                    {t("execWs.noActivity")}
                  </p>
                </div>
              )
            ) : (
              <div className="space-y-4">
                {(() => {
                  // Structured execution viewer state derivation
                  let totalPlannedFiles = 0
                  const fileMap = new Map<string, {
                    fileName: string
                    status: "running" | "completed" | "repairing" | "retrying" | "failed"
                    durationSec?: number
                    badge?: string
                    startedAtMs: number
                    completedAtMs?: number
                  }>()

                  const repairItems: Array<{
                    id: string
                    message: string
                    detail?: string
                    status: "running" | "completed" | "failed"
                  }> = []

                  let buildState: "idle" | "running" | "passed" | "failed" = "idle"
                  let buildLabel = t("execWs.build")
                  let buildDuration = ""

                  let testState: "idle" | "running" | "passed" | "failed" = "idle"
                  let testLabel = t("execWs.tests")
                  let testDuration = ""

                  // Single pass over activities in chronological order
                  for (const act of activities) {
                    const msg = act.message || ""
                    const actTime = new Date(act.createdAt).getTime()

                    // 1. Total files planned
                    const prepMatch = msg.match(/Preparing\s+(\d+)\s+file/i)
                    if (prepMatch) {
                      totalPlannedFiles = Math.max(totalPlannedFiles, parseInt(prepMatch[1], 10))
                    }

                    // 2. File generation started
                    const genStartMatch = msg.match(/Generating edit\s+(\d+)\/(\d+)\s*·\s*([^\s·]+)/i)
                    if (genStartMatch) {
                      totalPlannedFiles = Math.max(totalPlannedFiles, parseInt(genStartMatch[2], 10))
                      const fileName = genStartMatch[3]
                      if (!fileMap.has(fileName) || fileMap.get(fileName)?.status !== "completed") {
                        fileMap.set(fileName, {
                          fileName,
                          status: "running",
                          startedAtMs: actTime,
                        })
                      }
                    }

                    // 3. Compact retry
                    const compactMatch = msg.match(/Performing compact generation retry for\s+([^\s·]+)(?:\s*\(budget\s*(\d+\s*->\s*\d+)\))?/i)
                    if (compactMatch) {
                      const fileName = compactMatch[1]
                      const budgetTransition = compactMatch[2] ? ` · ${compactMatch[2]}` : ""
                      const existing = fileMap.get(fileName)
                      if (existing) {
                        existing.status = "retrying"
                        existing.badge = `${t("execWs.compactRetry")}${budgetTransition} · ${t("execWs.outputLimit")}`
                      }
                    }

                    // 4. Applicability repair
                    const repairMatch = msg.match(/Repair triggered for\s+([^\s·:]+)(?::\s*([^\n]+))?/i)
                    if (repairMatch) {
                      const fileName = repairMatch[1]
                      const reason = repairMatch[2] ? ` · ${repairMatch[2].substring(0, 30)}` : ""
                      const existing = fileMap.get(fileName)
                      if (existing) {
                        existing.status = "repairing"
                        existing.badge = `${t("execWs.applicabilityRepair")}${reason}`
                      }
                    }

                    // 5. File generation completed
                    const genDoneMatch = msg.match(/Generated edit\s+(\d+)\/(\d+)\s*·\s*([^\s·]+)(?:\s*·\s*(\d+)s)?/i)
                    if (genDoneMatch) {
                      totalPlannedFiles = Math.max(totalPlannedFiles, parseInt(genDoneMatch[2], 10))
                      const fileName = genDoneMatch[3]
                      const durSec = genDoneMatch[4] ? parseInt(genDoneMatch[4], 10) : undefined
                      const existing = fileMap.get(fileName)
                      fileMap.set(fileName, {
                        fileName,
                        status: "completed",
                        durationSec: durSec ?? (existing ? Math.max(1, Math.round((actTime - existing.startedAtMs) / 1000)) : undefined),
                        badge: existing?.badge,
                        startedAtMs: existing?.startedAtMs ?? actTime,
                        completedAtMs: actTime,
                      })
                    }

                    // 6. Compile repair / Stage repair
                    if (act.metadata?.repairKind && act.metadata?.repairRound) {
                      const fileCount = act.metadata.repairFiles?.length ?? act.metadata.modifiedFileCount
                      const scope = fileCount ? ` · ${t("common.files", { count: fileCount })}` : ""
                      const fileNames = act.metadata.repairFiles?.map(f => f.split("/").pop()).filter(Boolean).join(", ")
                      repairItems.push({
                        id: act.id,
                        message: `${t("execWs.meta.repair", { kind: act.metadata.repairKind, round: act.metadata.repairRound })}${scope}`,
                        detail: fileNames || undefined,
                        status: act.status === "Completed" ? "completed" : act.status === "Failed" ? "failed" : "running",
                      })
                    } else if (msg.includes("Compile repair started")) {
                      repairItems.push({
                        id: act.id,
                        message: "Compile repair",
                        detail: msg.replace("Compile repair started", "").trim() || undefined,
                        status: "running",
                      })
                    }

                    // 7. Verification: Build & Test
                    if (act.stage === "Build") {
                      if (act.status === "Started") {
                        buildState = "running"
                        buildLabel = t("execWs.build")
                      } else if (act.status === "Completed") {
                        buildState = "passed"
                        buildLabel = t("execWs.build")
                        if (act.metadata?.stageDurationMs) {
                          buildDuration = `${Math.round(act.metadata.stageDurationMs / 1000)}${t("shared.units.s")}`
                        }
                      } else if (act.status === "Failed") {
                        buildState = "failed"
                        buildLabel = t("execWs.build")
                      }
                    }

                    if (act.stage === "Test") {
                      if (act.status === "Started") {
                        testState = "running"
                        testLabel = t("execWs.tests")
                      } else if (act.status === "Completed") {
                        testState = "passed"
                        testLabel = t("execWs.tests")
                        if (act.metadata?.stageDurationMs) {
                          testDuration = `${Math.round(act.metadata.stageDurationMs / 1000)}${t("shared.units.s")}`
                        }
                      } else if (act.status === "Failed") {
                        testState = "failed"
                        testLabel = t("execWs.tests")
                      }
                    }
                  }

                  const allFilesList = Array.from(fileMap.values())
                  const runningFiles = allFilesList.filter(f => (f.status === "running" || f.status === "retrying" || f.status === "repairing") && isRunning)
                  const completedFiles = allFilesList.filter(f => f.status === "completed" || (!isRunning && f.status !== "failed"))
                  const totalFiles = Math.max(totalPlannedFiles, allFilesList.length)
                  const completedCount = Math.min(completedFiles.length, totalFiles > 0 ? totalFiles : completedFiles.length)
                  const percent = totalFiles > 0 ? Math.min(100, Math.round((completedCount / totalFiles) * 100)) : 0
                  const isGenComplete = completedCount === totalFiles && totalFiles > 0

                  const startedAtMs = execution.startedAt ? new Date(execution.startedAt).getTime() : 0
                  const completedAtMs = execution.completedAt ? new Date(execution.completedAt).getTime() : nowMs
                  const elapsedSec = startedAtMs > 0 ? Math.max(1, Math.floor(((execution.completedAt ? completedAtMs : nowMs) - startedAtMs) / 1000)) : undefined
                  const uS = t("shared.units.s")
                  const uM = t("shared.units.m")
                  const elapsedFormatted = elapsedSec !== undefined ? (elapsedSec >= 60 ? `${Math.floor(elapsedSec / 60)}${uM} ${elapsedSec % 60}${uS}` : `${elapsedSec}${uS}`) : `0${uS}`

                  const nextStageHint = !isGenComplete
                    ? t("execWs.hintApply")
                    : buildState === "idle" || buildState === "running"
                      ? t("execWs.hintBuild")
                      : testState === "idle" || testState === "running"
                        ? t("execWs.hintTests")
                        : t("execWs.hintReview")

                  return (
                    <>
                      {/* PRIMARY STRUCTURED TECHNICAL EXECUTION VIEWER */}
                      <div className="rounded-[var(--radius-lg)] border border-border bg-surface p-4 shadow-sm space-y-4">
                        {/* Generation Header & Progress */}
                        <div>
                          <div className="flex items-center justify-between gap-2">
                            <div className="flex items-center gap-2">
                              <Cpu className="h-4 w-4 text-primary shrink-0" />
                              <span className="text-[13.5px] font-semibold text-foreground">
                                {isGenComplete
                                  ? t("execWs.changesGenerated", { done: completedCount, total: totalFiles, elapsed: elapsedFormatted })
                                  : t("execWs.generating", { done: completedCount, total: totalFiles || "…", elapsed: elapsedFormatted })}
                              </span>
                            </div>
                            <span className="font-mono text-[11.5px] font-medium text-muted-foreground">
                              {percent}%
                            </span>
                          </div>

                          <div className="mt-2.5 h-1.5 w-full overflow-hidden rounded-full bg-surface-3">
                            <div
                              className={cn(
                                "h-full transition-all duration-300",
                                isGenComplete ? "bg-success" : "bg-primary motion-reduce:animate-none animate-pulse"
                              )}
                              style={{ width: `${Math.max(5, percent)}%` }}
                            />
                          </div>

                          <div className="mt-2 flex items-center justify-between text-[11px] text-subtle-foreground font-mono">
                            <span>{t("execWs.stageLine", { stage: isGenComplete ? (buildState === "running" || testState === "running" ? t("execWs.stageVerification") : t("execWs.stageComplete")) : t("execWs.stageAgent") })}</span>
                            <span>{t("execWs.nextLine", { hint: nextStageHint })}</span>
                          </div>
                        </div>

                        {/* Currently Running Files */}
                        {runningFiles.length > 0 && (
                          <div className="rounded-[var(--radius-md)] border border-primary/20 bg-primary-soft/30 p-3">
                            <div className="tech-label text-[10.5px] text-primary mb-2 flex items-center gap-1.5">
                              <CircleDot className="h-3 w-3 animate-pulse-dot text-primary motion-reduce:animate-none" />
                              {t("execWs.activeFile", { n: runningFiles.length })}
                            </div>
                            <div className="space-y-2">
                              {runningFiles.map((rf) => {
                                const liveSeconds = Math.max(1, Math.floor((nowMs - rf.startedAtMs) / 1000))
                                return (
                                  <div
                                    key={rf.fileName}
                                    className="flex flex-col gap-1 text-[12px] min-w-0"
                                  >
                                    <div className="flex items-center justify-between min-w-0">
                                      <div className="flex items-center gap-2 min-w-0">
                                        <span className="h-1.5 w-1.5 rounded-full bg-primary motion-reduce:animate-none animate-ping" />
                                        <span className="font-mono font-medium text-foreground truncate" title={rf.fileName}>
                                          {rf.fileName}
                                        </span>
                                      </div>
                                      <span className="font-mono text-[11px] text-primary shrink-0 ml-2">
                                        {liveSeconds}{uS}
                                      </span>
                                    </div>
                                    {rf.badge && (
                                      <div className="pl-3.5">
                                        <Badge tone="amber" className="text-[9.5px] px-1.5 py-0.5 font-mono">
                                          {rf.badge}
                                        </Badge>
                                      </div>
                                    )}
                                  </div>
                                )
                              })}
                            </div>
                          </div>
                        )}

                        {/* Completed Files */}
                        {completedFiles.length > 0 && (
                          <div className="space-y-2">
                            <div className="tech-label text-[10.5px] text-subtle-foreground flex items-center justify-between">
                              <span>{t("execWs.completedFiles", { n: completedFiles.length })}</span>
                            </div>
                            <div className="max-h-48 overflow-y-auto space-y-1 pr-1">
                              {completedFiles.map((cf) => (
                                <div
                                  key={cf.fileName}
                                  className="flex items-center justify-between rounded-[var(--radius-sm)] border border-border/40 bg-surface-2/60 px-2.5 py-1.5 text-[12px]"
                                >
                                  <div className="flex items-center gap-2 min-w-0">
                                    <Check className="h-3.5 w-3.5 text-success shrink-0" />
                                    <span className="font-mono text-foreground truncate" title={cf.fileName}>
                                      {cf.fileName}
                                    </span>
                                    {cf.badge && (
                                      <Badge tone="neutral" className="text-[9.5px] px-1 py-0 font-mono text-subtle-foreground">
                                        {cf.badge}
                                      </Badge>
                                    )}
                                  </div>
                                  {cf.durationSec !== undefined && (
                                    <span className="font-mono text-[10.5px] text-subtle-foreground shrink-0 ml-2">
                                      {cf.durationSec}{uS}
                                    </span>
                                  )}
                                </div>
                              ))}
                            </div>
                          </div>
                        )}

                        {/* Repair Section */}
                        {repairItems.length > 0 && (
                          <div className="rounded-[var(--radius-md)] border border-accent-line/40 bg-accent-soft/30 p-3 space-y-1.5">
                            <div className="tech-label text-[10.5px] text-accent flex items-center gap-1.5">
                              <RotateCcw className="h-3 w-3" />
                              {t("execWs.repairConvergence")}
                            </div>
                            <div className="space-y-1">
                              {repairItems.map((item) => (
                                <div
                                  key={item.id}
                                  className="flex items-center justify-between text-[12px] font-mono text-foreground"
                                >
                                  <div className="flex items-center gap-1.5 truncate">
                                    <span className="text-accent">↻</span>
                                    <span className="truncate">{item.message}</span>
                                    {item.detail && (
                                      <span className="text-muted-foreground text-[11px] truncate">({item.detail})</span>
                                    )}
                                  </div>
                                  <Badge tone={item.status === "completed" ? "green" : item.status === "failed" ? "red" : "amber"} className="text-[10px] px-1 py-0">
                                    {t(`execWs.repairStatus.${item.status}`)}
                                  </Badge>
                                </div>
                              ))}
                            </div>
                          </div>
                        )}

                        {/* Verification Repository Checks Section */}
                        <div className="border-t border-border/50 pt-3 space-y-2">
                          <div className="tech-label text-[10.5px]">{t("execWs.repoChecksTitle")}</div>
                          <div className="grid grid-cols-2 gap-2 text-[12px]">
                            {/* Build check */}
                            <div className="flex items-center justify-between rounded-[var(--radius-sm)] border border-border bg-surface-2 px-2.5 py-1.5">
                              <div className="flex items-center gap-1.5 min-w-0">
                                {buildState === "passed" ? (
                                  <Check className="h-3.5 w-3.5 text-success shrink-0" />
                                ) : buildState === "failed" ? (
                                  <X className="h-3.5 w-3.5 text-danger shrink-0" />
                                ) : buildState === "running" ? (
                                  <CircleDot className="h-3.5 w-3.5 text-primary animate-pulse-dot motion-reduce:animate-none shrink-0" />
                                ) : (
                                  <span className="h-1.5 w-1.5 rounded-full bg-subtle-foreground shrink-0" />
                                )}
                                <span className="font-medium text-foreground truncate">{buildLabel}</span>
                              </div>
                              <div className="flex items-center gap-1.5 shrink-0 ml-1">
                                {buildDuration && (
                                  <span className="font-mono text-[10.5px] text-subtle-foreground">{buildDuration}</span>
                                )}
                                <Badge tone={buildState === "passed" ? "green" : buildState === "failed" ? "red" : buildState === "running" ? "blue" : "neutral"} className="text-[9.5px] px-1 py-0 font-mono">
                                  {buildState === "passed" ? t("execWs.passed") : buildState === "failed" ? t("execWs.failed") : buildState === "running" ? t("execWs.running") : t("execWs.waitingBadge")}
                                </Badge>
                              </div>
                            </div>

                            {/* Test check */}
                            <div className="flex items-center justify-between rounded-[var(--radius-sm)] border border-border bg-surface-2 px-2.5 py-1.5">
                              <div className="flex items-center gap-1.5 min-w-0">
                                {testState === "passed" ? (
                                  <Check className="h-3.5 w-3.5 text-success shrink-0" />
                                ) : testState === "failed" ? (
                                  <X className="h-3.5 w-3.5 text-danger shrink-0" />
                                ) : testState === "running" ? (
                                  <CircleDot className="h-3.5 w-3.5 text-primary animate-pulse-dot motion-reduce:animate-none shrink-0" />
                                ) : (
                                  <span className="h-1.5 w-1.5 rounded-full bg-subtle-foreground shrink-0" />
                                )}
                                <span className="font-medium text-foreground truncate">{testLabel}</span>
                              </div>
                              <div className="flex items-center gap-1.5 shrink-0 ml-1">
                                {testDuration && (
                                  <span className="font-mono text-[10.5px] text-subtle-foreground">{testDuration}</span>
                                )}
                                <Badge tone={testState === "passed" ? "green" : testState === "failed" ? "red" : testState === "running" ? "blue" : "neutral"} className="text-[9.5px] px-1 py-0 font-mono">
                                  {testState === "passed" ? t("execWs.passed") : testState === "failed" ? t("execWs.failed") : testState === "running" ? t("execWs.running") : t("execWs.waitingBadge")}
                                </Badge>
                              </div>
                            </div>
                          </div>
                        </div>
                      </div>

                      {/* SECONDARY DISCLOSURE: RAW TECHNICAL LOG */}
                      <div className="rounded-[var(--radius-md)] border border-border bg-surface overflow-hidden">
                        <button
                          type="button"
                          onClick={() => setShowGenDetails(!showGenDetails)}
                          className="flex w-full items-center justify-between px-4 py-2.5 text-left text-[12.5px] font-medium text-muted-foreground hover:bg-surface-3 transition-colors"
                        >
                          <div className="flex items-center gap-2">
                            <Terminal className="h-3.5 w-3.5 text-subtle-foreground" />
                            <span>{t("execWs.rawLog")}</span>
                            <span className="font-mono text-[11px] text-subtle-foreground">
                              ({t("execWs.events", { count: activities.length })})
                            </span>
                          </div>
                          {showGenDetails ? (
                            <ChevronUp className="h-4 w-4 text-subtle-foreground" />
                          ) : (
                            <ChevronDown className="h-4 w-4 text-subtle-foreground" />
                          )}
                        </button>

                        {showGenDetails && (
                          <div className="max-h-80 overflow-y-auto border-t border-border p-3 space-y-2 divide-y divide-border/30">
                            {activities.map((act) => {
                              const isDone = act.status === "Completed"
                              const isFailedStatus = act.status === "Failed"
                              const isRejectedStatus = act.status === "Rejected"
                              const formattedTime = formatTimeOnly(act.createdAt)
                              const metaText = getMetadataDisplay(act, execution.verificationOutcome)

                              return (
                                <div
                                  key={act.id}
                                  className="pt-2 first:pt-0 flex items-start gap-2.5 text-[11.5px]"
                                >
                                  <div className="mt-0.5 shrink-0">
                                    {isDone ? (
                                      <Check className="h-3 w-3 text-success" />
                                    ) : isFailedStatus || isRejectedStatus ? (
                                      <X className="h-3 w-3 text-danger" />
                                    ) : (
                                      <CircleDot className="h-3 w-3 text-primary animate-pulse-dot motion-reduce:animate-none" />
                                    )}
                                  </div>
                                  <div className="min-w-0 flex-1 font-mono">
                                    <div className="flex items-center justify-between gap-2">
                                      <span className="text-foreground truncate">{srv(act.message)}</span>
                                      <span className="text-[10px] text-subtle-foreground shrink-0">{formattedTime}</span>
                                    </div>
                                    {metaText && (
                                      <div className="text-[10.5px] text-muted-foreground mt-0.5">
                                        {metaText}
                                      </div>
                                    )}
                                  </div>
                                </div>
                              )
                            })}
                          </div>
                        )}
                      </div>
                    </>
                  )
                })()}
              </div>
            )}
          </div>
        </section>

        {/* RIGHT — run telemetry */}
        <aside className="p-5 lg:border-l lg:border-border">
          <div className="tech-label mb-3">{t("execWs.telemetry")}</div>
          <div className="space-y-3">
            {execution.verdict && <VerdictCard verdict={execution.verdict} />}
            {execution.usage && <UsagePanel usage={execution.usage} />}
            <Panel className="p-3.5 space-y-2 font-mono text-[11px]">
              <div className="flex items-center justify-between text-subtle-foreground">
                <span>{t("execWs.created")}</span>
                <span className="text-foreground">{formatDateTime(execution.createdAt)}</span>
              </div>
              <div className="flex items-center justify-between text-subtle-foreground">
                <span>{t("execWs.started")}</span>
                <span className="text-foreground">{execution.startedAt ? formatDateTime(execution.startedAt) : "—"}</span>
              </div>
              <div className="flex items-center justify-between text-subtle-foreground">
                <span>{t("execWs.completed")}</span>
                <span className="text-foreground">{execution.completedAt ? formatDateTime(execution.completedAt) : "—"}</span>
              </div>
              {execution.commitStatus === "Committed" && (
                <div className="flex items-center justify-between border-t border-border/40 pt-2 text-subtle-foreground">
                  <span>{t("execWs.localCommit")}</span>
                  <span className="text-success font-semibold">{execution.commitSha?.slice(0, 7) ?? t("execWs.committed")}</span>
                </div>
              )}
              {execution.pushStatus === "Pushed" && (
                <div className="flex items-center justify-between border-t border-border/40 pt-2 text-subtle-foreground">
                  <span>{t("execWs.remotePush")}</span>
                  <span className="text-success font-semibold">{execution.remoteCommitSha?.slice(0, 7) ?? t("execWs.pushed")}</span>
                </div>
              )}
              {execution.pullRequestStatus === "Open" && (
                <>
                  <div className="flex items-center justify-between border-t border-border/40 pt-2 text-subtle-foreground">
                    <span>{t("execWs.pullRequest")}</span>
                    {execution.pullRequestUrl ? (
                      <a href={execution.pullRequestUrl} target="_blank" rel="noreferrer" className="text-success font-semibold hover:underline">
                        #{execution.pullRequestNumber} ({execution.pullRequestRemoteState ?? "Open"}) &rarr;
                      </a>
                    ) : (
                      <span className="text-success font-semibold">#{execution.pullRequestNumber} ({execution.pullRequestRemoteState ?? "Open"})</span>
                    )}
                  </div>
                  {execution.ciStatus && (
                    <div className="flex items-center justify-between border-t border-border/40 pt-2 text-subtle-foreground">
                      <span>{t("execWs.ciStatus")}</span>
                      <span className={cn(
                        "font-semibold",
                        execution.ciStatus === "Success" ? "text-success" : execution.ciStatus === "Failure" ? "text-danger" : "text-amber-500"
                      )}>
                        {execution.ciStatus}
                      </span>
                    </div>
                  )}
                  {execution.mergeStatus === "Merged" && (
                    <div className="flex items-center justify-between border-t border-border/40 pt-2 text-subtle-foreground">
                      <span>{t("execWs.mergeStatus")}</span>
                      <span className="text-emerald-400 font-semibold truncate">
                        {t("execWs.mergedConfirmed", { sha: execution.mergeCommitSha?.slice(0, 7) ?? t("execWs.confirmed") })}
                      </span>
                    </div>
                  )}
                </>
              )}
            </Panel>

            <Panel className="p-3.5">
              <div className="flex items-center justify-between">
                <span className="tech-label">{t("execWs.model")}</span>
                <span className="font-mono text-[11px] text-muted-foreground">{execution.model || t("execWs.notRecorded")}</span>
              </div>
            </Panel>
          </div>

          <div className="tech-label mb-2 mt-5">{t("execWs.repoChecks")}</div>
          <Panel className="p-3.5">
            <div className="flex items-center gap-2 text-[12.5px]">
              <Hammer className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="text-foreground">{t("execWs.prerequisites")}</span>
              <Badge
                tone={buildPassed ? "green" : buildFailed ? "red" : "neutral"}
                className="ml-auto"
              >
                {buildPassed ? t("execWs.passed") : buildFailed ? t("execWs.failed") : "—"}
              </Badge>
            </div>
            <div className="mt-1.5 flex items-center gap-2 text-[12.5px]">
              <FlaskConical className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="text-foreground">{t("execWs.tests")}</span>
              <Badge
                tone={testPassed ? "green" : testFailed ? "red" : "neutral"}
                className="ml-auto"
              >
                {testPassed
                  ? t("execWs.passed")
                  : testFailed
                    ? t("execWs.failed")
                    : execution.verificationOutcome === "PartiallyVerified"
                      ? t("execWs.noSuite")
                      : "—"}
              </Badge>
            </div>
          </Panel>

          <Button
            variant="default"
            size="md"
            className="mt-4 w-full"
            onClick={() => navigate(`/tasks/${execution.developmentTaskId}`)}
          >
            <FileText className="h-3.5 w-3.5" />
            {t("execWs.openTask")}
          </Button>
        </aside>
      </div>
    </div>
  )
}
