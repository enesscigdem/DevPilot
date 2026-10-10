import { useCallback, useEffect, useRef, useState } from "react"
import { Link, useNavigate, useParams } from "react-router-dom"
import { useTranslation } from "react-i18next"
import i18n from "@/i18n"
import {
  ArrowLeft,
  Check,
  ChevronRight,
  FileCode2,
  Play,
  Pencil,
  Sparkles,
  ShieldCheck,
  Database,
  Network,
  FlaskConical,
  Plus,
  Loader2,
  AlertCircle,
  AlertTriangle,
  BrainCircuit,
  RotateCcw,
  Layers,
  Cpu,
  FileCheck,
} from "lucide-react"
import { PageContainer } from "@/components/shared"
import { Button, Panel, Badge, Meter, StatusDot, IconChip } from "@/components/ui/primitives"
import { FormattedText } from "@/components/FormattedText"
import { CompareModelsPanel } from "@/components/CompareModelsPanel"
import { getTask, getTaskImpactAnalysis, analyzeTaskImpact, approveTask, rejectTask, startExecution, retryExecution, getExecutions } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import { deriveTaskImpactActionState, deriveTaskImpactLifecycle } from "@/lib/taskImpactState"
import {
  TaskStatus,
  TaskPriority,
  ImpactAnalysisStatus,
  TaskExecutionStatus,
  type Task,
  type ImpactAnalysis,
  type ImpactedFile,
  type ExecutionListItem,
  type Tone,
} from "@/types"

function getPriorityToneAndLabel(priority: number): { tone: Tone; label: string } {
  switch (priority) {
    case TaskPriority.Low:
      return { tone: "green", label: i18n.t("impact.level.low") }
    case TaskPriority.Medium:
      return { tone: "amber", label: i18n.t("impact.level.medium") }
    case TaskPriority.High:
    case TaskPriority.Critical:
      return { tone: "red", label: i18n.t("impact.level.high") }
    default:
      return { tone: "neutral", label: i18n.t("impact.level.normal") }
  }
}

function getImpactLevelTone(level: string): Tone {
  switch (level?.toLowerCase()) {
    case "low":
      return "green"
    case "medium":
      return "amber"
    case "high":
    case "critical":
      return "red"
    default:
      return "neutral"
  }
}

const levelText = (level?: string | null) => {
  const k = String(level ?? "").toLowerCase()
  if (k === "critical") return i18n.t("impact.level.high")
  return ["low", "medium", "high"].includes(k) ? i18n.t(`impact.level.${k}`) : (level ?? "")
}

export function TaskImpact() {
  const { t } = useTranslation()
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const { activeWorkspaceId, refreshOverview } = useWorkspace()

  const [task, setTask] = useState<Task | null>(null)
  const [analysis, setAnalysis] = useState<ImpactAnalysis | null>(null)
  const [activeExecution, setActiveExecution] = useState<ExecutionListItem | null>(null)

  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [isAnalyzing, setIsAnalyzing] = useState(false)
  const [analysisError, setAnalysisError] = useState<string | null>(null)

  const [selectedFileIndex, setSelectedFileIndex] = useState(0)

  const [isApproving, setIsApproving] = useState(false)
  const [acknowledgedRisks, setAcknowledgedRisks] = useState(false)
  const [isRejecting, setIsRejecting] = useState(false)
  const [approvalError, setApprovalError] = useState<string | null>(null)

  const [isStartingExecution, setIsStartingExecution] = useState(false)
  const [startExecutionError, setStartExecutionError] = useState<string | null>(null)

  const [isRetryingExecution, setIsRetryingExecution] = useState(false)
  const [retryExecutionError, setRetryExecutionError] = useState<string | null>(null)

  // 1-second ticker for live elapsed time updates
  const [nowMs, setNowMs] = useState<number>(Date.now())
  useEffect(() => {
    const ticker = setInterval(() => setNowMs(Date.now()), 1000)
    return () => clearInterval(ticker)
  }, [])

  const lifecycleState = deriveTaskImpactLifecycle(
    task,
    analysis,
    activeExecution,
    nowMs,
  )

  const actionState = deriveTaskImpactActionState(
    task?.status,
    activeExecution,
  )

  const handleStartExecution = async () => {
    if (!id || isStartingExecution) return
    setIsStartingExecution(true)
    setStartExecutionError(null)

    try {
      const execution = await startExecution(id)
      refreshOverview(true)
      navigate(`/executions/${execution.id}`)
    } catch (err) {
      setStartExecutionError(err instanceof Error ? err.message : t("impact.errStart"))
    } finally {
      setIsStartingExecution(false)
    }
  }

  const handleRetryExecution = async () => {
    if (!id || isRetryingExecution) return
    setIsRetryingExecution(true)
    setRetryExecutionError(null)

    try {
      const execution = await retryExecution(id)
      refreshOverview(true)
      navigate(`/executions/${execution.id}`)
    } catch (err) {
      setRetryExecutionError(err instanceof Error ? err.message : t("impact.errRetry"))
    } finally {
      setIsRetryingExecution(false)
    }
  }

  const loadData = useCallback(async () => {
    if (!id) {
      setIsLoading(false)
      return
    }

    setIsLoading(true)
    setError(null)

    try {
      // 1. Fetch task details & active executions in parallel
      const [loadedTask, execs] = await Promise.all([
        getTask(id),
        getExecutions(activeWorkspaceId),
      ])
      setTask(loadedTask)

      // Find active execution for this task (Pending or Running)
      const active = execs.find(
        (e) =>
          e.developmentTaskId === id &&
          (e.status === TaskExecutionStatus.Pending || e.status === TaskExecutionStatus.Running),
      )
      setActiveExecution(active ?? null)

      // 2. Fetch impact analysis. Only a 404 means "no analysis yet" (the api returns null for it); any other
      //    failure is shown as an error rather than passing for an empty state.
      setAnalysis(await getTaskImpactAnalysis(id))
    } catch (err) {
      setError(err instanceof Error ? err.message : t("impact.errNotFound"))
    } finally {
      setIsLoading(false)
    }
  }, [id, activeWorkspaceId])

  useEffect(() => {
    loadData()
  }, [loadData])

  // Dedicated scoped polling while analysis is in progress
  const isPollingAnalysisRef = useRef(false)
  useEffect(() => {
    if (!lifecycleState.isAnalyzing || !id) return

    const interval = setInterval(async () => {
      if (isPollingAnalysisRef.current) return
      isPollingAnalysisRef.current = true
      try {
        const [updatedTask, updatedAnalysis] = await Promise.all([
          getTask(id).catch(() => null),
          getTaskImpactAnalysis(id).catch(() => null),
        ])
        if (updatedTask) setTask(updatedTask)
        if (updatedAnalysis) setAnalysis(updatedAnalysis)
      } catch {
        // ignore polling network errors
      } finally {
        isPollingAnalysisRef.current = false
      }
    }, 2500)

    return () => {
      clearInterval(interval)
    }
  }, [id, lifecycleState.isAnalyzing])

  // Scoped polling while task has an actual active execution OR task.status claims Executing
  const isPollingExecRef = useRef(false)
  useEffect(() => {
    const isExecutingOrSyncing =
      activeExecution != null || task?.status === TaskStatus.Executing

    if (!isExecutingOrSyncing || !id) return

    const interval = setInterval(async () => {
      if (isPollingExecRef.current) return
      isPollingExecRef.current = true
      try {
        const [updatedTask, execs] = await Promise.all([
          getTask(id).catch(() => null),
          getExecutions(activeWorkspaceId).catch(() => null),
        ])
        if (updatedTask) setTask(updatedTask)
        // A failed fetch keeps what is shown; it must not read as "the execution ended".
        if (execs) {
          const active = execs.find(
            (e) =>
              e.developmentTaskId === id &&
              (e.status === TaskExecutionStatus.Pending || e.status === TaskExecutionStatus.Running),
          )
          setActiveExecution(active ?? null)
        }
      } catch {
        // ignore polling failures
      } finally {
        isPollingExecRef.current = false
      }
    }, 3500)

    return () => clearInterval(interval)
  }, [id, activeWorkspaceId, activeExecution, task?.status])

  const handleStartAnalysis = async () => {
    if (!id || isAnalyzing) return
    setIsAnalyzing(true)
    setAnalysisError(null)

    // Launch server-side impact analysis
    const analysisPromise = analyzeTaskImpact(id)

    // Bounded authoritative hydration: poll immediately and deterministically until InProgress is observed
    const hydrateAnalyzingState = async () => {
      for (let attempt = 0; attempt < 12; attempt++) {
        try {
          const [updatedTask, updatedAnalysis] = await Promise.all([
            getTask(id).catch(() => null),
            getTaskImpactAnalysis(id).catch(() => null),
          ])
          const isTaskAnalyzing = updatedTask?.status === TaskStatus.Analyzing
          const isAnalysisInProgress = updatedAnalysis?.status === ImpactAnalysisStatus.InProgress

          if (isTaskAnalyzing || isAnalysisInProgress) {
            if (updatedTask) setTask(updatedTask)
            if (updatedAnalysis) setAnalysis(updatedAnalysis)
            break
          }
        } catch {
          // ignore transient hydration error
        }
        await new Promise((r) => setTimeout(r, 20))
      }
    }
    void hydrateAnalyzingState()

    try {
      const result = await analysisPromise
      setAnalysis(result)

      // Re-fetch updated task so task status in header refreshes immediately
      try {
        const updatedTask = await getTask(id)
        setTask(updatedTask)
      } catch (err) {
        console.error("Failed to refresh task state after analysis", err)
      }
    } catch (err) {
      setAnalysisError(err instanceof Error ? err.message : t("impact.errAnalysis"))
      // Re-fetch authoritative terminal state (Completed or Failed)
      try {
        const [updatedTask, updatedAnalysis] = await Promise.all([
          getTask(id).catch(() => null),
          getTaskImpactAnalysis(id).catch(() => null),
        ])
        if (updatedTask) setTask(updatedTask)
        if (updatedAnalysis) setAnalysis(updatedAnalysis)
      } catch {
        // ignore
      }
    } finally {
      setIsAnalyzing(false)
    }
  }

  const handleApprove = async () => {
    if (!id || isApproving || isRejecting) return
    setIsApproving(true)
    setApprovalError(null)
    try {
      await approveTask(id)
      const updatedTask = await getTask(id)
      setTask(updatedTask)
    } catch (err) {
      setApprovalError(err instanceof Error ? err.message : t("impact.errApprove"))
    } finally {
      setIsApproving(false)
    }
  }

  const handleReject = async () => {
    if (!id || isApproving || isRejecting) return
    setIsRejecting(true)
    setApprovalError(null)
    try {
      await rejectTask(id)
      const updatedTask = await getTask(id)
      setTask(updatedTask)
    } catch (err) {
      setApprovalError(err instanceof Error ? err.message : t("impact.errReject"))
    } finally {
      setIsRejecting(false)
    }
  }

  if (isLoading) {
    return (
      <PageContainer className="flex items-center justify-center py-24">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="h-6 w-6 animate-spin text-primary" />
          <span className="tech-label">{t("impact.loading")}</span>
        </div>
      </PageContainer>
    )
  }

  if (error || !task) {
    return (
      <PageContainer className="py-12">
        <div className="mx-auto max-w-md text-center">
          <AlertCircle className="mx-auto h-8 w-8 text-danger" />
          <h2 className="mt-3 text-[15px] font-semibold text-foreground">{t("impact.notFound")}</h2>
          <p className="mt-1 text-[13px] text-muted-foreground">{error || t("impact.notFoundDesc")}</p>
          <Button variant="default" size="md" className="mt-4" onClick={() => navigate("/tasks")}>
            <ArrowLeft className="h-4 w-4" />
            {t("impact.backToTasks")}
          </Button>
        </div>
      </PageContainer>
    )
  }

  const displayTitle = task?.title || t("impact.untitled")
  const displayId = `TASK-${task?.id.slice(0, 6).toUpperCase()}`

  const priorityInfo = task
    ? getPriorityToneAndLabel(task.priority)
    : { tone: "neutral" as Tone, label: t("impact.level.normal") }

  const structured = analysis?.structuredResult

  const hasCompletedAnalysis = lifecycleState.isSucceeded

  // Risk badge comes ONLY from real persisted impact analysis data
  let analysisRiskInfo: { tone: Tone; label: string } | null = null
  if (hasCompletedAnalysis && structured?.risks && structured.risks.length > 0) {
    const levels = structured.risks.map((r) => r.level?.toLowerCase())
    let topLevel = "low"
    if (levels.includes("critical") || levels.includes("high")) topLevel = "high"
    else if (levels.includes("medium")) topLevel = "medium"

    analysisRiskInfo = {
      tone: getImpactLevelTone(topLevel),
      label: topLevel === "high" ? t("impact.riskLabel.high") : topLevel === "medium" ? t("impact.riskLabel.medium") : t("impact.riskLabel.low"),
    }
  }

  const confidence = structured?.confidence ?? analysis?.confidence ?? null

  const requirementText = task?.description || t("impact.noDescription")

  const acceptanceList =
    task?.acceptanceCriteria && task.acceptanceCriteria.trim().length > 0
      ? task.acceptanceCriteria.split("\n").filter((line) => line.trim().length > 0)
      : []

  const planSteps =
    structured?.proposedPlan && structured.proposedPlan.length > 0
      ? structured.proposedPlan.map((s) => ({
          title: s.title,
          detail: s.description,
          files: s.relatedFiles || [],
        }))
      : []

  const realFiles: ImpactedFile[] = structured?.impactedFiles || []

  // Approval checklist: the facts a developer should have seen before approving, derived from the plan itself.
  const uncertainFileCount = realFiles.filter((f) => f.isUncertain).length
  const unknownCount = structured?.unknowns?.length ?? 0
  const staleBase = structured?.baseSnapshot?.isStale ?? false
  const expectedCheckCount = structured?.changeBrief?.expectedChecks?.length ?? 0
  const requiresAcknowledgement = uncertainFileCount > 0 || staleBase

  const selectedFile =
    hasCompletedAnalysis && realFiles.length > 0
      ? realFiles[selectedFileIndex] || realFiles[0]
      : null

  return (
    <PageContainer className="max-w-none px-0 py-0 overflow-hidden">
      {/* Sticky task header */}
      <div className="sticky top-0 z-10 border-b border-border bg-canvas/85 backdrop-blur-sm">
        <div className="mx-auto flex max-w-[1600px] items-center gap-3 px-6 py-3 min-w-0">
          <Link
            to="/tasks"
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-[var(--radius-md)] text-muted-foreground hover:bg-surface-3 hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 min-w-0">
              <span className="font-mono text-[11px] text-subtle-foreground shrink-0">{displayId}</span>
              <h1 className="truncate text-[15px] font-semibold text-foreground" title={displayTitle}>
                {displayTitle}
              </h1>
            </div>
          </div>
          <Badge tone={lifecycleState.statusTone} className="shrink-0">
            {lifecycleState.isAnalyzing && (
              <span className="relative flex h-2 w-2 mr-1.5">
                <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-primary opacity-75"></span>
                <span className="relative inline-flex rounded-full h-2 w-2 bg-primary"></span>
              </span>
            )}
            {lifecycleState.statusLabel}
          </Badge>
          {lifecycleState.durationFormatted && (
            <Badge tone="neutral" className="shrink-0 font-mono text-[11px]">
              {lifecycleState.isSucceeded
                ? t("impact.completedIn", { time: lifecycleState.durationFormatted })
                : t("impact.failedAfter", { time: lifecycleState.durationFormatted })}
            </Badge>
          )}
          <Badge tone={priorityInfo.tone} className="shrink-0">{t("impact.priority", { level: priorityInfo.label })}</Badge>
          {analysisRiskInfo && (
            <Badge tone={analysisRiskInfo.tone} className="shrink-0">{analysisRiskInfo.label}</Badge>
          )}
          {hasCompletedAnalysis && confidence !== null && (
            <div className="hidden items-center gap-1.5 md:flex shrink-0">
              <span className="tech-label">{t("impact.confidence")}</span>
              <span className="font-mono text-[13px] font-semibold text-foreground">{confidence}%</span>
            </div>
          )}
        </div>
      </div>

      {/* Three-pane analysis */}
      <div className="mx-auto grid max-w-[1600px] grid-cols-1 gap-0 lg:grid-cols-[340px_minmax(0,1fr)_380px]">
        {/* LEFT — Requirement + plan */}
        <aside className="border-b border-border p-5 lg:border-b-0 lg:border-r min-w-0 overflow-hidden max-h-[calc(100vh-140px)] min-h-0 overflow-y-auto pr-3">
          <div className="sticky top-0 bg-canvas/95 backdrop-blur-sm z-10 pb-2 mb-2 border-b border-border/40 flex items-center justify-between">
            <span className="tech-label">{t("impact.requirement")}</span>
          </div>
          <div className="text-[13px] leading-relaxed text-foreground min-w-0">
            <FormattedText text={requirementText} />
          </div>

          {acceptanceList.length > 0 && (
            <>
              <div className="tech-label mb-2 mt-6">{t("impact.acceptance")}</div>
              <ul className="space-y-2 min-w-0">
                {acceptanceList.map((a, i) => (
                  <li key={i} className="flex gap-2 text-[12.5px] leading-relaxed text-muted-foreground min-w-0">
                    <Check className="mt-0.5 h-3.5 w-3.5 shrink-0 text-success" />
                    <span className="min-w-0 break-words">{a}</span>
                  </li>
                ))}
              </ul>
            </>
          )}

          {planSteps.length > 0 && (
            <>
              <div className="tech-label mb-3 mt-6 flex items-center gap-1.5">
                <Sparkles className="h-3 w-3 shrink-0" />
                {t("impact.plan")}
              </div>
              <ol className="relative space-y-0 border-l border-border pl-0 min-w-0">
                {planSteps.map((step, i) => (
                  <li key={i} className="relative pb-4 pl-5 last:pb-0 min-w-0">
                    <span className="absolute -left-[6.5px] top-1 flex h-3 w-3 items-center justify-center rounded-full border border-primary-ring bg-surface">
                      <span className="h-1.5 w-1.5 rounded-full bg-primary" />
                    </span>
                    <div className="text-[12.5px] font-semibold text-foreground break-words">{step.title}</div>
                    <p className="mt-0.5 text-[12px] leading-relaxed text-muted-foreground break-words whitespace-pre-wrap">
                      {step.detail}
                    </p>
                    {step.files.length > 0 && (
                      <div className="mt-1.5 flex flex-wrap gap-1 min-w-0">
                        {step.files.map((f) => (
                          <span
                            key={f}
                            className="max-w-full font-mono text-[10.5px] text-subtle-foreground truncate rounded border border-border/50 bg-surface-2 px-1 py-0.5"
                            title={f}
                          >
                            {f}
                          </span>
                        ))}
                      </div>
                    )}
                  </li>
                ))}
              </ol>
            </>
          )}
        </aside>

        {/* CENTER — Affected files + inspector / Analyzing / Failed states */}
        <section className="border-b border-border p-5 lg:border-b-0 min-w-0 overflow-hidden">
          {lifecycleState.lifecycle === "analyzing" ? (
            <div className="flex flex-col items-center justify-center rounded-[var(--radius-lg)] border border-border bg-surface-2/40 px-6 py-16 text-center">
              <Loader2 className="h-8 w-8 text-primary animate-spin" />
              <h3 className="mt-3 text-[14px] font-semibold text-foreground">{t("impact.analyzingTitle")}</h3>
              <p className="mt-1 max-w-md text-[12.5px] leading-relaxed text-muted-foreground">
                {t("impact.analyzingDesc")}
              </p>
              <div className="mt-4 inline-flex items-center gap-2 rounded-full border border-border bg-surface px-3 py-1 font-mono text-[12px] text-subtle-foreground">
                <span className="h-2 w-2 rounded-full bg-primary animate-pulse" />
                {t("impact.elapsed", { n: lifecycleState.elapsedSeconds })}
              </div>
            </div>
          ) : lifecycleState.lifecycle === "failed" ? (
            <div className="flex flex-col items-center justify-center rounded-[var(--radius-lg)] border border-danger/30 bg-danger-soft/20 px-6 py-12 text-center min-w-0">
              <AlertCircle className="h-9 w-9 text-danger" />
              <h3 className="mt-3 text-[14.5px] font-semibold text-foreground">{t("impact.analysisFailed")}</h3>
              {lifecycleState.durationFormatted && (
                <span className="mt-0.5 font-mono text-[11.5px] text-muted-foreground">
                  {t("impact.failedAfter", { time: lifecycleState.durationFormatted })}
                </span>
              )}
              <div className="mt-4 max-w-lg w-full text-left rounded-[var(--radius-md)] border border-danger/30 bg-surface p-3.5 text-[12px] leading-relaxed text-foreground break-words font-mono">
                {lifecycleState.sanitizedErrorMessage || analysisError || t("impact.analysisFailedShort")}
              </div>

              <Button
                variant="primary"
                size="md"
                className="mt-5 gap-2"
                disabled={!lifecycleState.canRetry || isAnalyzing}
                onClick={handleStartAnalysis}
              >
                <RotateCcw className="h-4 w-4" />
                {t("impact.retryAnalysis")}
              </Button>
            </div>
          ) : lifecycleState.lifecycle === "idle" ? (
            <div className="flex flex-col items-center justify-center rounded-[var(--radius-lg)] border border-dashed border-border px-6 py-16 text-center">
              <BrainCircuit className="h-8 w-8 text-primary opacity-80" />
              <h3 className="mt-3 text-[14px] font-semibold text-foreground">{t("impact.noAnalysis")}</h3>
              <p className="mt-1 max-w-md text-[12.5px] leading-relaxed text-muted-foreground">
                {t("impact.noAnalysisDesc")}
              </p>

              {analysisError && (
                <div className="mt-3 flex items-center gap-1.5 text-[12px] text-danger font-medium">
                  <AlertCircle className="h-3.5 w-3.5 shrink-0" />
                  <span className="break-words">{analysisError}</span>
                </div>
              )}

              <Button
                variant="primary"
                size="md"
                className="mt-5 gap-2"
                disabled={!lifecycleState.canRun || isAnalyzing}
                onClick={handleStartAnalysis}
              >
                <Sparkles className="h-4 w-4" />
                {t("impact.runAnalysis")}
              </Button>
            </div>
          ) : (
            <>
              {/* Change Brief */}
              {structured?.changeBrief && (
                <div className="mb-4 rounded-[var(--radius-lg)] border border-primary/25 bg-surface p-4 shadow-sm">
                  <div className="flex items-center justify-between gap-2 border-b border-border/40 pb-2.5 mb-3">
                    <div className="flex items-center gap-2">
                      <Sparkles className="h-4 w-4 text-primary" />
                      <span className="text-[13px] font-semibold text-foreground">{t("impact.changeBrief")}</span>
                    </div>
                    <div className="flex items-center gap-1.5 font-mono text-[11px] text-muted-foreground">
                      <span className="font-semibold text-foreground">{structured.changeBrief.fileCount}</span> {t("impact.filesCount")}
                      <span>·</span>
                      <span className="font-semibold text-foreground">{structured.changeBrief.projectCount}</span> {t("impact.projectsCount")}
                      <span>·</span>
                      <Badge tone={analysisRiskInfo?.tone ?? "neutral"} className="px-1.5 py-0 text-[10.5px]">
                        {t("impact.riskWord", { level: levelText(structured.changeBrief.riskLevel) })}
                      </Badge>
                    </div>
                  </div>

                  <div className="grid gap-3 sm:grid-cols-2 text-[12px]">
                    <div>
                      <span className="tech-label text-[10.5px]">{t("impact.explainableRisk")}</span>
                      <ul className="mt-1 space-y-1 text-[11.5px] text-muted-foreground">
                        {structured.changeBrief.riskReasons.map((reason, idx) => (
                          <li key={idx} className="flex items-start gap-1.5">
                            <span className="text-primary mt-0.5">•</span>
                            <span className="break-words">{reason}</span>
                          </li>
                        ))}
                      </ul>
                    </div>

                    <div>
                      <span className="tech-label text-[10.5px]">{t("impact.preflight")}</span>
                      <p className="mt-1 text-[11.5px] text-muted-foreground break-words">
                        {structured.changeBrief.verificationSummary || t("impact.standardPreflight")}
                      </p>
                      {structured.changeBrief.expectedChecks && structured.changeBrief.expectedChecks.length > 0 && (
                        <div className="mt-1.5 flex flex-wrap gap-1">
                          {structured.changeBrief.expectedChecks.map((chk) => (
                            <span
                              key={chk.checkId}
                              className="rounded border border-border/60 bg-surface-2 px-1.5 py-0.5 font-mono text-[10.5px] text-foreground"
                              title={chk.discoveryEvidence || chk.source}
                            >
                              {chk.displayName}
                            </span>
                          ))}
                        </div>
                      )}
                    </div>
                  </div>
                </div>
              )}

              <div className="mb-3 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <h2 className="text-[13px] font-semibold text-foreground">{t("impact.impactedFiles")}</h2>
                  <span className="rounded-full bg-surface-3 px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                    {t("common.files", { count: realFiles.length })}
                  </span>
                </div>
              </div>

              <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,320px)] min-w-0">
                {/* file list */}
                <div className="overflow-hidden rounded-[var(--radius-lg)] border border-border min-w-0">
                  {realFiles.map((f, idx) => {
                        const isSel = idx === selectedFileIndex
                        const fileName = f.filePath.split("/").pop() || f.filePath
                        const isAdded = f.changeType === "Add"
                        return (
                          <button
                            key={f.filePath}
                            onClick={() => setSelectedFileIndex(idx)}
                            className={
                              "flex w-full items-center gap-2.5 border-b border-border px-3 py-2.5 text-left transition-colors last:border-b-0 min-w-0 " +
                              (isSel ? "bg-primary-soft/70" : "hover:bg-surface-3")
                            }
                          >
                            {isAdded ? (
                              <Plus className="h-3.5 w-3.5 shrink-0 text-success" />
                            ) : (
                              <Pencil className="h-3.5 w-3.5 shrink-0 text-accent" />
                            )}
                            <div className="min-w-0 flex-1">
                              <div className="flex items-center gap-1.5 min-w-0 flex-wrap">
                                <span
                                  className={
                                    "truncate text-[12.5px] font-medium " +
                                    (isSel ? "text-primary" : "text-foreground")
                                  }
                                >
                                  {fileName}
                                </span>
                                {f.evidenceType && (
                                  <span className="rounded bg-surface-3 px-1 py-0.2 font-mono text-[10px] text-muted-foreground">
                                    {f.evidenceType}
                                  </span>
                                )}
                                {f.isUncertain && (
                                  <span className="rounded border border-amber-500/40 bg-amber-500/10 px-1 py-0.2 font-mono text-[10px] text-amber-500">
                                    {t("impact.uncertain")}
                                  </span>
                                )}
                              </div>
                              <div className="truncate font-mono text-[10.5px] text-subtle-foreground" title={f.filePath}>
                                {f.filePath}
                              </div>
                            </div>
                            {isSel && <ChevronRight className="h-4 w-4 shrink-0 text-primary" />}
                          </button>
                        )
                      })}
                </div>

                {/* inspector */}
                {selectedFile && (
                  <Panel className="h-fit p-4 min-w-0 overflow-hidden space-y-3">
                    <div className="flex items-center gap-2 min-w-0">
                      <IconChip tone="blue" className="shrink-0">
                        <FileCode2 className="h-4 w-4" />
                      </IconChip>
                      <div className="min-w-0 flex-1">
                        <div className="truncate text-[13px] font-semibold text-foreground">
                          {selectedFile.filePath.split("/").pop() || selectedFile.filePath}
                        </div>
                        <div className="truncate font-mono text-[10.5px] text-subtle-foreground">
                          {selectedFile.filePath.split("/")[1] || selectedFile.filePath.split("/")[0]}
                        </div>
                      </div>
                    </div>

                    <div className="flex items-center gap-1.5 flex-wrap">
                      <Badge tone={selectedFile.changeType === "Add" ? "green" : "amber"}>
                        {selectedFile.changeType === "Add" ? t("impact.newFile") : selectedFile.changeType}
                      </Badge>
                      {selectedFile.evidenceType && (
                        <Badge tone="neutral" className="font-mono text-[10.5px]">
                          {selectedFile.evidenceType}
                        </Badge>
                      )}
                      {selectedFile.isUncertain && (
                        <Badge tone="amber" className="text-[10.5px]">
                          {t("impact.uncertain")}
                        </Badge>
                      )}
                    </div>

                    <div>
                      <div className="tech-label mb-1">{t("impact.whyChanges")}</div>
                      <p className="text-[12px] leading-relaxed text-muted-foreground text-pretty break-words">
                        {selectedFile.reason}
                      </p>
                    </div>

                    {selectedFile.evidenceDetails && (
                      <div className="rounded-[var(--radius-md)] border border-border/60 bg-surface-2 p-2.5">
                        <div className="tech-label text-[10px] mb-1">{t("impact.evidence")}</div>
                        <p className="text-[11.5px] leading-relaxed text-foreground font-mono break-words">
                          {selectedFile.evidenceDetails}
                        </p>
                      </div>
                    )}

                    <div>
                      <div className="flex items-center justify-between">
                        <span className="tech-label">{t("impact.confidence")}</span>
                        <span className="font-mono text-[12px] font-semibold text-foreground">
                          {selectedFile.confidence}%
                        </span>
                      </div>
                      <Meter
                        value={selectedFile.confidence}
                        tone={
                          selectedFile.confidence >= 90
                            ? "green"
                            : selectedFile.confidence >= 80
                              ? "blue"
                              : "amber"
                        }
                        className="mt-1.5"
                      />
                    </div>
                  </Panel>
                )}
              </div>
            </>
          )}
        </section>

        {/* RIGHT — System impact + Unknowns + decision */}
        <aside className="p-5 lg:border-l lg:border-border min-w-0 overflow-hidden space-y-4">
          <div>
            <div className="tech-label mb-2.5">{t("impact.systemImpact")}</div>
            <div className="space-y-2.5 min-w-0">
              {structured?.dimensions && structured.dimensions.length > 0
                  ? structured.dimensions.map((dim, i) => {
                      const tone = getImpactLevelTone(dim.impactLevel)
                      const Icon = dim.area.toUpperCase() === "API"
                        ? Network
                        : dim.area.toUpperCase() === "DATA"
                          ? Database
                          : dim.area.toUpperCase() === "TESTS"
                            ? FlaskConical
                            : dim.area.toUpperCase() === "RUNTIME"
                              ? Cpu
                              : dim.area.toUpperCase() === "DEPENDENCIES"
                                ? Layers
                                : FileCode2

                      return (
                        <div key={i} className="rounded-[var(--radius-md)] border border-border bg-surface p-3 min-w-0">
                          <div className="mb-1.5 flex items-center gap-2 min-w-0">
                            <Icon className="h-3.5 w-3.5 text-subtle-foreground shrink-0" />
                            <span className="truncate text-[12px] font-semibold text-foreground">{dim.area}</span>
                            <StatusDot tone={tone} className="ml-auto shrink-0" />
                          </div>
                          <div className="text-[12px] font-medium text-foreground break-words">{dim.summary}</div>
                          {dim.details && dim.details.length > 0 && (
                            <ul className="mt-1 space-y-0.5 text-[11.5px] text-muted-foreground">
                              {dim.details.map((d, idx) => (
                                <li key={idx} className="break-words">• {d}</li>
                              ))}
                            </ul>
                          )}
                          {dim.evidence && dim.evidence.length > 0 && (
                            <div className="mt-1.5 flex flex-wrap gap-1">
                              {dim.evidence.map((ev, idx) => (
                                <span key={idx} className="rounded bg-surface-3 px-1 py-0.2 font-mono text-[10px] text-muted-foreground truncate max-w-full">
                                  {ev}
                                </span>
                              ))}
                            </div>
                          )}
                        </div>
                      )
                    })
                  : structured?.systemImpacts && structured.systemImpacts.length > 0
                    ? structured.systemImpacts.map((si, i) => {
                        const tone = getImpactLevelTone(si.impactLevel)
                        return (
                          <div key={i} className="rounded-[var(--radius-md)] border border-border bg-surface p-3 min-w-0">
                            <div className="mb-1.5 flex items-center gap-2 min-w-0">
                              <Network className="h-3.5 w-3.5 text-subtle-foreground shrink-0" />
                              <span className="truncate text-[12px] font-semibold text-foreground">{si.area}</span>
                              <StatusDot tone={tone} className="ml-auto shrink-0" />
                            </div>
                            <div className="text-[12px] font-medium text-foreground">{t("impact.impactWord", { level: levelText(si.impactLevel) })}</div>
                            <p className="mt-0.5 text-[11.5px] leading-relaxed text-muted-foreground break-words">{si.description}</p>
                          </div>
                        )
                      })
                    : (
                        <div className="rounded-[var(--radius-md)] border border-border bg-surface p-3 text-[12px] text-muted-foreground">
                          {t("impact.noSystemImpacts")}
                        </div>
                      )}
            </div>
          </div>

          {/* Base freshness evidence captured when this analysis was produced */}
          {structured?.baseSnapshot?.isStale && (
            <div className="rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/5 p-3 min-w-0">
              <div className="mb-1 flex items-center gap-1.5 text-[12px] font-semibold text-amber-500">
                <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
                <span>{t("impact.staleBase")}</span>
              </div>
              <p className="text-[11.5px] leading-relaxed text-muted-foreground break-words">
                {structured.baseSnapshot.message ??
                  t("impact.staleBaseMsg", { n: structured.baseSnapshot.behindCount })}
                {structured.baseSnapshot.baseCommitSha && (
                  <span className="mt-1 block font-mono text-[10.5px] text-subtle-foreground">
                    {t("impact.baseSha", { sha: structured.baseSnapshot.baseCommitSha.slice(0, 7) })}
                    {structured.baseSnapshot.remoteCommitSha ? t("impact.originSha", { sha: structured.baseSnapshot.remoteCommitSha.slice(0, 7) }) : ""}
                  </span>
                )}
              </p>
            </div>
          )}

          {/* Unknowns section */}
          {structured?.unknowns && structured.unknowns.length > 0 && (
            <div className="rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/5 p-3 min-w-0">
              <div className="mb-1.5 flex items-center gap-1.5 text-[12px] font-semibold text-amber-500">
                <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
                <span>{t("impact.unknowns")}</span>
              </div>
              <ul className="space-y-1 text-[11.5px] leading-relaxed text-muted-foreground">
                {structured.unknowns.map((u, i) => (
                  <li key={i} className="flex items-start gap-1.5">
                    <span className="text-amber-500">•</span>
                    <span className="break-words">{u}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {hasCompletedAnalysis && (
            <div className="mt-6 rounded-[var(--radius-lg)] border border-primary-ring/50 bg-primary-soft/50 p-4 min-w-0">
              {actionState.kind === "active-execution" ? (
                <div className="space-y-3 min-w-0">
                  <div className="flex items-center gap-2.5 min-w-0">
                    <IconChip tone="blue" className="shrink-0">
                      <Loader2 className="h-4 w-4 animate-spin" />
                    </IconChip>
                    <div className="min-w-0 flex-1">
                      <div className="text-[13px] font-semibold text-foreground">{t("impact.inProgress")}</div>
                      <div className="text-[12px] text-muted-foreground">
                        {actionState.message}
                      </div>
                    </div>
                  </div>

                  {actionState.activeExecutionId && (
                    <Button
                      variant="primary"
                      size="lg"
                      className="w-full gap-2"
                      onClick={() => navigate(`/executions/${actionState.activeExecutionId}`)}
                    >
                      <Play className="h-4 w-4" />
                      {t("impact.viewLive")}
                    </Button>
                  )}
                </div>
              ) : actionState.kind === "syncing-execution" ? (
                <div className="space-y-3 min-w-0">
                  <div className="flex items-center gap-2.5 min-w-0">
                    <IconChip tone="blue" className="shrink-0">
                      <Loader2 className="h-4 w-4 animate-spin" />
                    </IconChip>
                    <div className="min-w-0 flex-1">
                      <div className="text-[13px] font-semibold text-foreground">{t("impact.syncingTitle")}</div>
                      <div className="text-[12px] text-muted-foreground">
                        {actionState.message}
                      </div>
                    </div>
                  </div>
                </div>
              ) : actionState.kind === "awaiting-approval" ? (
                <>
                  <div className="flex items-center gap-2 min-w-0">
                    <ShieldCheck className="h-4 w-4 text-primary shrink-0" />
                    <span className="text-[13px] font-semibold text-foreground truncate">{t("impact.readyApproval")}</span>
                  </div>
                  <p className="mt-1.5 text-[12px] leading-relaxed text-muted-foreground break-words">
                    {t("impact.approvalDesc1")}
                    {t("impact.approvalDesc2")}
                  </p>

                  {realFiles.length > 20 && (
                    <div className="mt-3 flex items-start gap-2 rounded-[var(--radius-sm)] border border-danger/30 bg-danger/10 p-2.5 text-[12px] text-danger leading-relaxed">
                      <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                      <span>
                        {t("impact.tooManyA")}<strong>{t("impact.tooManyB", { n: realFiles.length })}</strong>{t("impact.tooManyC")}
                      </span>
                    </div>
                  )}

                  {approvalError && (
                    <div className="mt-3 flex items-center gap-1.5 text-[12px] text-danger font-medium">
                      <AlertCircle className="h-3.5 w-3.5 shrink-0" />
                      <span className="break-words">{approvalError}</span>
                    </div>
                  )}

                  <div className="mt-3 space-y-1.5 rounded-[var(--radius-md)] border border-border bg-surface-2 p-3 text-[11.5px]">
                    <div className="tech-label mb-1">{t("impact.beforeApprove")}</div>
                    <div className="flex items-center justify-between gap-2 text-muted-foreground">
                      <span>{t("impact.filesInPlan")}</span>
                      <span className="font-mono text-foreground">{realFiles.length} / 20</span>
                    </div>
                    <div className="flex items-center justify-between gap-2 text-muted-foreground">
                      <span>{t("impact.uncertainFiles")}</span>
                      <span className={uncertainFileCount > 0 ? "font-mono text-amber-500" : "font-mono text-foreground"}>{uncertainFileCount}</span>
                    </div>
                    <div className="flex items-center justify-between gap-2 text-muted-foreground">
                      <span>{t("impact.openUnknowns")}</span>
                      <span className={unknownCount > 0 ? "font-mono text-amber-500" : "font-mono text-foreground"}>{unknownCount}</span>
                    </div>
                    <div className="flex items-center justify-between gap-2 text-muted-foreground">
                      <span>{t("impact.checksVerify")}</span>
                      <span className="font-mono text-foreground">{expectedCheckCount > 0 ? expectedCheckCount : t("impact.noneDiscovered")}</span>
                    </div>
                    <div className="flex items-center justify-between gap-2 text-muted-foreground">
                      <span>{t("impact.baseVsOrigin")}</span>
                      <span className={staleBase ? "font-mono text-amber-500" : "font-mono text-foreground"}>
                        {structured?.baseSnapshot ? (staleBase ? t("impact.behind", { n: structured.baseSnapshot.behindCount }) : structured.baseSnapshot.freshness) : t("impact.unknown")}
                      </span>
                    </div>
                    {requiresAcknowledgement && (
                      <label className="mt-2 flex cursor-pointer items-start gap-2 border-t border-border/60 pt-2 text-foreground">
                        <input
                          type="checkbox"
                          checked={acknowledgedRisks}
                          onChange={(e) => setAcknowledgedRisks(e.target.checked)}
                          className="mt-0.5 h-3.5 w-3.5 accent-[var(--color-primary)]"
                        />
                        <span className="leading-snug">
                          {t("impact.ack.reviewed", { what: uncertainFileCount > 0 && staleBase ? t("impact.ack.both") : uncertainFileCount > 0 ? t("impact.ack.uncertainFiles") : t("impact.ack.staleBase") })}
                        </span>
                      </label>
                    )}
                  </div>

                  <div className="mt-3 flex flex-col gap-2">
                    <Button
                      variant="primary"
                      size="lg"
                      className="w-full"
                      disabled={isApproving || isRejecting || realFiles.length > 20 || (requiresAcknowledgement && !acknowledgedRisks)}
                      onClick={handleApprove}
                    >
                      {isApproving ? (
                        <>
                          <Loader2 className="h-4 w-4 animate-spin" />
                          {t("impact.approving")}
                        </>
                      ) : (
                        <>
                          <Check className="h-4 w-4" />
                          {t("impact.approve")}
                        </>
                      )}
                    </Button>
                    <div className="flex gap-2">
                      <Button
                        variant="danger"
                        size="md"
                        className="flex-1"
                        disabled={isApproving || isRejecting}
                        onClick={handleReject}
                      >
                        {isRejecting ? (
                          <>
                            <Loader2 className="h-3.5 w-3.5 animate-spin" />
                            {t("impact.rejecting")}
                          </>
                        ) : (
                          t("impact.reject")
                        )}
                      </Button>
                    </div>
                  </div>
                </>
              ) : actionState.kind === "approved" ? (
                <div className="space-y-3 min-w-0">
                  <div className="flex items-center gap-2.5 min-w-0">
                    <IconChip tone="blue" className="shrink-0">
                      <Check className="h-4 w-4" />
                    </IconChip>
                    <div className="min-w-0 flex-1">
                      <div className="text-[13px] font-semibold text-foreground">{t("impact.planApproved")}</div>
                      <div className="text-[12px] text-muted-foreground">{t("impact.planApprovedDesc")}</div>
                    </div>
                  </div>

                  {startExecutionError && (
                    <div className="flex items-center gap-1.5 text-[12px] font-medium text-danger">
                      <AlertCircle className="h-3.5 w-3.5 shrink-0" />
                      <span className="break-words">{startExecutionError}</span>
                    </div>
                  )}

                  <Button
                    variant="primary"
                    size="lg"
                    className="w-full"
                    disabled={isStartingExecution}
                    onClick={handleStartExecution}
                  >
                    {isStartingExecution ? (
                      <>
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t("impact.startingExecution")}
                      </>
                    ) : (
                      <>
                        <Play className="h-4 w-4" />
                        {t("impact.startExecution")}
                      </>
                    )}
                  </Button>
                </div>
              ) : actionState.kind === "rejected" ? (
                <div className="flex items-center gap-2.5 min-w-0">
                  <IconChip tone="red" className="shrink-0">
                    <AlertCircle className="h-4 w-4 text-danger" />
                  </IconChip>
                  <div className="min-w-0 flex-1">
                    <div className="text-[13px] font-semibold text-foreground">{t("impact.planRejected")}</div>
                    <div className="text-[12px] text-muted-foreground">{t("impact.planRejectedDesc")}</div>
                  </div>
                </div>
              ) : actionState.kind === "failed" ? (
                <div className="space-y-3 min-w-0">
                  <div className="flex items-center gap-2.5 min-w-0">
                    <IconChip tone="red" className="shrink-0">
                      <AlertCircle className="h-4 w-4 text-danger" />
                    </IconChip>
                    <div className="min-w-0 flex-1">
                      <div className="text-[13px] font-semibold text-foreground">{t("impact.execFailed")}</div>
                      <div className="text-[12px] text-muted-foreground">{t("impact.execFailedDesc")}</div>
                    </div>
                  </div>

                  {retryExecutionError && (
                    <div className="flex items-center gap-1.5 text-[12px] font-medium text-danger">
                      <AlertCircle className="h-3.5 w-3.5 shrink-0" />
                      <span className="break-words">{retryExecutionError}</span>
                    </div>
                  )}

                  <Button
                    variant="primary"
                    size="lg"
                    className="w-full"
                    disabled={isRetryingExecution}
                    onClick={handleRetryExecution}
                  >
                    {isRetryingExecution ? (
                      <>
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t("impact.retryingExecution")}
                      </>
                    ) : (
                      <>
                        <RotateCcw className="h-4 w-4" />
                        {t("impact.retryExecution")}
                      </>
                    )}
                  </Button>
                </div>
              ) : null}
            </div>
          )}

          {/* Only an approved plan can be re-run with other models; never while a run is in progress. */}
          {id && !activeExecution && task && (task.status === TaskStatus.Approved || task.status === TaskStatus.Failed || task.status === TaskStatus.Completed) && (
            <CompareModelsPanel taskId={id} />
          )}
        </aside>
      </div>
    </PageContainer>
  )
}
