import { useCallback, useEffect, useMemo, useState } from "react"
import { useTranslation } from "react-i18next"
import { AlertCircle, CheckCircle2, ExternalLink, Loader2, Search, Trash2, X } from "lucide-react"
import {
  createTrackerConnection,
  deleteTrackerConnection,
  getTrackerConnections,
  importTrackerIssues,
  searchTrackerIssues,
} from "@/api"
import { Badge, Button } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"
import type { ImportIssuesResponse, TrackerConnection, TrackerIssue } from "@/types"

const fieldClass =
  "h-9 w-full rounded-[var(--radius-md)] border border-border bg-surface px-3 text-sm text-foreground outline-none placeholder:text-subtle-foreground focus:border-border-strong"

interface JiraImportModalProps {
  workspaceId: string | null
  onClose: () => void
  onImported: () => void
}

function messageOf(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

export function JiraImportModal({ workspaceId, onClose, onImported }: JiraImportModalProps) {
  const { t } = useTranslation()

  const [connections, setConnections] = useState<TrackerConnection[] | null>(null)
  const [activeId, setActiveId] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [site, setSite] = useState("")
  const [email, setEmail] = useState("")
  const [token, setToken] = useState("")
  const [connecting, setConnecting] = useState(false)
  const [connectError, setConnectError] = useState<string | null>(null)

  const [query, setQuery] = useState("")
  const [issues, setIssues] = useState<TrackerIssue[] | null>(null)
  const [searching, setSearching] = useState(false)
  const [searchError, setSearchError] = useState<string | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())

  const [importing, setImporting] = useState(false)
  const [importError, setImportError] = useState<string | null>(null)
  const [result, setResult] = useState<ImportIssuesResponse | null>(null)

  const active = useMemo(() => connections?.find((c) => c.id === activeId) ?? null, [connections, activeId])

  useEffect(() => {
    getTrackerConnections()
      .then((list) => {
        setConnections(list)
        setActiveId(list[0]?.id ?? null)
        setAdding(list.length === 0)
      })
      .catch((err) => {
        setConnections([])
        setAdding(true)
        setLoadError(messageOf(err, t("tasks.jira.errLoad")))
      })
  }, [t])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose()
    }
    window.addEventListener("keydown", onKey)
    return () => window.removeEventListener("keydown", onKey)
  }, [onClose])

  const runSearch = useCallback(
    async (connectionId: string, text: string) => {
      setSearching(true)
      setSearchError(null)
      setSelected(new Set())
      try {
        setIssues(await searchTrackerIssues(connectionId, text, workspaceId))
      } catch (err) {
        setIssues(null)
        setSearchError(messageOf(err, t("tasks.jira.errSearch")))
      } finally {
        setSearching(false)
      }
    },
    [workspaceId, t],
  )

  // Show the person's own open issues as soon as a connection is picked.
  useEffect(() => {
    if (activeId && !adding) {
      void runSearch(activeId, "")
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeId, adding])

  const handleConnect = async () => {
    setConnecting(true)
    setConnectError(null)
    try {
      const created = await createTrackerConnection({
        provider: "Jira",
        baseUrl: site.trim(),
        email: email.trim() || undefined,
        token: token.trim(),
      })
      setConnections((prev) => [...(prev ?? []), created])
      setActiveId(created.id)
      setAdding(false)
      setSite("")
      setEmail("")
      setToken("")
    } catch (err) {
      setConnectError(messageOf(err, t("tasks.jira.errConnect")))
    } finally {
      setConnecting(false)
    }
  }

  const handleRemove = async () => {
    if (!active) return
    if (!window.confirm(`${t("tasks.jira.removeConnection")}\n\n${t("tasks.jira.removeConfirm")}`)) return
    try {
      await deleteTrackerConnection(active.id)
      const rest = (connections ?? []).filter((c) => c.id !== active.id)
      setConnections(rest)
      setActiveId(rest[0]?.id ?? null)
      setAdding(rest.length === 0)
      setIssues(null)
    } catch (err) {
      setSearchError(messageOf(err, t("tasks.jira.errConnect")))
    }
  }

  const handleImport = async () => {
    if (!active || !workspaceId || selected.size === 0) return
    setImporting(true)
    setImportError(null)
    try {
      const response = await importTrackerIssues(active.id, workspaceId, Array.from(selected))
      setResult(response)
      if (response.imported > 0) onImported()
    } catch (err) {
      setImportError(messageOf(err, t("tasks.jira.errImport")))
    } finally {
      setImporting(false)
    }
  }

  const importable = (issues ?? []).filter((i) => !i.alreadyImported)
  const toggle = (key: string) =>
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center px-4 pt-[8vh]" onMouseDown={onClose}>
      <div className="absolute inset-0 bg-foreground/20 backdrop-blur-[2px]" />
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t("tasks.jira.title")}
        className="animate-fade-rise relative flex max-h-[80vh] w-full max-w-[720px] flex-col overflow-hidden rounded-[var(--radius-lg)] border border-border-strong bg-surface shadow-[var(--shadow-lg)]"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3 border-b border-border px-5 py-4">
          <div>
            <h2 className="text-base font-semibold text-foreground">{t("tasks.jira.title")}</h2>
            <p className="mt-0.5 text-xs text-subtle-foreground">{t("tasks.jira.help")}</p>
          </div>
          <button type="button" onClick={onClose} className="rounded p-1 text-subtle-foreground hover:bg-surface-secondary hover:text-foreground" aria-label="Close">
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4">
          {connections === null ? (
            <div className="flex items-center gap-2 py-8 text-sm text-subtle-foreground">
              <Loader2 className="h-4 w-4 animate-spin" />
            </div>
          ) : result ? (
            <ImportSummary result={result} />
          ) : adding ? (
            <div className="space-y-3">
              <div>
                <h3 className="text-sm font-semibold text-foreground">{t("tasks.jira.connectTitle")}</h3>
                <p className="mt-0.5 text-xs text-subtle-foreground">{t("tasks.jira.connectHelp")}</p>
              </div>
              {loadError && <ErrorLine message={loadError} />}
              {connectError && <ErrorLine message={connectError} />}
              <div>
                <label className="tech-label mb-1.5 block">{t("tasks.jira.siteLabel")}</label>
                <input value={site} onChange={(e) => setSite(e.target.value)} placeholder={t("tasks.jira.sitePlaceholder")} disabled={connecting} className={fieldClass} autoFocus />
              </div>
              <div>
                <label className="tech-label mb-1.5 block">{t("tasks.jira.emailLabel")}</label>
                <input value={email} onChange={(e) => setEmail(e.target.value)} disabled={connecting} autoComplete="off" className={fieldClass} />
              </div>
              <div>
                <label className="tech-label mb-1.5 block">{t("tasks.jira.tokenLabel")}</label>
                <input type="password" value={token} onChange={(e) => setToken(e.target.value)} placeholder={t("tasks.jira.tokenPlaceholder")} disabled={connecting} autoComplete="off" className={fieldClass} />
              </div>
              <div className="flex items-center justify-end gap-2 pt-1">
                {(connections?.length ?? 0) > 0 && (
                  <Button size="sm" variant="ghost" onClick={() => setAdding(false)} disabled={connecting}>
                    {t("tasks.batch.back")}
                  </Button>
                )}
                <Button size="sm" onClick={handleConnect} disabled={connecting || !site.trim() || !token.trim()}>
                  {connecting && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                  {connecting ? t("tasks.jira.connecting") : t("tasks.jira.connect")}
                </Button>
              </div>
            </div>
          ) : (
            <div className="space-y-3">
              {!workspaceId && <ErrorLine message={t("tasks.jira.needWorkspace")} />}

              <div className="flex flex-wrap items-center gap-2">
                <select
                  value={activeId ?? ""}
                  onChange={(e) => setActiveId(e.target.value)}
                  aria-label={t("tasks.jira.connection")}
                  className={cn(fieldClass, "w-auto min-w-[200px] flex-1")}
                >
                  {connections.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.displayName}
                    </option>
                  ))}
                </select>
                <Button size="sm" variant="ghost" onClick={() => setAdding(true)}>
                  {t("tasks.jira.addAnother")}
                </Button>
                <button type="button" onClick={handleRemove} className="rounded p-1.5 text-subtle-foreground hover:bg-surface-secondary hover:text-red-500" title={t("tasks.jira.removeConnection")} aria-label={t("tasks.jira.removeConnection")}>
                  <Trash2 className="h-4 w-4" />
                </button>
              </div>

              <form
                onSubmit={(e) => {
                  e.preventDefault()
                  if (activeId) void runSearch(activeId, query)
                }}
                className="flex items-center gap-2"
              >
                <div className="relative flex-1">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-subtle-foreground" />
                  <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t("tasks.jira.searchPlaceholder")} className={cn(fieldClass, "pl-9")} />
                </div>
                <Button size="sm" type="submit" disabled={searching}>
                  {searching ? t("tasks.jira.searching") : t("tasks.jira.search")}
                </Button>
              </form>
              <p className="text-xs text-subtle-foreground">{t("tasks.jira.searchHint")}</p>

              {searchError && <ErrorLine message={searchError} />}
              {importError && <ErrorLine message={importError} />}

              {issues && issues.length === 0 && !searching && (
                <p className="py-6 text-center text-sm text-subtle-foreground">{t("tasks.jira.noResults")}</p>
              )}

              {issues && issues.length > 0 && (
                <div className="space-y-2">
                  <div className="flex items-center gap-3 text-xs">
                    <button type="button" onClick={() => setSelected(new Set(importable.map((i) => i.key)))} className="font-medium text-foreground hover:underline">
                      {t("tasks.jira.selectAllNew")}
                    </button>
                    {selected.size > 0 && (
                      <button type="button" onClick={() => setSelected(new Set())} className="text-subtle-foreground hover:text-foreground hover:underline">
                        {t("tasks.jira.clearSelection")}
                      </button>
                    )}
                  </div>
                  <ul className="divide-y divide-border rounded-[var(--radius-md)] border border-border">
                    {issues.map((issue) => (
                      <li key={issue.key}>
                        <label className={cn("flex cursor-pointer items-start gap-3 px-3 py-2.5", issue.alreadyImported && "cursor-not-allowed opacity-60")}>
                          <input
                            type="checkbox"
                            className="mt-1"
                            checked={selected.has(issue.key)}
                            disabled={issue.alreadyImported || !workspaceId}
                            onChange={() => toggle(issue.key)}
                          />
                          <div className="min-w-0 flex-1">
                            <div className="flex flex-wrap items-center gap-2">
                              <span className="font-mono text-[11px] text-subtle-foreground">{issue.key}</span>
                              <span className="text-[13px] font-medium text-foreground">{issue.summary}</span>
                            </div>
                            <div className="mt-1 flex flex-wrap items-center gap-1.5">
                              {issue.issueType && <Badge tone="neutral">{issue.issueType}</Badge>}
                              {issue.status && <Badge tone="gray">{issue.status}</Badge>}
                              {issue.priority && <Badge tone="amber">{issue.priority}</Badge>}
                              {issue.alreadyImported && <Badge tone="green">{t("tasks.jira.alreadyImported")}</Badge>}
                            </div>
                          </div>
                          <a href={issue.url} target="_blank" rel="noreferrer" onClick={(e) => e.stopPropagation()} className="shrink-0 text-subtle-foreground hover:text-foreground" title={t("tasks.jira.openInJira")} aria-label={t("tasks.jira.openInJira")}>
                            <ExternalLink className="h-3.5 w-3.5" />
                          </a>
                        </label>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}
        </div>

        <div className="flex items-center justify-between gap-3 border-t border-border bg-surface-secondary/30 px-5 py-3">
          {result ? (
            <>
              <span />
              <Button size="sm" onClick={onClose}>
                {t("tasks.jira.done")}
              </Button>
            </>
          ) : (
            <>
              <span className="text-xs text-subtle-foreground">{selected.size > 0 ? t("tasks.jira.selected", { count: selected.size }) : ""}</span>
              <Button size="sm" onClick={handleImport} disabled={importing || selected.size === 0 || !workspaceId || adding}>
                {importing && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                {importing ? t("tasks.jira.importing") : t("tasks.jira.import", { count: Math.max(selected.size, 1) })}
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
  )
}

function ErrorLine({ message }: { message: string }) {
  return (
    <div className="flex items-start gap-2 rounded-[var(--radius-md)] border border-red-500/20 bg-red-500/10 p-3 text-xs text-red-500">
      <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
      <span>{message}</span>
    </div>
  )
}

function ImportSummary({ result }: { result: ImportIssuesResponse }) {
  const { t } = useTranslation()
  const failures = result.items.filter((i) => i.outcome === "Failed")

  return (
    <div className="space-y-3 py-2">
      <div className="flex items-center gap-2 text-sm font-medium text-foreground">
        <CheckCircle2 className="h-5 w-5 text-emerald-500" />
        {t("tasks.jira.importedSummary", { count: result.imported })}
      </div>
      {result.alreadyImported > 0 && <p className="text-xs text-subtle-foreground">{t("tasks.jira.skippedSummary", { count: result.alreadyImported })}</p>}
      {result.failed > 0 && (
        <div className="space-y-1.5">
          <p className="text-xs font-medium text-red-500">{t("tasks.jira.failedSummary", { count: result.failed })}</p>
          <ul className="space-y-1 text-xs text-subtle-foreground">
            {failures.map((f) => (
              <li key={f.key}>
                <span className="font-mono">{f.key}</span>: {f.message}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
