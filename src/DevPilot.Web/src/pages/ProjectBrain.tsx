import { useCallback, useEffect, useState } from "react"
import {
  Sparkles,
  CornerDownLeft,
  FileCode2,
  Boxes,
  Search,
  Check,
  Database,
  Hash,
  Braces,
  Link2,
  Quote,
  ShieldCheck,
  Clock,
  Loader2,
  RefreshCw,
  MessageSquare,
  Plus,
  Trash2,
} from "lucide-react"
import { Button, Badge, StatusDot } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"
import {
  getBrainStatus,
  indexBrain,
  askBrain,
  getBrainConversations,
  getBrainConversationById,
  deleteBrainConversation,
} from "@/api"
import { getCachedBrainStatus, setCachedBrainStatus } from "@/lib/workspaceCache"
import { useWorkspace } from "@/lib/workspace"
import type {
  BrainMessage,
  BrainCitation,
  BrainStatus,
  BrainContextFile,
  BrainConversation,
} from "@/types"

function citationKey(c: BrainCitation) {
  return `${c.path}#${c.lines}`
}

function getLayerTone(layer: string): "blue" | "amber" | "green" | "neutral" | "gray" {
  switch (layer.toLowerCase()) {
    case "web":
      return "blue"
    case "application":
      return "amber"
    case "domain":
      return "green"
    case "tests":
      return "gray"
    default:
      return "neutral"
  }
}

/* ------------------------------ Conversation ------------------------------- */

function Message({
  msg,
  selectedKey,
  onSelect,
}: {
  msg: BrainMessage
  selectedKey: string | null
  onSelect: (c: BrainCitation) => void
}) {
  if (msg.role === "user") {
    return (
      <div className="flex justify-end">
        <div className="max-w-[78%] rounded-[var(--radius-lg)] rounded-br-sm bg-primary px-3.5 py-2.5 text-[13.5px] leading-relaxed text-primary-foreground">
          {msg.content}
        </div>
      </div>
    )
  }
  return (
    <div className="flex gap-3">
      <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
        <Sparkles className="h-3.5 w-3.5" />
      </div>
      <div className="min-w-0 flex-1 rounded-[var(--radius-lg)] border border-border bg-surface p-4 shadow-[var(--shadow-sm)]">
        <div className="mb-2.5 flex items-center gap-2 border-b border-border/80 pb-2">
          <span className="text-[12px] font-semibold text-foreground">Project Brain</span>
          {msg.confidence !== undefined && (
            <span className="flex items-center gap-1 font-mono text-[10.5px] text-subtle-foreground" title="Grounding score based on retrieval relevance and cited sources">
              <ShieldCheck className="h-3 w-3 text-success" />
              {msg.confidence}% grounded
            </span>
          )}
          {msg.elapsed && (
            <span className="flex items-center gap-1 font-mono text-[10.5px] text-subtle-foreground">
              <Clock className="h-3 w-3" />
              {msg.elapsed}
            </span>
          )}
        </div>
        <p className="text-[13.5px] leading-relaxed text-foreground text-pretty whitespace-pre-line">{msg.content}</p>

        {msg.citations && msg.citations.length > 0 && (
          <div className="mt-3.5 border-t border-border/80 pt-3">
            <div className="tech-label mb-1.5 flex items-center gap-1.5">
              <Quote className="h-3 w-3" />
              Grounded in {msg.citations.length} {msg.citations.length === 1 ? "source" : "sources"}
            </div>
            <div className="grid gap-1.5 sm:grid-cols-2">
              {msg.citations.map((c) => {
                const active = selectedKey === citationKey(c)
                return (
                  <button
                    key={citationKey(c)}
                    onClick={() => onSelect(c)}
                    className={cn(
                      "flex items-center justify-between rounded-[var(--radius-md)] border px-2.5 py-1.5 text-left transition-all",
                      active
                        ? "border-primary bg-primary-soft shadow-[var(--shadow-sm)]"
                        : "border-border bg-surface-2 hover:border-border-strong hover:bg-surface-3",
                    )}
                  >
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-1.5 font-mono text-[11.5px] font-medium text-foreground">
                        <FileCode2 className="h-3 w-3 shrink-0 text-subtle-foreground" />
                        <span className="truncate" title={c.file}>{c.file}</span>
                      </div>
                      <div className="font-mono text-[10px] text-subtle-foreground">
                        {c.lines} {c.symbol ? `· ${c.symbol}` : ""}
                      </div>
                    </div>
                    {c.lang && <Badge tone="neutral">{c.lang}</Badge>}
                  </button>
                )
              })}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

/* --------------------------- Source code preview --------------------------- */

function SourcePreview({ citation }: { citation: BrainCitation }) {
  const lines = citation.snippet ? citation.snippet.split("\n") : []
  const start = citation.startLine ?? 1
  return (
    <div className="min-w-0 overflow-hidden rounded-[var(--radius-md)] border border-border bg-inset">
      <div className="flex items-center gap-2 border-b border-border bg-surface-2 px-3 py-1.5">
        <FileCode2 className="h-3.5 w-3.5 shrink-0 text-primary" />
        <span className="truncate text-[12px] font-medium text-foreground" title={citation.file}>{citation.file}</span>
        <span className="ml-auto shrink-0 font-mono text-[10px] text-subtle-foreground">{citation.lang ?? "cs"}</span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full border-collapse font-mono text-[11.5px] leading-relaxed">
          <tbody>
            {lines.map((ln, i) => (
              <tr key={i} className="align-top">
                <td className="select-none whitespace-nowrap border-r border-border/70 px-2.5 py-0.5 text-right font-mono text-subtle-foreground">
                  {start + i}
                </td>
                <td className="whitespace-pre px-3 py-0.5 font-mono text-muted-foreground">{ln || " "}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

/* --------------------------------- Page ------------------------------------ */

export function ProjectBrain() {
  const { activeWorkspace, activeWorkspaceId } = useWorkspace()
  const cachedStatus = activeWorkspaceId ? getCachedBrainStatus(activeWorkspaceId) : { data: null, isStale: true }
  const [draft, setDraft] = useState("")
  const [status, setStatus] = useState<BrainStatus | null>(cachedStatus.data)
  const [isStatusLoading, setIsStatusLoading] = useState(!cachedStatus.data && !!activeWorkspaceId)
  const [isIndexing, setIsIndexing] = useState(false)
  const [isAsking, setIsAsking] = useState(false)
  const [conversations, setConversations] = useState<BrainConversation[]>([])
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null)
  const [isLoadingConversation, setIsLoadingConversation] = useState(false)
  const [conversation, setConversation] = useState<BrainMessage[]>([])
  const [contextFiles, setContextFiles] = useState<BrainContextFile[]>([])
  const [selected, setSelected] = useState<BrainCitation | null>(null)
  const [error, setError] = useState<string | null>(null)

  const repoFullName = activeWorkspace
    ? `${activeWorkspace.owner}/${activeWorkspace.repository}`
    : "No workspace selected"

  const selectedKey = selected ? citationKey(selected) : null

  const fetchStatus = useCallback(async (workspaceId: string) => {
    const cached = getCachedBrainStatus(workspaceId)
    if (cached.data) {
      setStatus(cached.data)
      setIsStatusLoading(false)
    } else {
      setIsStatusLoading(true)
    }
    setError(null)
    try {
      const data = await getBrainStatus(workspaceId)
      setStatus(data)
      setCachedBrainStatus(workspaceId, data)
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to load Project Brain status."
      setError(msg)
    } finally {
      setIsStatusLoading(false)
    }
  }, [])

  const fetchConversations = useCallback(async (workspaceId: string) => {
    try {
      const list = await getBrainConversations(workspaceId)
      setConversations(list)
    } catch {
      setConversations([])
    }
  }, [])

  // When active workspace changes: reset conversation, reload status & conversation list
  useEffect(() => {
    if (activeWorkspaceId) {
      setActiveConversationId(null)
      setConversation([])
      setContextFiles([])
      setSelected(null)
      setDraft("")
      setError(null)
      fetchStatus(activeWorkspaceId)
      fetchConversations(activeWorkspaceId)
    } else {
      setStatus(null)
      setConversations([])
      setActiveConversationId(null)
      setConversation([])
      setContextFiles([])
      setSelected(null)
      setDraft("")
      setError(null)
    }
  }, [activeWorkspaceId, fetchStatus, fetchConversations])

  const handleSelectConversation = async (convId: string) => {
    if (!activeWorkspaceId || convId === activeConversationId) return
    setIsLoadingConversation(true)
    setError(null)
    try {
      const detail = await getBrainConversationById(activeWorkspaceId, convId)
      setActiveConversationId(detail.id)
      const mapped: BrainMessage[] = detail.messages.map((m) => ({
        role: m.role,
        content: m.content,
        citations: m.citations ?? undefined,
        confidence: m.confidence ?? undefined,
        elapsed: m.elapsed ?? undefined,
      }))
      setConversation(mapped)

      const lastAssistant = [...detail.messages].reverse().find((m) => m.role === "assistant")
      if (lastAssistant?.contextFiles && lastAssistant.contextFiles.length > 0) {
        setContextFiles(lastAssistant.contextFiles)
      } else {
        setContextFiles([])
      }
      if (lastAssistant?.citations && lastAssistant.citations.length > 0) {
        setSelected(lastAssistant.citations[0])
      } else {
        setSelected(null)
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to load conversation."
      setError(msg)
    } finally {
      setIsLoadingConversation(false)
    }
  }

  const handleNewChat = () => {
    setActiveConversationId(null)
    setConversation([])
    setContextFiles([])
    setSelected(null)
    setDraft("")
    setError(null)
  }

  const handleDeleteConversation = async (e: React.MouseEvent, convId: string) => {
    e.stopPropagation()
    if (!activeWorkspaceId) return
    try {
      await deleteBrainConversation(activeWorkspaceId, convId)
      setConversations((prev) => prev.filter((c) => c.id !== convId))
      if (activeConversationId === convId) {
        handleNewChat()
      }
    } catch {
      // Ignore
    }
  }

  const handleIndex = async () => {
    if (!activeWorkspaceId || isIndexing) return
    setIsIndexing(true)
    setError(null)
    try {
      await indexBrain(activeWorkspaceId)
      await fetchStatus(activeWorkspaceId)
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Indexing failed."
      setError(msg)
    } finally {
      setIsIndexing(false)
    }
  }

  const handleAsk = async (questionToAsk?: string) => {
    const q = (questionToAsk ?? draft).trim()
    if (!q || !activeWorkspaceId || isAsking) return

    const userMessage: BrainMessage = { role: "user", content: q }
    setConversation((prev) => [...prev, userMessage])
    setDraft("")
    setIsAsking(true)
    setError(null)

    try {
      const res = await askBrain(activeWorkspaceId, q, activeConversationId)
      if (res.success) {
        if (res.conversationId) {
          setActiveConversationId(res.conversationId)
          fetchConversations(activeWorkspaceId)
        }
        const assistantMessage: BrainMessage = {
          role: "assistant",
          content: res.content,
          citations: res.citations,
          confidence: res.confidence ?? undefined,
          elapsed: res.elapsed,
        }
        setConversation((prev) => [...prev, assistantMessage])
        setContextFiles(res.contextFiles ?? [])

        if (res.citations && res.citations.length > 0) {
          setSelected(res.citations[0])
        }
      } else {
        const assistantMessage: BrainMessage = {
          role: "assistant",
          content: res.errorMessage ?? "An error occurred while answering your question.",
        }
        setConversation((prev) => [...prev, assistantMessage])
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to get an answer from Project Brain."
      const assistantMessage: BrainMessage = {
        role: "assistant",
        content: `Error: ${msg}`,
      }
      setConversation((prev) => [...prev, assistantMessage])
    } finally {
      setIsAsking(false)
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault()
      handleAsk()
    }
  }

  const indexSteps = status?.steps ?? []
  const sourceGroups = status?.sourceGroups ?? []
  const suggestedQuestions = status?.suggestedQuestions ?? []

  const isReady = status?.state === "ready"
  const isUnindexed = status?.state === "unindexed"
  const isStale = status?.state === "stale"

  return (
    <div className="mx-auto grid h-full max-w-[1720px] grid-cols-1 lg:h-[calc(100vh-56px)] lg:max-h-[calc(100vh-56px)] lg:grid-cols-[276px_minmax(0,1fr)_440px] xl:grid-cols-[276px_minmax(0,1fr)_520px] lg:overflow-hidden">
      {/* LEFT — conversations & knowledge index */}
      <aside className="flex min-h-0 min-w-0 flex-col overflow-y-auto border-b border-border p-5 lg:border-b-0 lg:border-r space-y-5">
        {/* Conversations list */}
        <div>
          <div className="flex items-center justify-between mb-2">
            <div className="tech-label">Chats</div>
            <Button
              variant="subtle"
              size="sm"
              className="h-6 gap-1 px-2 text-[11px]"
              onClick={handleNewChat}
            >
              <Plus className="h-3 w-3" />
              New chat
            </Button>
          </div>

          <div className="space-y-1 max-h-48 overflow-y-auto pr-1">
            {conversations.length === 0 ? (
              <div className="rounded-[var(--radius-md)] border border-dashed border-border px-3 py-3 text-center text-[11px] text-subtle-foreground">
                No past chats in this workspace.
              </div>
            ) : (
              conversations.map((c) => {
                const isActive = c.id === activeConversationId
                return (
                  <div
                    key={c.id}
                    onClick={() => handleSelectConversation(c.id)}
                    className={cn(
                      "group flex items-center justify-between gap-2 rounded-[var(--radius-md)] border px-2.5 py-1.5 text-left cursor-pointer transition-colors text-[11.5px]",
                      isActive
                        ? "border-primary-ring bg-primary-soft text-primary font-medium"
                        : "border-border bg-surface hover:bg-surface-2 text-foreground",
                    )}
                  >
                    <div className="flex items-center gap-1.5 min-w-0 flex-1">
                      <MessageSquare className={cn("h-3 w-3 shrink-0", isActive ? "text-primary" : "text-subtle-foreground")} />
                      <span className="truncate" title={c.title}>{c.title}</span>
                    </div>
                    <button
                      onClick={(e) => handleDeleteConversation(e, c.id)}
                      title="Delete chat"
                      className="opacity-0 group-hover:opacity-100 hover:text-danger text-subtle-foreground p-0.5 rounded transition-opacity"
                    >
                      <Trash2 className="h-3 w-3" />
                    </button>
                  </div>
                )
              })
            )}
          </div>
        </div>

        <div>
          <div className="flex items-center justify-between mb-2.5">
            <div className="tech-label">Knowledge index</div>
            {activeWorkspaceId && (
              <button
                onClick={handleIndex}
                disabled={isIndexing}
                title="Reindex workspace"
                className="text-subtle-foreground hover:text-foreground transition-colors p-1 rounded"
              >
                <RefreshCw className={cn("h-3.5 w-3.5", isIndexing && "animate-spin text-primary")} />
              </button>
            )}
          </div>
          <div className="rounded-[var(--radius-lg)] border border-border bg-surface p-3.5 shadow-[var(--shadow-sm)]">
            <div className="flex items-center gap-2">
              <div className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
                <Boxes className="h-4 w-4" />
              </div>
              <div className="min-w-0">
                <div className="truncate font-mono text-[12.5px] font-medium text-foreground" title={repoFullName}>{repoFullName}</div>
                <div className="font-mono text-[10.5px] text-subtle-foreground">
                  {status ? `${status.totalChunks} chunks indexed` : "C# · TypeScript"}
                </div>
              </div>
            </div>

            <div className="mt-3 grid grid-cols-3 gap-2">
              {[
                { icon: FileCode2, label: "Files", value: status?.totalFiles ?? 0 },
                { icon: Braces, label: "Types", value: status?.totalTypes ?? 0 },
                { icon: Hash, label: "Symbols", value: (status?.totalSymbols ?? 0).toLocaleString() },
              ].map((s) => (
                <div key={s.label} className="rounded-[var(--radius-md)] border border-border bg-inset px-2 py-2 text-center">
                  <s.icon className="mx-auto h-3.5 w-3.5 text-subtle-foreground" />
                  <div className="mt-1 font-mono text-[12px] font-semibold text-foreground">{s.value}</div>
                  <div className="tech-label text-[8.5px]">{s.label}</div>
                </div>
              ))}
            </div>

            <div className={cn(
              "mt-3 flex items-center justify-between rounded-[var(--radius-md)] border px-2.5 py-1.5",
              isReady
                ? "border-success/25 bg-success-soft"
                : isStale
                  ? "border-warning/25 bg-warning-soft"
                  : "border-border bg-surface-2",
            )}>
              <span className={cn(
                "flex items-center gap-1.5 text-[11px] font-medium",
                isReady ? "text-success" : isStale ? "text-warning" : "text-muted-foreground",
              )}>
                <StatusDot tone={isReady ? "green" : isStale ? "amber" : "gray"} />
                {status?.engine ?? "Roslyn workspace analysis"}
              </span>
              <span className={cn(
                "font-mono text-[10px]",
                isReady ? "text-success/80" : isStale ? "text-warning/80" : "text-subtle-foreground",
              )}>
                {status?.lastIndexedRelative ?? (isUnindexed ? "Unindexed" : "Never")}
              </span>
            </div>

            {(isUnindexed || isStale) && (
              <div className="mt-2.5">
                <Button
                  variant={isUnindexed ? "primary" : "subtle"}
                  size="sm"
                  className="w-full text-[11.5px] h-7"
                  onClick={handleIndex}
                  disabled={isIndexing || !activeWorkspaceId}
                >
                  {isIndexing ? (
                    <>
                      <Loader2 className="h-3 w-3 animate-spin" />
                      Indexing repository…
                    </>
                  ) : isUnindexed ? (
                    "Index repository now"
                  ) : (
                    "Update stale index"
                  )}
                </Button>
              </div>
            )}

            <ol className="mt-3 space-y-1">
              {indexSteps.map((st) => (
                <li key={st.label} className="flex items-center gap-2 text-[11.5px] text-muted-foreground">
                  <Check className={cn("h-3 w-3 shrink-0", st.done ? "text-success" : "text-subtle-foreground opacity-40")} />
                  <span className={cn(!st.done && "opacity-60")}>{st.label}</span>
                </li>
              ))}
            </ol>
          </div>
        </div>

        <div>
          <div className="tech-label mb-2">Indexed sources</div>
          <div className="space-y-1.5">
            {sourceGroups.length === 0 ? (
              <div className="rounded-[var(--radius-md)] border border-dashed border-border px-3 py-4 text-center text-[11.5px] text-subtle-foreground">
                {isStatusLoading ? "Loading sources…" : "No indexed sources yet."}
              </div>
            ) : (
              sourceGroups.map((g) => {
                const tone = getLayerTone(g.layer)
                return (
                  <div
                    key={g.project}
                    title={`${g.project} (${g.layer}) — ${g.files} files, ${g.symbols.toLocaleString()} symbols`}
                    className="flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-2"
                  >
                    <StatusDot tone={tone} />
                    <div className="min-w-0 flex-1">
                      <div className="truncate font-mono text-[11.5px] font-medium text-foreground" title={g.project}>{g.project}</div>
                      <div className="font-mono text-[10px] text-subtle-foreground">
                        {g.files} files · {g.symbols.toLocaleString()} symbols
                      </div>
                    </div>
                    <Badge tone={tone}>{g.layer}</Badge>
                  </div>
                )
              })
            )}
          </div>
        </div>
      </aside>

      {/* CENTER — conversation */}
      <section className="flex min-h-0 min-w-0 flex-1 flex-col lg:border-r lg:border-border">
        <div className="shrink-0 border-b border-border px-6 py-4">
          <div className="tech-label mb-1">Project Brain</div>
          <h1 className="text-[18px] font-semibold tracking-tight text-foreground">Ask the codebase anything</h1>
          <p className="mt-1 max-w-2xl text-[13px] leading-relaxed text-muted-foreground text-pretty">
            Semantic Q&amp;A grounded in the Roslyn index. Every answer cites the exact files and line ranges it drew
            from — select a source to inspect it. No hallucinated APIs.
          </p>
        </div>

        <div className="min-h-0 flex-1 space-y-6 overflow-y-auto px-6 py-5">
          {conversation.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 text-center">
              <div className="flex h-12 w-12 items-center justify-center rounded-[var(--radius-lg)] bg-primary-soft text-primary mb-3">
                <Sparkles className="h-6 w-6" />
              </div>
              <h3 className="text-[14px] font-semibold text-foreground">Ready to explore {repoFullName}</h3>
              <p className="mt-1 max-w-md text-[12.5px] text-muted-foreground text-pretty">
                Ask architectural questions, trace business logic, locate endpoint handlers, or explore data models.
              </p>
            </div>
          ) : (
            conversation.map((m, i) => (
              <Message key={i} msg={m} selectedKey={selectedKey} onSelect={setSelected} />
            ))
          )}

          {isAsking && (
            <div className="flex gap-3">
              <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
                <Sparkles className="h-3.5 w-3.5 animate-pulse" />
              </div>
              <div className="min-w-0 flex-1 pt-1">
                <div className="flex items-center gap-2">
                  <span className="text-[12px] font-semibold text-foreground">Project Brain</span>
                  <span className="flex items-center gap-1 text-[11px] text-subtle-foreground font-mono">
                    <Loader2 className="h-3 w-3 animate-spin text-primary" />
                    Retrieving grounded context…
                  </span>
                </div>
              </div>
            </div>
          )}

          {suggestedQuestions.length > 0 && (
            <div className="flex gap-3 pt-1">
              <div className="w-7 shrink-0" aria-hidden="true" />
              <div className="min-w-0 flex-1">
                <div className="tech-label mb-2">Try asking</div>
                <div className="flex flex-wrap items-center justify-start gap-1.5">
                  {suggestedQuestions.map((q) => (
                    <button
                      key={q}
                      onClick={() => {
                        setDraft(q)
                        handleAsk(q)
                      }}
                      disabled={isAsking}
                      title={q}
                      className="rounded-full border border-border bg-surface px-3 py-1.5 text-[12px] text-muted-foreground transition-colors hover:border-primary-ring/60 hover:bg-primary-soft hover:text-primary disabled:opacity-50 text-left"
                    >
                      {q}
                    </button>
                  ))}
                </div>
              </div>
            </div>
          )}
        </div>

        {/* composer */}
        <div className="shrink-0 border-t border-border bg-canvas/60 p-4">
          <div className="mx-auto flex max-w-3xl items-end gap-2 rounded-[var(--radius-lg)] border border-border-strong bg-surface p-2 shadow-[var(--shadow-sm)] focus-within:border-primary-ring">
            <Search className="mb-2 ml-1.5 h-4 w-4 shrink-0 text-subtle-foreground" />
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={handleKeyDown}
              disabled={isAsking || !activeWorkspaceId}
              rows={1}
              placeholder={activeWorkspaceId ? "Ask about architecture, data flow, a specific class…" : "Please select a workspace first…"}
              className="flex-1 resize-none bg-transparent py-1.5 text-[13.5px] leading-relaxed text-foreground outline-none placeholder:text-subtle-foreground disabled:opacity-50"
            />
            <Button
              variant="primary"
              size="sm"
              onClick={() => handleAsk()}
              disabled={!draft.trim() || isAsking || !activeWorkspaceId}
            >
              {isAsking ? (
                <Loader2 className="h-3 w-3 animate-spin" />
              ) : (
                <>
                  Ask
                  <CornerDownLeft className="h-3 w-3" />
                </>
              )}
            </Button>
          </div>
          <div className="mx-auto mt-2 flex max-w-3xl items-center gap-1.5 font-mono text-[10.5px] text-subtle-foreground">
            <ShieldCheck className="h-3 w-3 text-success" />
            Answers are constrained to symbols in the compiled workspace.
          </div>
        </div>
      </section>

      {/* RIGHT — source inspector */}
      <aside className="flex min-h-0 min-w-0 flex-col overflow-y-auto p-4 lg:p-5">
        <div className="tech-label mb-3 flex items-center gap-1.5">
          <FileCode2 className="h-3 w-3" />
          Source inspector
        </div>

        {selected ? (
          <div className="min-w-0 rounded-[var(--radius-lg)] border border-border bg-surface p-3.5 shadow-[var(--shadow-sm)]">
            <div className="flex items-center gap-2">
              <div className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary shrink-0">
                <FileCode2 className="h-4 w-4" />
              </div>
              <div className="min-w-0 flex-1">
                <div className="truncate text-[13.5px] font-semibold text-foreground" title={selected.file}>{selected.file}</div>
                <div className="truncate font-mono text-[10px] text-subtle-foreground" title={selected.path}>{selected.path}</div>
              </div>
            </div>

            <div className="mt-3 flex flex-wrap items-center gap-1.5">
              <Badge tone="blue" mono>
                {selected.lines}
              </Badge>
              {selected.symbol && (
                <span className="truncate font-mono text-[11px] text-muted-foreground" title={selected.symbol}>{selected.symbol}</span>
              )}
            </div>

            <div className="mt-3">
              <SourcePreview citation={selected} />
            </div>

            <div className="mt-3 flex items-center gap-2 rounded-[var(--radius-md)] border border-success/25 bg-success-soft px-2.5 py-2">
              <ShieldCheck className="h-3.5 w-3.5 shrink-0 text-success" />
              <span className="text-[11.5px] leading-snug text-foreground">
                Verified against the compiled symbol graph — this snippet is present in the current index.
              </span>
            </div>

            <div className="mt-3 flex gap-2">
              <Button variant="default" size="sm" className="flex-1" onClick={() => {}}>
                <Link2 className="h-3.5 w-3.5" />
                Open in editor
              </Button>
              <Button variant="subtle" size="sm" className="flex-1" onClick={() => {}}>
                <Database className="h-3.5 w-3.5" />
                Find references
              </Button>
            </div>
          </div>
        ) : (
          <div className="rounded-[var(--radius-lg)] border border-dashed border-border-strong bg-surface-2 p-6 text-center">
            <FileCode2 className="mx-auto h-5 w-5 text-subtle-foreground" />
            <p className="mt-2 text-[12px] text-muted-foreground">Select a cited source to preview it here.</p>
          </div>
        )}

        <div className="tech-label mb-2 mt-5">Context used</div>
        <div className="space-y-1.5">
          {contextFiles.length === 0 ? (
            <div className="rounded-[var(--radius-md)] border border-dashed border-border px-3 py-4 text-center text-[11.5px] text-subtle-foreground">
              No context files used yet.
            </div>
          ) : (
            contextFiles.map((f) => (
              <div key={f.file} className="rounded-[var(--radius-md)] border border-border bg-surface p-2.5" title={f.path}>
                <div className="flex items-center gap-2">
                  <FileCode2 className="h-3.5 w-3.5 text-subtle-foreground" />
                  <span className="truncate text-[12px] font-medium text-foreground" title={f.file}>{f.file}</span>
                  <span className="ml-auto font-mono text-[10.5px] text-muted-foreground">{f.relevance}%</span>
                </div>
                <div className="mt-1 truncate font-mono text-[10px] text-subtle-foreground" title={f.path}>{f.path}</div>
                <div className="mt-1.5 h-1 w-full overflow-hidden rounded-full bg-surface-3">
                  <div className="h-full rounded-full bg-primary" style={{ width: `${f.relevance}%` }} />
                </div>
              </div>
            ))
          )}
        </div>
      </aside>
    </div>
  )
}
