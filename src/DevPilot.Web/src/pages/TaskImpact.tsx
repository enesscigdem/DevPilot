import { useCallback, useEffect, useRef, useState } from "react"
import { Link, useNavigate, useParams } from "react-router-dom"
import {
  ArrowLeft,
  Check,
  ChevronRight,
  FileCode2,
  GitBranch,
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
import { activeTask, affectedFiles as mockAffectedFiles, impactSummary as mockImpactSummary, riskMeta } from "@/data/mock"

function getPriorityToneAndLabel(priority: number): { tone: Tone; label: string } {
  switch (priority) {
    case TaskPriority.Low:
      return { tone: "green", label: "Low" }
    case TaskPriority.Medium:
      return { tone: "amber", label: "Medium" }
    case TaskPriority.High:
    case TaskPriority.Critical:
      return { tone: "red", label: "High" }
    default:
      return { tone: "neutral", label: "Normal" }
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

export function TaskImpact() {
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
  const [isRejecting, setIsRejecting] = useState(false)
  const [approvalError, setApprovalError] = useState<string | null>(null)
  const [mockStatus, setMockStatus] = useState<string>("awaiting-approval")

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

  // Fallback to mock data if viewing mock task ID
  const isMockView = !id || id === activeTask.id || id === "TASK-142"

  const lifecycleState = deriveTaskImpactLifecycle(
    task,
    analysis,
    activeExecution,
    isMockView,
    mockStatus,
    nowMs,
  )

  const actionState = deriveTaskImpactActionState(
    task?.status,
    activeExecution,
    isMockView,
    mockStatus,
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
      setStartExecutionError(err instanceof Error ? err.message : "Failed to start execution.")
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
      setRetryExecutionError(err instanceof Error ? err.message : "Failed to retry execution.")
    } finally {
      setIsRetryingExecution(false)
    }
  }

  const loadData = useCallback(async () => {
    if (!id || id === activeTask.id || id === "TASK-142") {
      setIsLoading(false)
      return
    }

    setIsLoading(true)
    setError(null)

    try {
      // 1. Fetch task details & active executions in parallel
      const [loadedTask, execs] = await Promise.all([
        getTask(id),
        getExecutions(activeWorkspaceId).catch(() => []),
      ])
      setTask(loadedTask)

      // Find active execution for this task (Pending or Running)
      const active = execs.find(
        (e) =>
          e.developmentTaskId === id &&
          (e.status === TaskExecutionStatus.Pending || e.status === TaskExecutionStatus.Running),
      )
      setActiveExecution(active ?? null)

      // 2. Fetch impact analysis (404 means no analysis yet)
      try {
        const loadedAnalysis = await getTaskImpactAnalysis(id)
        setAnalysis(loadedAnalysis)
      } catch {
        setAnalysis(null)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Task not found.")
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
    if (!lifecycleState.isAnalyzing || !id || isMockView) return

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
  }, [id, lifecycleState.isAnalyzing, isMockView])

  // Scoped polling while task has an actual active execution OR task.status claims Executing
  const isPollingExecRef = useRef(false)
  useEffect(() => {
    const isExecutingOrSyncing =
      activeExecution != null || task?.status === TaskStatus.Executing

    if (!isExecutingOrSyncing || !id || isMockView) return

    const interval = setInterval(async () => {
      if (isPollingExecRef.current) return
      isPollingExecRef.current = true
      try {
        const [updatedTask, execs] = await Promise.all([
          getTask(id).catch(() => null),
          getExecutions(activeWorkspaceId).catch(() => []),
        ])
        if (updatedTask) setTask(updatedTask)
        const active = execs.find(
          (e) =>
            e.developmentTaskId === id &&
            (e.status === TaskExecutionStatus.Pending || e.status === TaskExecutionStatus.Running),
        )
        setActiveExecution(active ?? null)
      } catch {
        // ignore polling failures
      } finally {
        isPollingExecRef.current = false
      }
    }, 3500)

    return () => clearInterval(interval)
  }, [id, activeWorkspaceId, activeExecution, task?.status, isMockView])

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
      setAnalysisError(err instanceof Error ? err.message : "Failed to generate impact analysis.")
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
      if (isMockView) {
        setMockStatus("approved")
      } else {
        await approveTask(id)
        const updatedTask = await getTask(id)
        setTask(updatedTask)
      }
    } catch (err) {
      setApprovalError(err instanceof Error ? err.message : "Failed to approve task.")
    } finally {
      setIsApproving(false)
    }
  }

  const handleReject = async () => {
    if (!id || isApproving || isRejecting) return
    setIsRejecting(true)
    setApprovalError(null)
    try {
      if (isMockView) {
        setMockStatus("rejected")
      } else {
        await rejectTask(id)
        const updatedTask = await getTask(id)
        setTask(updatedTask)
      }
    } catch (err) {
      setApprovalError(err instanceof Error ? err.message : "Failed to reject task.")
    } finally {
      setIsRejecting(false)
    }
  }

  if (isLoading) {
    return (
      <PageContainer className="flex items-center justify-center py-24">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="h-6 w-6 animate-spin text-primary" />
          <span className="tech-label">Loading task impact analysis…</span>
        </div>
      </PageContainer>
    )
  }

  if (error || (!isMockView && !task)) {
    return (
      <PageContainer className="py-12">
        <div className="mx-auto max-w-md text-center">
          <AlertCircle className="mx-auto h-8 w-8 text-danger" />
          <h2 className="mt-3 text-[15px] font-semibold text-foreground">Task not found</h2>
          <p className="mt-1 text-[13px] text-muted-foreground">{error || "The requested task could not be loaded."}</p>
          <Button variant="default" size="md" className="mt-4" onClick={() => navigate("/tasks")}>
            <ArrowLeft className="h-4 w-4" />
            Back to tasks
          </Button>
        </div>
      </PageContainer>
    )
  }

  const displayTitle = isMockView ? activeTask.title : task?.title || "Untitled task"
  const displayId = isMockView ? activeTask.id : `TASK-${task?.id.slice(0, 6).toUpperCase()}`
  const displayBranch = isMockView ? activeTask.branch : `feature/${task?.title.toLowerCase().replace(/[^a-z0-9]/g, "-").slice(0, 30)}`

  const priorityInfo = isMockView
    ? { tone: riskMeta[activeTask.risk].tone, label: activeTask.risk === "low" ? "Low" : activeTask.risk === "high" ? "High" : "Medium" }
    : task
      ? getPriorityToneAndLabel(task.priority)
      : { tone: "neutral" as Tone, label: "Normal" }

  const structured = analysis?.structuredResult

  const hasCompletedAnalysis = lifecycleState.isSucceeded

  // Risk badge comes ONLY from real persisted impact analysis data (or mock view)
  let analysisRiskInfo: { tone: Tone; label: string } | null = null
  if (isMockView) {
    analysisRiskInfo = { tone: riskMeta[activeTask.risk].tone, label: riskMeta[activeTask.risk].label }
  } else if (hasCompletedAnalysis && structured?.risks && structured.risks.length > 0) {
    const levels = structured.risks.map((r) => r.level?.toLowerCase())
    let topLevel = "low"
    if (levels.includes("critical") || levels.includes("high")) topLevel = "high"
    else if (levels.includes("medium")) topLevel = "medium"

    analysisRiskInfo = {
      tone: getImpactLevelTone(topLevel),
      label: topLevel === "high" ? "High risk" : topLevel === "medium" ? "Medium risk" : "Low risk",
    }
  }

  const confidence = isMockView
    ? activeTask.confidence
    : structured?.confidence ?? analysis?.confidence ?? null

  const requirementText = isMockView ? activeTask.requirement : task?.description || "No description provided."

  const acceptanceList = isMockView
    ? activeTask.acceptance
    : task?.acceptanceCriteria && task.acceptanceCriteria.trim().length > 0
      ? task.acceptanceCriteria.split("\n").filter((line) => line.trim().length > 0)
      : []

  const planSteps = isMockView
    ? activeTask.planSteps
    : structured?.proposedPlan && structured.proposedPlan.length > 0
      ? structured.proposedPlan.map((s) => ({
          title: s.title,
          detail: s.description,
          files: s.relatedFiles || [],
        }))
      : []

  const realFiles: ImpactedFile[] = structured?.impactedFiles || []

  const selectedFile = isMockView
    ? mockAffectedFiles[selectedFileIndex] || mockAffectedFiles[0]
    : hasCompletedAnalysis && realFiles.length > 0
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
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground min-w-0 truncate">
              <GitBranch className="h-3 w-3 shrink-0" />
              <span className="truncate">{displayBranch}</span>
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
                ? `Completed in ${lifecycleState.durationFormatted}`
                : `Failed after ${lifecycleState.durationFormatted}`}
            </Badge>
          )}
          <Badge tone={priorityInfo.tone} className="shrink-0">Priority: {priorityInfo.label}</Badge>
          {analysisRiskInfo && (
            <Badge tone={analysisRiskInfo.tone} className="shrink-0">{analysisRiskInfo.label}</Badge>
          )}
          {hasCompletedAnalysis && confidence !== null && (
            <div className="hidden items-center gap-1.5 md:flex shrink-0">
              <span className="tech-label">Confidence</span>
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
            <span className="tech-label">Requirement</span>
          </div>
          <div className="text-[13px] leading-relaxed text-foreground min-w-0">
            <FormattedText text={requirementText} />
          </div>

          {acceptanceList.length > 0 && (
            <>
              <div className="tech-label mb-2 mt-6">Acceptance criteria</div>
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
                Proposed plan
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
              <h3 className="mt-3 text-[14px] font-semibold text-foreground">Analyzing workspace…</h3>
              <p className="mt-1 max-w-md text-[12.5px] leading-relaxed text-muted-foreground">
                DevPilot is inspecting Roslyn project references, symbol graphs, and generating impact predictions.
              </p>
              <div className="mt-4 inline-flex items-center gap-2 rounded-full border border-border bg-surface px-3 py-1 font-mono text-[12px] text-subtle-foreground">
                <span className="h-2 w-2 rounded-full bg-primary animate-pulse" />
                Elapsed: {lifecycleState.elapsedSeconds}s
              </div>
            </div>
          ) : lifecycleState.lifecycle === "failed" ? (
            <div className="flex flex-col items-center justify-center rounded-[var(--radius-lg)] border border-danger/30 bg-danger-soft/20 px-6 py-12 text-center min-w-0">
              <AlertCircle className="h-9 w-9 text-danger" />
              <h3 className="mt-3 text-[14.5px] font-semibold text-foreground">Impact analysis failed</h3>
              {lifecycleState.durationFormatted && (
                <span className="mt-0.5 font-mono text-[11.5px] text-muted-foreground">
                  Failed after {lifecycleState.durationFormatted}
                </span>
              )}
              <div className="mt-4 max-w-lg w-full text-left rounded-[var(--radius-md)] border border-danger/30 bg-surface p-3.5 text-[12px] leading-relaxed text-foreground break-words font-mono">
                {lifecycleState.sanitizedErrorMessage || analysisError || "Impact analysis failed."}
              </div>

              <Button
                variant="primary"
                size="md"
                className="mt-5 gap-2"
                disabled={!lifecycleState.canRetry || isAnalyzing}
                onClick={handleStartAnalysis}
              >
                <RotateCcw className="h-4 w-4" />
                Retry analysis
              </Button>
            </div>
          ) : lifecycleState.lifecycle === "idle" ? (
            <div className="flex flex-col items-center justify-center rounded-[var(--radius-lg)] border border-dashed border-border px-6 py-16 text-center">
              <BrainCircuit className="h-8 w-8 text-primary opacity-80" />
              <h3 className="mt-3 text-[14px] font-semibold text-foreground">No impact analysis generated yet</h3>
              <p className="mt-1 max-w-md text-[12.5px] leading-relaxed text-muted-foreground">
                DevPilot can analyze the Roslyn workspace graph, identify affected files, build a step-by-step implementation plan, and estimate risk.
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
                Run Impact Analysis
              </Button>
            </div>
          ) : (
            <>
              {/* Grounding Unresolved Banner */}
              {structured?.isGroundingUnresolved && (
                <div className="mb-4 flex items-start gap-2.5 rounded-[var(--radius-md)] border border-red-500/40 bg-red-500/10 p-3.5 text-[12.5px] text-red-400">
                  <AlertCircle className="h-4 w-4 shrink-0 text-red-400 mt-0.5" />
                  <div className="min-w-0">
                    <strong className="font-semibold text-red-300">Grounding Unresolved:</strong>{" "}
                    {structured.unresolvedReason || "Central task subject could not be resolved in repository evidence."}
                    <p className="mt-1 text-[11.5px] text-red-400/80">
                      Executable plan and approval are blocked because the target entity or member does not exist in the repository. Advisory risk analysis is retained below.
                    </p>
                  </div>
                </div>
              )}

              {/* Change Brief */}
              {structured?.changeBrief && (
                <div className="mb-4 rounded-[var(--radius-lg)] border border-primary/25 bg-surface p-4 shadow-sm">
                  <div className="flex items-center justify-between gap-2 border-b border-border/40 pb-2.5 mb-3">
                    <div className="flex items-center gap-2">
                      <Sparkles className="h-4 w-4 text-primary" />
                      <span className="text-[13px] font-semibold text-foreground">Change Brief</span>
                    </div>
                    <div className="flex items-center gap-1.5 font-mono text-[11px] text-muted-foreground">
                      <span className="font-semibold text-foreground">{structured.changeBrief.fileCount}</span> files
                      <span>·</span>
                      <span className="font-semibold text-foreground">{structured.changeBrief.projectCount}</span> project(s)
                      <span>·</span>
                      <Badge tone={analysisRiskInfo?.tone ?? "neutral"} className="px-1.5 py-0 text-[10.5px]">
                        {structured.changeBrief.riskLevel} Risk
                      </Badge>
                    </div>
                  </div>

                  <div className="grid gap-3 sm:grid-cols-2 text-[12px]">
                    <div>
                      <span className="tech-label text-[10.5px]">Explainable Risk</span>
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
                      <span className="tech-label text-[10.5px]">Verification Preflight</span>
                      <p className="mt-1 text-[11.5px] text-muted-foreground break-words">
                        {structured.changeBrief.verificationSummary || "Standard repository preflight checks"}
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

              {/* Database Impact */}
              {((structured?.databaseImpact && (structured.databaseImpact.requiresSchemaMigration || structured.databaseImpact.changes.length > 0 || structured.databaseImpact.requiresDataMigration)) ||
                (structured?.changeBrief?.databaseImpact && (structured.changeBrief.databaseImpact.requiresSchemaMigration || structured.changeBrief.databaseImpact.changes.length > 0))) && (() => {
                const db = structured?.databaseImpact || structured?.changeBrief?.databaseImpact;
                if (!db) return null;
                const isDestructive = db.changeKind === "Destructive" || db.dataRiskLevel === "High" || db.dataRiskLevel === "Critical";
                return (
                  <div className={`mb-4 rounded-[var(--radius-lg)] border ${isDestructive ? "border-amber-500/40 bg-amber-500/5" : "border-border/60 bg-surface"} p-4 shadow-sm`}>
                    <div className="flex items-center justify-between gap-2 border-b border-border/40 pb-2.5 mb-3">
                      <div className="flex items-center gap-2">
                        <Database className={`h-4 w-4 ${isDestructive ? "text-amber-500" : "text-primary"}`} />
                        <span className="text-[13px] font-semibold text-foreground">Database Impact</span>
                      </div>
                      <div className="flex items-center gap-2 font-mono text-[11px] text-muted-foreground">
                        <span>Schema migration: <strong className="text-foreground">{db.requiresSchemaMigration ? (db.migrationRequirement || "Expected") : "None"}</strong></span>
                        <span>·</span>
                        <span>Data risk: <strong className={db.dataRiskLevel === "High" || db.dataRiskLevel === "Critical" ? "text-amber-500" : "text-foreground"}>{db.dataRiskLevel || "Low"}</strong></span>
                        {db.requiresDataMigration && (
                          <>
                            <span>·</span>
                            <Badge tone="amber" className="px-1.5 py-0 text-[10.5px]">
                              Data migration: {db.dataMigrationRequirement === "ReviewRequired" ? "Review required" : db.dataMigrationRequirement}
                            </Badge>
                          </>
                        )}
                      </div>
                    </div>

                    {db.changes.length > 0 && (
                      <div className="mb-3">
                        <span className="tech-label text-[10.5px]">Changes</span>
                        <div className="mt-1 space-y-1 font-mono text-[11.5px]">
                          {db.changes.map((change, idx) => (
                            <div key={idx} className="flex items-center gap-2">
                              <span className={`font-bold ${change.operation === "Remove" ? "text-destructive" : change.operation === "Add" ? "text-success" : "text-amber-500"}`}>
                                {change.operation === "Remove" ? "-" : change.operation === "Add" ? "+" : "~"}
                              </span>
                              <span className="font-semibold text-foreground">
                                {change.parentObjectName ? `${change.parentObjectName}.${change.objectName}` : change.objectName}
                              </span>
                              <span className="text-muted-foreground text-[11px]">{change.evidence}</span>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}

                    {db.summary && (
                      <div className="mb-2 text-[12px]">
                        <span className="tech-label text-[10.5px]">Risk & Strategy</span>
                        <p className="mt-1 text-[11.5px] text-muted-foreground break-words">{db.summary}</p>
                      </div>
                    )}

                    {db.unknowns.length > 0 && (
                      <div className="mt-2 text-[12px]">
                        <span className="tech-label text-[10.5px]">Unknowns</span>
                        <ul className="mt-1 space-y-0.5 text-[11.5px] text-muted-foreground">
                          {db.unknowns.map((u, idx) => (
                            <li key={idx} className="flex items-start gap-1.5">
                              <span className="text-muted-foreground">•</span>
                              <span className="break-words">{u}</span>
                            </li>
                          ))}
                        </ul>
                      </div>
                    )}
                  </div>
                );
              })()}

              <div className="mb-3 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <h2 className="text-[13px] font-semibold text-foreground">Impacted files</h2>
                  <span className="rounded-full bg-surface-3 px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                    {isMockView ? mockAffectedFiles.length : realFiles.length} files
                  </span>
                </div>
              </div>

              <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,320px)] min-w-0">
                {/* file list */}
                <div className="overflow-hidden rounded-[var(--radius-lg)] border border-border min-w-0">
                  {isMockView
                    ? mockAffectedFiles.map((f, idx) => {
                        const isSel = idx === selectedFileIndex
                        return (
                          <button
                            key={f.path}
                            onClick={() => setSelectedFileIndex(idx)}
                            className={
                              "flex w-full items-center gap-2.5 border-b border-border px-3 py-2.5 text-left transition-colors last:border-b-0 min-w-0 " +
                              (isSel ? "bg-primary-soft/70" : "hover:bg-surface-2")
                            }
                          >
                            {f.changeType === "added" ? (
                              <Plus className="h-3.5 w-3.5 shrink-0 text-success" />
                            ) : (
                              <Pencil className="h-3.5 w-3.5 shrink-0 text-accent" />
                            )}
                            <div className="min-w-0 flex-1">
                              <div className="flex items-center gap-2 min-w-0">
                                <span
                                  className={
                                    "truncate text-[12.5px] font-medium " +
                                    (isSel ? "text-primary" : "text-foreground")
                                  }
                                >
                                  {f.name}
                                </span>
                              </div>
                              <div className="truncate font-mono text-[10.5px] text-subtle-foreground" title={f.path}>
                                {f.path}
                              </div>
                            </div>
                            <div className="flex items-center gap-1.5 font-mono text-[10.5px] shrink-0">
                              <span className="text-success">+{f.additions}</span>
                              <span className="text-danger">−{f.deletions}</span>
                            </div>
                            {isSel && <ChevronRight className="h-4 w-4 shrink-0 text-primary" />}
                          </button>
                        )
                      })
                    : realFiles.map((f, idx) => {
                        const isSel = idx === selectedFileIndex
                        const fileName = f.filePath.split("/").pop() || f.filePath
                        const isAdded = f.changeType === "Add"
                        return (
                          <button
                            key={f.filePath}
                            onClick={() => setSelectedFileIndex(idx)}
                            className={
                              "flex w-full items-center gap-2.5 border-b border-border px-3 py-2.5 text-left transition-colors last:border-b-0 min-w-0 " +
                              (isSel ? "bg-primary-soft/70" : "hover:bg-surface-2")
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
                                    Uncertain
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
                          {"name" in selectedFile
                            ? selectedFile.name
                            : selectedFile.filePath.split("/").pop() || selectedFile.filePath}
                        </div>
                        <div className="truncate font-mono text-[10.5px] text-subtle-foreground">
                          {"project" in selectedFile
                            ? selectedFile.project
                            : selectedFile.filePath.split("/")[1] || selectedFile.filePath.split("/")[0]}
                        </div>
                      </div>
                    </div>

                    <div className="flex items-center gap-1.5 flex-wrap">
                      <Badge
                        tone={
                          "changeType" in selectedFile &&
                          (selectedFile.changeType === "added" || selectedFile.changeType === "Add")
                            ? "green"
                            : "amber"
                        }
                      >
                        {"changeType" in selectedFile
                          ? selectedFile.changeType === "added" || selectedFile.changeType === "Add"
                            ? "New file"
                            : selectedFile.changeType
                          : "Modified"}
                      </Badge>
                      {"evidenceType" in selectedFile && selectedFile.evidenceType && (
                        <Badge tone="neutral" className="font-mono text-[10.5px]">
                          {selectedFile.evidenceType}
                        </Badge>
                      )}
                      {"isUncertain" in selectedFile && selectedFile.isUncertain && (
                        <Badge tone="amber" className="text-[10.5px]">
                          Uncertain
                        </Badge>
                      )}
                    </div>

                    <div>
                      <div className="tech-label mb-1">Why it changes</div>
                      <p className="text-[12px] leading-relaxed text-muted-foreground text-pretty break-words">
                        {selectedFile.reason}
                      </p>
                    </div>

                    {"evidenceDetails" in selectedFile && selectedFile.evidenceDetails && (
                      <div className="rounded-[var(--radius-md)] border border-border/60 bg-surface-2 p-2.5">
                        <div className="tech-label text-[10px] mb-1">Repository Evidence</div>
                        <p className="text-[11.5px] leading-relaxed text-foreground font-mono break-words">
                          {selectedFile.evidenceDetails}
                        </p>
                      </div>
                    )}

                    <div>
                      <div className="flex items-center justify-between">
                        <span className="tech-label">Confidence</span>
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
            <div className="tech-label mb-2.5">System impact & dimensions</div>
            <div className="space-y-2.5 min-w-0">
              {isMockView
                ? [
                    { label: "API surface", icon: Network, items: mockImpactSummary.apiChanges },
                    { label: "Database", icon: Database, items: mockImpactSummary.database },
                    { label: "Integrations", icon: ShieldCheck, items: mockImpactSummary.integrations },
                    { label: "Tests", icon: FlaskConical, items: mockImpactSummary.tests },
                  ].map((g) => {
                    const item = g.items[0]
                    const Icon = g.icon
                    return (
                      <div key={g.label} className="rounded-[var(--radius-md)] border border-border bg-surface p-3 min-w-0">
                        <div className="mb-1.5 flex items-center gap-2 min-w-0">
                          <Icon className="h-3.5 w-3.5 text-subtle-foreground shrink-0" />
                          <span className="truncate text-[12px] font-semibold text-foreground">{g.label}</span>
                          <StatusDot tone={item.tone} className="ml-auto shrink-0" />
                        </div>
                        <div className="text-[12px] font-medium text-foreground break-words">{item.label}</div>
                        <p className="mt-0.5 text-[11.5px] leading-relaxed text-muted-foreground break-words">{item.detail}</p>
                      </div>
                    )
                  })
                : structured?.dimensions && structured.dimensions.length > 0
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
                            <div className="text-[12px] font-medium text-foreground">{si.impactLevel} Impact</div>
                            <p className="mt-0.5 text-[11.5px] leading-relaxed text-muted-foreground break-words">{si.description}</p>
                          </div>
                        )
                      })
                    : (
                        <div className="rounded-[var(--radius-md)] border border-border bg-surface p-3 text-[12px] text-muted-foreground">
                          No system impacts recorded.
                        </div>
                      )}
            </div>
          </div>

          {/* Unknowns section */}
          {structured?.unknowns && structured.unknowns.length > 0 && (
            <div className="rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/5 p-3 min-w-0">
              <div className="mb-1.5 flex items-center gap-1.5 text-[12px] font-semibold text-amber-500">
                <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
                <span>Unknowns & Boundaries</span>
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
                      <div className="text-[13px] font-semibold text-foreground">Execution in progress</div>
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
                      View live execution
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
                      <div className="text-[13px] font-semibold text-foreground">Execution state is syncing…</div>
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
                    <span className="text-[13px] font-semibold text-foreground truncate">Ready for your approval</span>
                  </div>
                  <p className="mt-1.5 text-[12px] leading-relaxed text-muted-foreground break-words">
                    DevPilot will implement the plan on branch{" "}
                    <span className="font-mono text-foreground break-all">{displayBranch}</span>, run the build and tests, then hand the
                    diff back for review. Nothing merges without you.
                  </p>

                  {realFiles.length > 20 && (
                    <div className="mt-3 flex items-start gap-2 rounded-[var(--radius-sm)] border border-danger/30 bg-danger/10 p-2.5 text-[12px] text-danger leading-relaxed">
                      <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                      <span>
                        Plan proposes <strong>{realFiles.length} files</strong>, exceeding maximum executable capacity (20 files). Please decompose the task into smaller focused tasks before executing.
                      </span>
                    </div>
                  )}

                  {approvalError && (
                    <div className="mt-3 flex items-center gap-1.5 text-[12px] text-danger font-medium">
                      <AlertCircle className="h-3.5 w-3.5 shrink-0" />
                      <span className="break-words">{approvalError}</span>
                    </div>
                  )}

                  <div className="mt-3 flex flex-col gap-2">
                    <Button
                      variant="primary"
                      size="lg"
                      className="w-full"
                      disabled={isApproving || isRejecting || realFiles.length > 20}
                      onClick={handleApprove}
                    >
                      {isApproving ? (
                        <>
                          <Loader2 className="h-4 w-4 animate-spin" />
                          Approving plan…
                        </>
                      ) : (
                        <>
                          <Check className="h-4 w-4" />
                          Approve plan
                        </>
                      )}
                    </Button>
                    <div className="flex gap-2">
                      <Button
                        variant="default"
                        size="md"
                        className="flex-1"
                        disabled={isApproving || isRejecting}
                      >
                        <Pencil className="h-3.5 w-3.5" />
                        Edit plan
                      </Button>
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
                            Rejecting…
                          </>
                        ) : (
                          "Reject"
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
                      <div className="text-[13px] font-semibold text-foreground">Plan approved</div>
                      <div className="text-[12px] text-muted-foreground">This task has been approved and is ready for execution.</div>
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
                        Starting execution…
                      </>
                    ) : (
                      <>
                        <Play className="h-4 w-4" />
                        Start execution
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
                    <div className="text-[13px] font-semibold text-foreground">Plan rejected</div>
                    <div className="text-[12px] text-muted-foreground">This task plan was rejected.</div>
                  </div>
                </div>
              ) : actionState.kind === "failed" ? (
                <div className="space-y-3 min-w-0">
                  <div className="flex items-center gap-2.5 min-w-0">
                    <IconChip tone="red" className="shrink-0">
                      <AlertCircle className="h-4 w-4 text-danger" />
                    </IconChip>
                    <div className="min-w-0 flex-1">
                      <div className="text-[13px] font-semibold text-foreground">Execution failed</div>
                      <div className="text-[12px] text-muted-foreground">The previous execution attempt failed. You can retry execution with the approved plan.</div>
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
                        Retrying execution…
                      </>
                    ) : (
                      <>
                        <RotateCcw className="h-4 w-4" />
                        Retry execution
                      </>
                    )}
                  </Button>
                </div>
              ) : null}
            </div>
          )}
        </aside>
      </div>
    </PageContainer>
  )
}
