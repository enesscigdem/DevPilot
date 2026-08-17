import { useMemo, useState } from "react"
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
} from "lucide-react"
import { Button, Badge, StatusDot } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"
import {
  brainConversation,
  brainSuggested,
  brainContextFiles,
  brainSourceGroups,
  repository,
  indexer,
  type BrainMessage,
  type BrainCitation,
} from "@/data/mock"
import { useWorkspace } from "@/lib/workspace"

function citationKey(c: BrainCitation) {
  return `${c.path}#${c.lines}`
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
      <div className="min-w-0 flex-1">
        <div className="mb-1.5 flex items-center gap-2">
          <span className="text-[12px] font-semibold text-foreground">Project Brain</span>
          {msg.confidence !== undefined && (
            <span className="flex items-center gap-1 font-mono text-[10.5px] text-subtle-foreground">
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
        <p className="text-[13.5px] leading-relaxed text-foreground text-pretty">{msg.content}</p>

        {msg.citations && (
          <div className="mt-3">
            <div className="tech-label mb-1.5 flex items-center gap-1.5">
              <Quote className="h-3 w-3" />
              Grounded in {msg.citations.length} sources
            </div>
            <div className="grid gap-1.5 sm:grid-cols-2">
              {msg.citations.map((c) => {
                const active = selectedKey === citationKey(c)
                return (
                  <button
                    key={citationKey(c)}
                    onClick={() => onSelect(c)}
                    className={cn(
                      "group flex items-center gap-2 rounded-[var(--radius-md)] border px-2.5 py-2 text-left transition-colors",
                      active
                        ? "border-primary-ring bg-primary-soft"
                        : "border-border bg-surface hover:border-primary-ring/60 hover:bg-surface-2",
                    )}
                  >
                    <FileCode2 className={cn("h-3.5 w-3.5 shrink-0", active ? "text-primary" : "text-subtle-foreground")} />
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-1.5">
                        <span className={cn("truncate text-[12px] font-medium", active ? "text-primary" : "text-foreground")}>
                          {c.file}
                        </span>
                        <span className="font-mono text-[10px] text-subtle-foreground">{c.lines}</span>
                      </div>
                      {c.symbol && (
                        <div className="truncate font-mono text-[10px] text-subtle-foreground">{c.symbol}</div>
                      )}
                    </div>
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
  const lines = citation.snippet.split("\n")
  const start = citation.startLine ?? 1
  return (
    <div className="overflow-hidden rounded-[var(--radius-md)] border border-border bg-inset">
      <div className="flex items-center gap-2 border-b border-border bg-surface-2 px-3 py-1.5">
        <FileCode2 className="h-3.5 w-3.5 text-primary" />
        <span className="text-[12px] font-medium text-foreground">{citation.file}</span>
        <span className="ml-auto font-mono text-[10px] text-subtle-foreground">{citation.lang ?? "cs"}</span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full border-collapse font-mono text-[11.5px] leading-relaxed">
          <tbody>
            {lines.map((ln, i) => (
              <tr key={i} className="align-top">
                <td className="select-none whitespace-nowrap border-r border-border/70 px-2.5 py-0.5 text-right text-subtle-foreground">
                  {start + i}
                </td>
                <td className="whitespace-pre px-3 py-0.5 text-muted-foreground">{ln || " "}</td>
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
  const { activeWorkspace } = useWorkspace()
  const [draft, setDraft] = useState("")

  const repoFullName = activeWorkspace
    ? `${activeWorkspace.owner}/${activeWorkspace.repository}`
    : repository.fullName

  const allCitations = useMemo(
    () => brainConversation.flatMap((m) => m.citations ?? []),
    [],
  )
  const [selected, setSelected] = useState<BrainCitation>(allCitations[0])
  const selectedKey = selected ? citationKey(selected) : null

  const indexSteps = indexer.steps

  return (
    <div className="mx-auto grid min-h-[calc(100vh-56px)] max-w-[1600px] grid-cols-1 lg:grid-cols-[276px_minmax(0,1fr)_384px]">
      {/* LEFT — knowledge index */}
      <aside className="border-b border-border p-5 lg:border-b-0 lg:border-r">
        <div className="tech-label mb-2.5">Knowledge index</div>
        <div className="rounded-[var(--radius-lg)] border border-border bg-surface p-3.5 shadow-[var(--shadow-sm)]">
          <div className="flex items-center gap-2">
            <div className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
              <Boxes className="h-4 w-4" />
            </div>
            <div className="min-w-0">
              <div className="truncate font-mono text-[12.5px] font-medium text-foreground">{repoFullName}</div>
              <div className="font-mono text-[10.5px] text-subtle-foreground">{repository.language}</div>
            </div>
          </div>

          <div className="mt-3 grid grid-cols-3 gap-2">
            {[
              { icon: FileCode2, label: "Files", value: repository.files },
              { icon: Braces, label: "Types", value: indexer.types },
              { icon: Hash, label: "Symbols", value: indexer.symbols.toLocaleString() },
            ].map((s) => (
              <div key={s.label} className="rounded-[var(--radius-md)] border border-border bg-inset px-2 py-2 text-center">
                <s.icon className="mx-auto h-3.5 w-3.5 text-subtle-foreground" />
                <div className="mt-1 font-mono text-[12px] font-semibold text-foreground">{s.value}</div>
                <div className="tech-label text-[8.5px]">{s.label}</div>
              </div>
            ))}
          </div>

          <div className="mt-3 flex items-center justify-between rounded-[var(--radius-md)] border border-success/25 bg-success-soft px-2.5 py-1.5">
            <span className="flex items-center gap-1.5 text-[11px] font-medium text-success">
              <StatusDot tone="green" />
              {indexer.engine}
            </span>
            <span className="font-mono text-[10px] text-success/80">{indexer.lastRun}</span>
          </div>

          <ol className="mt-3 space-y-1">
            {indexSteps.map((st) => (
              <li key={st.label} className="flex items-center gap-2 text-[11.5px] text-muted-foreground">
                <Check className="h-3 w-3 shrink-0 text-success" />
                {st.label}
              </li>
            ))}
          </ol>
        </div>

        <div className="tech-label mb-2 mt-5">Indexed sources</div>
        <div className="space-y-1.5">
          {brainSourceGroups.map((g) => (
            <div
              key={g.project}
              className="flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-2"
            >
              <StatusDot tone={g.tone} />
              <div className="min-w-0 flex-1">
                <div className="truncate font-mono text-[11.5px] font-medium text-foreground">{g.project}</div>
                <div className="font-mono text-[10px] text-subtle-foreground">
                  {g.files} files · {g.symbols.toLocaleString()} symbols
                </div>
              </div>
              <Badge tone={g.tone}>{g.layer}</Badge>
            </div>
          ))}
        </div>
      </aside>

      {/* CENTER — conversation */}
      <section className="flex min-h-[calc(100vh-56px)] flex-col lg:border-r lg:border-border">
        <div className="border-b border-border px-6 py-4">
          <div className="tech-label mb-1">Project Brain</div>
          <h1 className="text-[18px] font-semibold tracking-tight text-foreground">Ask the codebase anything</h1>
          <p className="mt-1 max-w-2xl text-[13px] leading-relaxed text-muted-foreground text-pretty">
            Semantic Q&amp;A grounded in the Roslyn index. Every answer cites the exact files and line ranges it drew
            from — select a source to inspect it. No hallucinated APIs.
          </p>
        </div>

        <div className="flex-1 space-y-6 overflow-y-auto px-6 py-5">
          {brainConversation.map((m, i) => (
            <Message key={i} msg={m} selectedKey={selectedKey} onSelect={setSelected} />
          ))}

          <div className="pt-1">
            <div className="tech-label mb-2">Try asking</div>
            <div className="flex flex-wrap gap-1.5">
              {brainSuggested.map((q) => (
                <button
                  key={q}
                  onClick={() => setDraft(q)}
                  className="rounded-full border border-border bg-surface px-3 py-1.5 text-[12px] text-muted-foreground transition-colors hover:border-primary-ring/60 hover:bg-primary-soft hover:text-primary"
                >
                  {q}
                </button>
              ))}
            </div>
          </div>
        </div>

        {/* composer */}
        <div className="border-t border-border bg-canvas/60 p-4">
          <div className="mx-auto flex max-w-3xl items-end gap-2 rounded-[var(--radius-lg)] border border-border-strong bg-surface p-2 shadow-[var(--shadow-sm)] focus-within:border-primary-ring">
            <Search className="mb-2 ml-1.5 h-4 w-4 shrink-0 text-subtle-foreground" />
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              rows={1}
              placeholder="Ask about architecture, data flow, a specific class…"
              className="flex-1 resize-none bg-transparent py-1.5 text-[13.5px] leading-relaxed text-foreground outline-none placeholder:text-subtle-foreground"
            />
            <Button variant="primary" size="sm" disabled={!draft.trim()}>
              Ask
              <CornerDownLeft className="h-3 w-3" />
            </Button>
          </div>
          <div className="mx-auto mt-2 flex max-w-3xl items-center gap-1.5 font-mono text-[10.5px] text-subtle-foreground">
            <ShieldCheck className="h-3 w-3 text-success" />
            Answers are constrained to symbols in the compiled workspace.
          </div>
        </div>
      </section>

      {/* RIGHT — source inspector */}
      <aside className="flex min-h-[calc(100vh-56px)] flex-col overflow-y-auto p-5">
        <div className="tech-label mb-3 flex items-center gap-1.5">
          <FileCode2 className="h-3 w-3" />
          Source inspector
        </div>

        {selected ? (
          <div className="rounded-[var(--radius-lg)] border border-border bg-surface p-4 shadow-[var(--shadow-sm)]">
            <div className="flex items-center gap-2">
              <div className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] bg-primary-soft text-primary">
                <FileCode2 className="h-4 w-4" />
              </div>
              <div className="min-w-0">
                <div className="truncate text-[13.5px] font-semibold text-foreground">{selected.file}</div>
                <div className="truncate font-mono text-[10px] text-subtle-foreground">{selected.path}</div>
              </div>
            </div>

            <div className="mt-3 flex flex-wrap items-center gap-1.5">
              <Badge tone="blue" mono>
                {selected.lines}
              </Badge>
              {selected.symbol && (
                <span className="truncate font-mono text-[11px] text-muted-foreground">{selected.symbol}</span>
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
              <Button variant="default" size="sm" className="flex-1">
                <Link2 className="h-3.5 w-3.5" />
                Open in editor
              </Button>
              <Button variant="subtle" size="sm" className="flex-1">
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
          {brainContextFiles.map((f) => (
            <div key={f.file} className="rounded-[var(--radius-md)] border border-border bg-surface p-2.5">
              <div className="flex items-center gap-2">
                <FileCode2 className="h-3.5 w-3.5 text-subtle-foreground" />
                <span className="truncate text-[12px] font-medium text-foreground">{f.file}</span>
                <span className="ml-auto font-mono text-[10.5px] text-muted-foreground">{f.relevance}%</span>
              </div>
              <div className="mt-1 truncate font-mono text-[10px] text-subtle-foreground">{f.path}</div>
              <div className="mt-1.5 h-1 w-full overflow-hidden rounded-full bg-surface-3">
                <div className="h-full rounded-full bg-primary" style={{ width: `${f.relevance}%` }} />
              </div>
            </div>
          ))}
        </div>
      </aside>
    </div>
  )
}
