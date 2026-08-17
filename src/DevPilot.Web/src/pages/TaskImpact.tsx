import { useCallback, useEffect, useState } from "react"
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
  BrainCircuit,
} from "lucide-react"
import { PageContainer } from "@/components/shared"
import { Button, Panel, Badge, Meter, StatusDot, IconChip } from "@/components/ui/primitives"
import { getTask, getTaskImpactAnalysis, analyzeTaskImpact, approveTask, rejectTask, startExecution } from "@/api"
import {
  TaskStatus,
  TaskPriority,
  ImpactAnalysisStatus,
  type Task,
  type ImpactAnalysis,
  type ImpactedFile,
} from "@/types"
import { activeTask, affectedFiles as mockAffectedFiles, impactSummary as mockImpactSummary, statusMeta, riskMeta, type Tone } from "@/data/mock"

function getStatusToneAndLabel(status: number): { tone: Tone; label: string } {
  switch (status) {
    case TaskStatus.Draft:
      return { tone: "gray", label: "Draft" }
    case TaskStatus.ReadyForAnalysis:
      return { tone: "neutral", label: "Ready for Analysis" }
    case TaskStatus.Analyzing:
      return { tone: "blue", label: "Analyzing" }
    case TaskStatus.AwaitingApproval:
      return { tone: "amber", label: "Awaiting approval" }
    case TaskStatus.Approved:
      return { tone: "blue", label: "Approved" }
    case TaskStatus.Executing:
      return { tone: "blue", label: "Executing" }
    case TaskStatus.Completed:
      return { tone: "green", label: "Merged" }
    case TaskStatus.Failed:
      return { tone: "red", label: "Failed" }
    case TaskStatus.Rejected:
      return { tone: "red", label: "Rejected" }
    default:
      return { tone: "neutral", label: "Unknown" }
  }
}

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

  const [task, setTask] = useState<Task | null>(null)
  const [analysis, setAnalysis] = useState<ImpactAnalysis | null>(null)

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

  const handleStartExecution = async () => {
    if (!id || isStartingExecution) return
    setIsStartingExecution(true)
    setStartExecutionError(null)

    try {
      const execution = await startExecution(id)
      navigate(`/executions/${execution.id}`)
    } catch (err) {
      setStartExecutionError(err instanceof Error ? err.message : "Failed to start execution.")
    } finally {
      setIsStartingExecution(false)
    }
  }

  const loadData = useCallback(async () => {
    if (!id) return
    setIsLoading(true)
    setError(null)
    try {
      // Check if this matches mock task id
      if (id === activeTask.id || id === "TASK-142") {
        setTask(null)
        setAnalysis(null)
        setIsLoading(false)
        return
      }

      // 1. Fetch task details
      const loadedTask = await getTask(id)
      setTask(loadedTask)

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
  }, [id])

  useEffect(() => {
    loadData()
  }, [loadData])

  const handleStartAnalysis = async () => {
    if (!id || isAnalyzing) return
    setIsAnalyzing(true)
    setAnalysisError(null)
    try {
      const result = await analyzeTaskImpact(id)
      setAnalysis(result)

      // Re-fetch updated task so task status in header refreshes immediately (e.g. to AwaitingApproval)
      try {
        const updatedTask = await getTask(id)
        setTask(updatedTask)
      } catch (err) {
        console.error("Failed to refresh task state after analysis", err)
      }
    } catch (err) {
      setAnalysisError(err instanceof Error ? err.message : "Failed to generate impact analysis.")
    } finally {
      setIsAnalyzing(false)
    }
  }

  // Fallback to mock data if viewing mock task ID
  const isMockView = !id || id === activeTask.id || id === "TASK-142"

  const handleApprove = async () => {
    if (!id || isApproving || isRejecting) return
    if (isMockView) {
      setMockStatus("approved")
      return
    }

    setIsApproving(true)
    setApprovalError(null)

    try {
      await approveTask(id)
      const updatedTask = await getTask(id)
      setTask(updatedTask)
    } catch (err) {
      setApprovalError(err instanceof Error ? err.message : "Failed to approve task.")
      // Sync latest task status from backend in case of 409 Conflict
      try {
        const currentTask = await getTask(id)
        setTask(currentTask)
      } catch {
        // ignore secondary fetch failure
      }
    } finally {
      setIsApproving(false)
    }
  }

  const handleReject = async () => {
    if (!id || isApproving || isRejecting) return
    if (isMockView) {
      setMockStatus("rejected")
      return
    }

    setIsRejecting(true)
    setApprovalError(null)

    try {
      await rejectTask(id)
      const updatedTask = await getTask(id)
      setTask(updatedTask)
    } catch (err) {
      setApprovalError(err instanceof Error ? err.message : "Failed to reject task.")
      // Sync latest task status from backend in case of 409 Conflict
      try {
        const currentTask = await getTask(id)
        setTask(currentTask)
      } catch {
        // ignore secondary fetch failure
      }
    } finally {
      setIsRejecting(false)
    }
  }

  if (isLoading) {
    return (
      <PageContainer className="flex flex-col items-center justify-center py-24">
        <Loader2 className="h-6 w-6 animate-spin text-subtle-foreground" />
        <p className="mt-3 text-[13.5px] text-muted-foreground">Loading task impact analysis…</p>
      </PageContainer>
    )
  }

  if (error) {
    return (
      <PageContainer className="flex flex-col items-center justify-center py-20">
        <AlertCircle className="h-8 w-8 text-danger" />
        <h2 className="mt-3 text-[16px] font-semibold text-foreground">Task not found</h2>
        <p className="mt-1 text-[13px] text-muted-foreground">{error}</p>
        <Button variant="default" size="sm" className="mt-4" onClick={() => navigate("/tasks")}>
          <ArrowLeft className="h-3.5 w-3.5" />
          Back to tasks
        </Button>
      </PageContainer>
    )
  }

  // Render variables derived from either real API or mock fallback
  const displayId = isMockView
    ? activeTask.id
    : task
      ? task.id.length > 12
        ? `TASK-${task.id.slice(0, 8)}`
        : task.id
      : id || ""

  const displayTitle = isMockView ? activeTask.title : task?.title || "Untitled Task"
  const displayBranch = isMockView
    ? activeTask.branch
    : task
      ? `${task.repositoryOwner}/${task.repositoryName}`
      : "master"

  const isAwaitingApproval = isMockView
    ? mockStatus === "awaiting-approval"
    : task?.status === TaskStatus.AwaitingApproval

  const isApproved = isMockView
    ? mockStatus === "approved"
    : task?.status === TaskStatus.Approved

  const isRejected = isMockView
    ? mockStatus === "rejected"
    : task?.status === TaskStatus.Rejected

  const statusInfo = isMockView
    ? (mockStatus === "approved"
        ? { tone: "blue" as Tone, label: "Approved" }
        : mockStatus === "rejected"
          ? { tone: "red" as Tone, label: "Rejected" }
          : statusMeta[mockStatus as keyof typeof statusMeta] || { tone: "amber" as Tone, label: "Awaiting approval" })
    : task
      ? getStatusToneAndLabel(task.status)
      : { tone: "neutral" as Tone, label: "Unknown" }

  const priorityInfo = isMockView
    ? { tone: riskMeta[activeTask.risk].tone, label: activeTask.risk === "low" ? "Low" : activeTask.risk === "high" ? "High" : "Medium" }
    : task
      ? getPriorityToneAndLabel(task.priority)
      : { tone: "neutral" as Tone, label: "Normal" }

  const structured = analysis?.structuredResult

  const hasCompletedAnalysis =
    isMockView ||
    (analysis !== null &&
      analysis.status === ImpactAnalysisStatus.Completed &&
      structured !== null)

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
          <Badge tone={statusInfo.tone} className="shrink-0">{statusInfo.label}</Badge>
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
        <aside className="border-b border-border p-5 lg:border-b-0 lg:border-r min-w-0 overflow-hidden">
          <div className="tech-label mb-2">Requirement</div>
          <p className="text-[13.5px] leading-relaxed text-foreground text-pretty break-words min-w-0">
            {requirementText}
          </p>

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

        {/* CENTER — Affected files + inspector */}
        <section className="border-b border-border p-5 lg:border-b-0 min-w-0 overflow-hidden">
          {!hasCompletedAnalysis ? (
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
                className="mt-5"
                disabled={isAnalyzing}
                onClick={handleStartAnalysis}
              >
                {isAnalyzing ? (
                  <>
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Analyzing workspace…
                  </>
                ) : (
                  <>
                    <Sparkles className="h-4 w-4" />
                    Run Impact Analysis
                  </>
                )}
              </Button>
            </div>
          ) : (
            <>
              <div className="mb-3 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <h2 className="text-[13px] font-semibold text-foreground">Impact analysis</h2>
                  <span className="rounded-full bg-surface-3 px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                    {isMockView ? mockAffectedFiles.length : realFiles.length} files
                  </span>
                </div>
              </div>

              <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,300px)] min-w-0">
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
                              <div className="flex items-center gap-2 min-w-0">
                                <span
                                  className={
                                    "truncate text-[12.5px] font-medium " +
                                    (isSel ? "text-primary" : "text-foreground")
                                  }
                                >
                                  {fileName}
                                </span>
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
                  <Panel className="h-fit p-4 min-w-0 overflow-hidden">
                    <div className="mb-3 flex items-center gap-2 min-w-0">
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
                    <div className="tech-label mb-1.5">Why it changes</div>
                    <p className="text-[12.5px] leading-relaxed text-muted-foreground text-pretty break-words">
                      {selectedFile.reason}
                    </p>

                    <div className="mt-4 flex items-center justify-between">
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

                    <div className="mt-4 flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-inset px-2.5 py-2 min-w-0">
                      <Badge
                        tone={
                          "changeType" in selectedFile &&
                          (selectedFile.changeType === "added" || selectedFile.changeType === "Add")
                            ? "green"
                            : "amber"
                        }
                        className="shrink-0"
                      >
                        {"changeType" in selectedFile
                          ? selectedFile.changeType === "added" || selectedFile.changeType === "Add"
                            ? "New file"
                            : selectedFile.changeType
                          : "Modified"}
                      </Badge>
                    </div>
                  </Panel>
                )}
              </div>
            </>
          )}
        </section>

        {/* RIGHT — System impact + decision */}
        <aside className="p-5 lg:border-l lg:border-border min-w-0 overflow-hidden">
          <div className="tech-label mb-3">System impact</div>
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

          {hasCompletedAnalysis && (
            <div className="mt-6 rounded-[var(--radius-lg)] border border-primary-ring/50 bg-primary-soft/50 p-4 min-w-0">
              {isAwaitingApproval ? (
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
                      disabled={isApproving || isRejecting}
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
              ) : isApproved ? (
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
              ) : isRejected ? (
                <div className="flex items-center gap-2.5 min-w-0">
                  <IconChip tone="red" className="shrink-0">
                    <AlertCircle className="h-4 w-4 text-danger" />
                  </IconChip>
                  <div className="min-w-0 flex-1">
                    <div className="text-[13px] font-semibold text-foreground">Plan rejected</div>
                    <div className="text-[12px] text-muted-foreground">This task plan was rejected.</div>
                  </div>
                </div>
              ) : null}
            </div>
          )}
        </aside>
      </div>
    </PageContainer>
  )
}
