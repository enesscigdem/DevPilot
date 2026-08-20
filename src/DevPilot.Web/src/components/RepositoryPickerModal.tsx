import { useEffect, useMemo, useRef, useState } from "react"
import {
  Search,
  GitFork,
  Check,
  Lock,
  Globe,
  Plus,
  RefreshCw,
  ExternalLink,
  ArrowLeft,
  Loader2,
  AlertCircle,
  FolderGit2,
} from "lucide-react"
import { useWorkspace } from "@/lib/workspace"
import {
  getGitHubStatus,
  getGitHubConnectUrl,
  getGitHubRepositories,
  getGitHubBranches,
} from "@/api"
import type {
  GitHubConnectionStatus,
  GitHubDiscoveredRepository,
  GitHubBranch,
  RepositoryWorkspace,
} from "@/types"
import { Kbd } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"

interface RepositoryPickerModalProps {
  open: boolean
  onClose: () => void
  returnUrl?: string
}

export function RepositoryPickerModal({ open, onClose, returnUrl }: RepositoryPickerModalProps) {
  const { workspaces, activeWorkspaceId, selectWorkspace, connectWorkspace, refreshWorkspaces } = useWorkspace()

  const [step, setStep] = useState<"list" | "branch">("list")
  const [query, setQuery] = useState("")
  const [activeIdx, setActiveIdx] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)

  // GitHub Data state
  const [ghStatus, setGhStatus] = useState<GitHubConnectionStatus | null>(null)
  const [ghRepos, setGhRepos] = useState<GitHubDiscoveredRepository[]>([])
  const [isLoadingRepos, setIsLoadingRepos] = useState(false)
  const [ghError, setGhError] = useState<string | null>(null)

  // Step 2 (Branch Selection) state
  const [selectedRepo, setSelectedRepo] = useState<GitHubDiscoveredRepository | null>(null)
  const [branches, setBranches] = useState<GitHubBranch[]>([])
  const [selectedBranch, setSelectedBranch] = useState("")
  const [isLoadingBranches, setIsLoadingBranches] = useState(false)
  const [isConnecting, setIsConnecting] = useState(false)
  const [connectError, setConnectError] = useState<string | null>(null)

  // Fetch GitHub connection status and repos on open
  useEffect(() => {
    if (!open) {
      setStep("list")
      setQuery("")
      setSelectedRepo(null)
      setConnectError(null)
      return
    }

    requestAnimationFrame(() => inputRef.current?.focus())
    loadGitHubData()
  }, [open])

  const loadGitHubData = async () => {
    setIsLoadingRepos(true)
    setGhError(null)
    try {
      const status = await getGitHubStatus()
      setGhStatus(status)

      if (status.isConnected) {
        const repos = await getGitHubRepositories()
        setGhRepos(repos)
      } else {
        setGhRepos([])
      }
    } catch (err: any) {
      setGhError(err?.message || "Failed to load GitHub repositories.")
    } finally {
      setIsLoadingRepos(false)
    }
  }

  const handleConnectGitHub = async () => {
    try {
      const currentPath = returnUrl || window.location.pathname
      const { url } = await getGitHubConnectUrl(currentPath)
      window.location.href = url
    } catch (err: any) {
      setGhError(err?.message || "Failed to generate GitHub authorization URL.")
    }
  }

  // Filter connected workspaces and available repos
  const filteredConnected = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return workspaces
    return workspaces.filter(
      (w) =>
        w.owner.toLowerCase().includes(q) ||
        w.repository.toLowerCase().includes(q) ||
        w.branch.toLowerCase().includes(q) ||
        `${w.owner}/${w.repository}`.toLowerCase().includes(q)
    )
  }, [workspaces, query])

  const filteredGitHubRepos = useMemo(() => {
    const q = query.trim().toLowerCase()
    const available = ghRepos.filter((r) => !r.isConnectedToDevPilot)
    if (!q) return available
    return available.filter(
      (r) =>
        r.fullName.toLowerCase().includes(q) ||
        r.name.toLowerCase().includes(q) ||
        r.owner.toLowerCase().includes(q) ||
        (r.description && r.description.toLowerCase().includes(q))
    )
  }, [ghRepos, query])

  // Flat list for keyboard navigation in Step 1
  const flatItems = useMemo(() => {
    const items: Array<
      | { type: "connected"; data: RepositoryWorkspace }
      | { type: "github"; data: GitHubDiscoveredRepository }
    > = []

    filteredConnected.forEach((w) => items.push({ type: "connected", data: w }))
    filteredGitHubRepos.forEach((r) => items.push({ type: "github", data: r }))
    return items
  }, [filteredConnected, filteredGitHubRepos])

  useEffect(() => {
    setActiveIdx(0)
  }, [query, step])

  const handleSelectConnected = (workspaceId: string) => {
    selectWorkspace(workspaceId)
    onClose()
  }

  const handleSelectGitHubRepo = async (repo: GitHubDiscoveredRepository) => {
    setSelectedRepo(repo)
    setSelectedBranch(repo.defaultBranch || "main")
    setStep("branch")
    setConnectError(null)

    setIsLoadingBranches(true)
    try {
      const branchList = await getGitHubBranches(repo.owner, repo.name)
      setBranches(branchList)
    } catch {
      // Fallback: default branch only
      setBranches([{ name: repo.defaultBranch || "main", commitSha: "", isProtected: false }])
    } finally {
      setIsLoadingBranches(false)
    }
  }

  const handleExecuteConnect = async () => {
    if (!selectedRepo || !selectedBranch) return

    setIsConnecting(true)
    setConnectError(null)
    try {
      const newWs = await connectWorkspace({
        owner: selectedRepo.owner,
        repository: selectedRepo.name,
        branch: selectedBranch,
      })
      await refreshWorkspaces()
      selectWorkspace(newWs.id)
      onClose()
    } catch (err: any) {
      setConnectError(err?.message || "Failed to clone and index repository workspace.")
    } finally {
      setIsConnecting(false)
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (step === "branch") {
      if (e.key === "Escape") {
        setStep("list")
      }
      return
    }

    if (e.key === "ArrowDown") {
      e.preventDefault()
      setActiveIdx((a) => Math.min(a + 1, Math.max(0, flatItems.length - 1)))
    } else if (e.key === "ArrowUp") {
      e.preventDefault()
      setActiveIdx((a) => Math.max(a - 1, 0))
    } else if (e.key === "Enter") {
      e.preventDefault()
      const item = flatItems[activeIdx]
      if (item) {
        if (item.type === "connected") {
          handleSelectConnected(item.data.id)
        } else {
          handleSelectGitHubRepo(item.data)
        }
      }
    } else if (e.key === "Escape") {
      onClose()
    }
  }

  if (!open) return null

  const manageUrl = ghStatus?.installations?.[0]?.manageUrl || "https://github.com/settings/installations"

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center px-4 pt-[10vh]"
      onMouseDown={onClose}
    >
      <div className="absolute inset-0 bg-foreground/20 backdrop-blur-[2px]" />
      <div
        className="animate-fade-rise relative w-full max-w-[620px] overflow-hidden rounded-[var(--radius-lg)] border border-border-strong bg-surface shadow-[var(--shadow-lg)]"
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={handleKeyDown}
      >
        {step === "list" ? (
          <>
            {/* Search Header */}
            <div className="flex items-center gap-2.5 border-b border-border px-3.5">
              <Search className="h-4 w-4 shrink-0 text-subtle-foreground" strokeWidth={2} />
              <input
                ref={inputRef}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Search repositories across DevPilot & GitHub…"
                className="h-12 w-full bg-transparent text-sm text-foreground outline-none placeholder:text-subtle-foreground"
              />
              {isLoadingRepos && <Loader2 className="h-4 w-4 shrink-0 animate-spin text-subtle-foreground" />}
              <Kbd>Esc</Kbd>
            </div>

            {/* Error Banner */}
            {ghError && (
              <div className="flex items-center gap-2 border-b border-border bg-amber-500/10 px-4 py-2.5 text-xs text-amber-500">
                <AlertCircle className="h-4 w-4 shrink-0" />
                <span className="flex-1">{ghError}</span>
                <button
                  onClick={loadGitHubData}
                  className="rounded px-2 py-0.5 font-medium hover:bg-amber-500/20"
                >
                  Retry
                </button>
              </div>
            )}

            {/* Main Content Area */}
            <div className="max-h-[56vh] overflow-y-auto py-2">
              {/* Not connected state banner */}
              {ghStatus && !ghStatus.isConnected && (
                <div className="mx-3 my-2 rounded-[var(--radius-md)] border border-border bg-surface-secondary/50 p-4 text-center">
                  <div className="mx-auto mb-2 flex h-10 w-10 items-center justify-center rounded-full bg-surface border border-border">
                    <FolderGit2 className="h-5 w-5 text-foreground" />
                  </div>
                  <h3 className="text-sm font-semibold text-foreground">Connect GitHub Account</h3>
                  <p className="mx-auto mt-1 max-w-sm text-xs text-subtle-foreground">
                    Connect your GitHub account or organization to browse repositories, create branches, push changes, and open pull requests.
                  </p>
                  <button
                    onClick={handleConnectGitHub}
                    className="mt-3 inline-flex items-center gap-1.5 rounded-[var(--radius-md)] bg-foreground px-3.5 py-1.5 text-xs font-medium text-background transition-opacity hover:opacity-90"
                  >
                    <Plus className="h-3.5 w-3.5" />
                    Connect GitHub
                  </button>
                </div>
              )}

              {/* Section 1: Connected to DevPilot */}
              {filteredConnected.length > 0 && (
                <div className="px-2 pb-2">
                  <div className="tech-label px-2.5 py-1.5">CONNECTED TO DEVPILOT</div>
                  {filteredConnected.map((w) => {
                    const idx = flatItems.findIndex((item) => item.type === "connected" && item.data.id === w.id)
                    const isActive = idx === activeIdx
                    const isCurrent = w.id === activeWorkspaceId

                    return (
                      <button
                        key={w.id}
                        type="button"
                        onClick={() => handleSelectConnected(w.id)}
                        className={cn(
                          "group flex w-full items-center justify-between gap-3 rounded-[var(--radius-md)] px-2.5 py-2 text-left text-sm transition-colors",
                          isActive
                            ? "bg-surface-secondary text-foreground"
                            : "text-foreground/90 hover:bg-surface-secondary/60 hover:text-foreground",
                        )}
                      >
                        <div className="flex min-w-0 items-center gap-2.5">
                          <div className="flex h-6 w-6 shrink-0 items-center justify-center rounded border border-border bg-surface">
                            <FolderGit2 className="h-3.5 w-3.5 text-foreground" />
                          </div>
                          <div className="min-w-0 truncate">
                            <div className="flex items-center gap-1.5">
                              <span className="font-medium text-foreground">{w.owner}/{w.repository}</span>
                              {isCurrent && (
                                <span className="rounded bg-emerald-500/10 px-1.5 py-0.2 text-[10px] font-medium text-emerald-500">
                                  Active
                                </span>
                              )}
                            </div>
                            <div className="flex items-center gap-2 text-xs text-subtle-foreground">
                              <span className="inline-flex items-center gap-1">
                                <GitFork className="h-3 w-3" />
                                {w.branch}
                              </span>
                              <span>•</span>
                              <span>Indexed</span>
                            </div>
                          </div>
                        </div>
                        {isCurrent && <Check className="h-4 w-4 shrink-0 text-emerald-500" />}
                      </button>
                    )
                  })}
                </div>
              )}

              {/* Section 2: Available from GitHub */}
              {ghStatus?.isConnected && (
                <div className="px-2 pb-1 pt-2">
                  <div className="tech-label px-2.5 py-1.5 flex items-center justify-between">
                    <span>AVAILABLE FROM GITHUB</span>
                    {ghStatus.installations.length > 0 && (
                      <span className="text-[10px] lowercase text-subtle-foreground">
                        ({ghStatus.installations.map((i) => i.accountLogin).join(", ")})
                      </span>
                    )}
                  </div>

                  {filteredGitHubRepos.length === 0 && !isLoadingRepos && (
                    <div className="px-3 py-4 text-center text-xs text-subtle-foreground">
                      {query ? `No available GitHub repositories matching "${query}"` : "All authorized repositories are already connected."}
                    </div>
                  )}

                  {filteredGitHubRepos.map((repo) => {
                    const idx = flatItems.findIndex((item) => item.type === "github" && item.data.id === repo.id)
                    const isActive = idx === activeIdx

                    return (
                      <button
                        key={repo.id}
                        type="button"
                        onClick={() => handleSelectGitHubRepo(repo)}
                        className={cn(
                          "group flex w-full items-center justify-between gap-3 rounded-[var(--radius-md)] px-2.5 py-2 text-left text-sm transition-colors",
                          isActive
                            ? "bg-surface-secondary text-foreground"
                            : "text-foreground/90 hover:bg-surface-secondary/60 hover:text-foreground",
                        )}
                      >
                        <div className="flex min-w-0 items-center gap-2.5">
                          <div className="flex h-6 w-6 shrink-0 items-center justify-center rounded border border-border bg-surface text-subtle-foreground group-hover:text-foreground">
                            {repo.isPrivate ? (
                              <Lock className="h-3.5 w-3.5" />
                            ) : (
                              <Globe className="h-3.5 w-3.5" />
                            )}
                          </div>
                          <div className="min-w-0 truncate">
                            <div className="flex items-center gap-2">
                              <span className="font-medium text-foreground">{repo.fullName}</span>
                              <span className="rounded border border-border bg-surface px-1.5 py-0.2 text-[10px] text-subtle-foreground">
                                {repo.isPrivate ? "Private" : "Public"}
                              </span>
                            </div>
                            {repo.description && (
                              <p className="truncate text-xs text-subtle-foreground">{repo.description}</p>
                            )}
                          </div>
                        </div>
                        <div className="flex shrink-0 items-center gap-1.5 text-xs text-subtle-foreground">
                          <GitFork className="h-3 w-3" />
                          <span>{repo.defaultBranch || "main"}</span>
                        </div>
                      </button>
                    )
                  })}
                </div>
              )}

              {flatItems.length === 0 && !isLoadingRepos && !ghError && (
                <div className="px-4 py-8 text-center text-sm text-subtle-foreground">
                  No repositories found for "{query}"
                </div>
              )}
            </div>

            {/* Footer Toolbar */}
            <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border bg-surface-secondary/30 px-3.5 py-2.5 text-xs text-subtle-foreground">
              <div className="flex items-center gap-3">
                <button
                  type="button"
                  onClick={handleConnectGitHub}
                  className="inline-flex items-center gap-1 font-medium text-foreground hover:underline"
                >
                  <Plus className="h-3.5 w-3.5" />
                  Connect account
                </button>
                {ghStatus?.isConnected && (
                  <a
                    href={manageUrl}
                    target="_blank"
                    rel="noreferrer"
                    className="inline-flex items-center gap-1 text-subtle-foreground hover:text-foreground hover:underline"
                  >
                    Manage access
                    <ExternalLink className="h-3 w-3" />
                  </a>
                )}
                <button
                  type="button"
                  onClick={loadGitHubData}
                  disabled={isLoadingRepos}
                  className="inline-flex items-center gap-1 text-subtle-foreground hover:text-foreground"
                >
                  <RefreshCw className={cn("h-3 w-3", isLoadingRepos && "animate-spin")} />
                  Refresh
                </button>
              </div>
              <div className="hidden sm:flex items-center gap-2">
                <span><Kbd>↑↓</Kbd> navigate</span>
                <span><Kbd>↵</Kbd> select</span>
              </div>
            </div>
          </>
        ) : (
          /* Step 2: Branch Selection */
          <div className="p-5">
            <button
              type="button"
              onClick={() => setStep("list")}
              className="inline-flex items-center gap-1.5 text-xs font-medium text-subtle-foreground transition-colors hover:text-foreground"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
              Back to repository list
            </button>

            <div className="mt-3">
              <h2 className="text-base font-semibold text-foreground">
                Connect {selectedRepo?.fullName}
              </h2>
              <p className="mt-0.5 text-xs text-subtle-foreground">
                Select the branch to clone and index into DevPilot.
              </p>
            </div>

            {connectError && (
              <div className="mt-3 flex items-center gap-2 rounded-[var(--radius-md)] border border-red-500/20 bg-red-500/10 p-3 text-xs text-red-500">
                <AlertCircle className="h-4 w-4 shrink-0" />
                <span>{connectError}</span>
              </div>
            )}

            <div className="mt-4 space-y-3">
              <div>
                <label className="tech-label mb-1.5 block">Branch</label>
                {isLoadingBranches ? (
                  <div className="flex h-9 items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-3 text-xs text-subtle-foreground">
                    <Loader2 className="h-3.5 w-3.5 animate-spin" />
                    Loading branches…
                  </div>
                ) : (
                  <select
                    value={selectedBranch}
                    onChange={(e) => setSelectedBranch(e.target.value)}
                    disabled={isConnecting}
                    className="h-9 w-full rounded-[var(--radius-md)] border border-border bg-surface px-3 text-sm text-foreground outline-none focus:border-border-strong"
                  >
                    {branches.map((b) => (
                      <option key={b.name} value={b.name}>
                        {b.name} {b.name === selectedRepo?.defaultBranch ? "(default)" : ""}
                      </option>
                    ))}
                  </select>
                )}
              </div>
            </div>

            <div className="mt-6 flex items-center justify-end gap-2.5 border-t border-border pt-4">
              <button
                type="button"
                onClick={() => setStep("list")}
                disabled={isConnecting}
                className="rounded-[var(--radius-md)] border border-border px-3.5 py-1.5 text-xs font-medium text-foreground hover:bg-surface-secondary"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleExecuteConnect}
                disabled={isConnecting || !selectedBranch}
                className="inline-flex items-center gap-1.5 rounded-[var(--radius-md)] bg-foreground px-4 py-1.5 text-xs font-medium text-background transition-opacity hover:opacity-90 disabled:opacity-50"
              >
                {isConnecting && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                Connect & Index
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
