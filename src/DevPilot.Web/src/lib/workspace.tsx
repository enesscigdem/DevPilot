import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react"
import {
  getRepositoryWorkspaces,
  createRepositoryWorkspace,
  getWorkspaceOverview,
} from "@/api"
import {
  getCachedWorkspaceOverview,
  setCachedWorkspaceOverview,
} from "@/lib/workspaceCache"
import {
  RepositoryWorkspaceStatus,
  type RepositoryWorkspace,
  type CreateRepositoryWorkspaceRequest,
  type WorkspaceActiveExecution,
  type WorkspaceOverview,
} from "@/types"

interface WorkspaceContextValue {
  workspaces: RepositoryWorkspace[]
  activeWorkspace: RepositoryWorkspace | null
  activeWorkspaceId: string | null
  overview: WorkspaceOverview | null
  activeExecution: WorkspaceActiveExecution | null
  activeAgentExecution: WorkspaceActiveExecution | null
  isLoading: boolean
  isLoadingOverview: boolean
  error: string | null
  overviewError: string | null
  selectWorkspace: (id: string) => void
  refreshWorkspaces: () => Promise<void>
  refreshOverview: (isBackground?: boolean) => Promise<void>
  connectWorkspace: (request: CreateRepositoryWorkspaceRequest) => Promise<RepositoryWorkspace>
}

const WorkspaceContext = createContext<WorkspaceContextValue | null>(null)

const STORAGE_KEY = "devpilot-active-workspace-id"

export function WorkspaceProvider({ children }: { children: ReactNode }) {
  const [workspaces, setWorkspaces] = useState<RepositoryWorkspace[]>([])
  const [activeWorkspaceId, setActiveWorkspaceId] = useState<string | null>(() => {
    return typeof window !== "undefined" ? window.localStorage.getItem(STORAGE_KEY) : null
  })
  const [overview, setOverview] = useState<WorkspaceOverview | null>(() => {
    const persistedId = typeof window !== "undefined" ? window.localStorage.getItem(STORAGE_KEY) : null
    return persistedId ? getCachedWorkspaceOverview(persistedId).data : null
  })
  const [isLoading, setIsLoading] = useState(true)
  const [isLoadingOverview, setIsLoadingOverview] = useState(() => {
    const persistedId = typeof window !== "undefined" ? window.localStorage.getItem(STORAGE_KEY) : null
    return !persistedId || !getCachedWorkspaceOverview(persistedId).data
  })
  const [error, setError] = useState<string | null>(null)
  const [overviewError, setOverviewError] = useState<string | null>(null)

  const overviewRef = useRef<WorkspaceOverview | null>(overview)
  const activeReqOverviewWorkspaceIdRef = useRef<string | null>(null)
  const inFlightOverviewRef = useRef<{
    workspaceId: string
    promise: Promise<WorkspaceOverview>
    controller: AbortController
  } | null>(null)
  const inFlightWorkspacesPromiseRef = useRef<Promise<RepositoryWorkspace[]> | null>(null)
  const abortOverviewControllerRef = useRef<AbortController | null>(null)

  useEffect(() => {
    overviewRef.current = overview
  }, [overview])

  const resolveActiveWorkspace = useCallback(
    (
      list: RepositoryWorkspace[],
      preferredId?: string | null,
    ): string | null => {
      // 1. If preferredId (current in-memory selection) is valid and completed, preserve it
      if (preferredId) {
        const foundPreferred = list.find((w) => w.id === preferredId)
        if (foundPreferred && foundPreferred.status === RepositoryWorkspaceStatus.Completed) {
          return foundPreferred.id
        }
      }

      // 2. If no valid preferredId, check persisted localStorage ID
      const persistedId = typeof window !== "undefined" ? window.localStorage.getItem(STORAGE_KEY) : null
      if (persistedId) {
        const foundPersisted = list.find((w) => w.id === persistedId)
        if (foundPersisted && foundPersisted.status === RepositoryWorkspaceStatus.Completed) {
          return foundPersisted.id
        }
      }

      // 3. Fallback only if no active or persisted completed workspace exists
      const firstCompleted = list.find(
        (w) => w.status === RepositoryWorkspaceStatus.Completed,
      )
      return firstCompleted ? firstCompleted.id : null
    },
    [],
  )

  const refreshWorkspaces = useCallback(async () => {
    setIsLoading(true)
    setError(null)

    // Deduplicate in-flight getRepositoryWorkspaces requests (e.g. StrictMode double-mount)
    if (!inFlightWorkspacesPromiseRef.current) {
      inFlightWorkspacesPromiseRef.current = getRepositoryWorkspaces().finally(() => {
        inFlightWorkspacesPromiseRef.current = null
      })
    }

    try {
      const list = await inFlightWorkspacesPromiseRef.current
      setWorkspaces(list)

      setActiveWorkspaceId((prev) => {
        const resolved = resolveActiveWorkspace(list, prev)
        if (typeof window !== "undefined") {
          if (resolved) {
            window.localStorage.setItem(STORAGE_KEY, resolved)
          } else {
            window.localStorage.removeItem(STORAGE_KEY)
          }
        }
        return resolved
      })
    } catch (err) {
      const msg =
        err instanceof Error ? err.message : "Failed to load repository workspaces."
      setError(msg)
    } finally {
      setIsLoading(false)
    }
  }, [resolveActiveWorkspace])

  useEffect(() => {
    refreshWorkspaces()
  }, [refreshWorkspaces])

  const fetchOverview = useCallback(
    async (isBackground = false) => {
      if (!activeWorkspaceId) {
        setOverview(null)
        setIsLoadingOverview(false)
        setOverviewError(null)
        return
      }

      const targetWorkspaceId = activeWorkspaceId
      activeReqOverviewWorkspaceIdRef.current = targetWorkspaceId

      // If cached data exists for this workspace, load immediately
      const cached = getCachedWorkspaceOverview(targetWorkspaceId)
      if (cached.data) {
        setOverview((prev) => (prev && prev.header.workspaceId === targetWorkspaceId ? prev : cached.data))
        if (!isBackground) {
          setIsLoadingOverview(false)
        }
      } else if (!isBackground) {
        setIsLoadingOverview(true)
        setOverviewError(null)
      }

      // If an identical non-aborted request is already in flight for this workspace, reuse it
      const currentInFlight = inFlightOverviewRef.current
      if (
        currentInFlight &&
        currentInFlight.workspaceId === targetWorkspaceId &&
        !currentInFlight.controller.signal.aborted
      ) {
        try {
          const data = await currentInFlight.promise
          if (activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId) {
            setOverview(data)
            setOverviewError(null)
          }
          return
        } catch (err) {
          // If the reused promise failed due to an abort from previous cleanup, fall through to fetch afresh
          if (!(err instanceof DOMException && err.name === "AbortError")) {
            return
          }
        }
      }

      if (abortOverviewControllerRef.current) {
        abortOverviewControllerRef.current.abort()
      }
      const controller = new AbortController()
      abortOverviewControllerRef.current = controller

      const reqPromise = getWorkspaceOverview(targetWorkspaceId, {
        signal: controller.signal,
      })
      inFlightOverviewRef.current = {
        workspaceId: targetWorkspaceId,
        promise: reqPromise,
        controller,
      }

      try {
        const data = await reqPromise
        if (activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId) {
          setOverview(data)
          setCachedWorkspaceOverview(targetWorkspaceId, data)
          setOverviewError(null)
        }
      } catch (err) {
        if (err instanceof DOMException && err.name === "AbortError") {
          // Clean up this aborted controller ref so future callers don't reuse it
          if (inFlightOverviewRef.current?.controller === controller) {
            inFlightOverviewRef.current = null
          }
          return
        }
        if (activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId) {
          // If we already have last-known-good cached data for this workspace, keep it without showing red error
          const hasLkg = Boolean(cached.data || overviewRef.current)
          if (!hasLkg && !isBackground) {
            setOverviewError(
              err instanceof Error ? err.message : "Failed to load workspace overview.",
            )
          }
        }
      } finally {
        if (inFlightOverviewRef.current?.controller === controller) {
          inFlightOverviewRef.current = null
        }
        if (
          activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId &&
          !isBackground
        ) {
          setIsLoadingOverview(false)
        }
      }
    },
    [activeWorkspaceId],
  )

  // Trigger overview fetch ONLY when active workspace ID changes
  useEffect(() => {
    if (!activeWorkspaceId) {
      setOverview(null)
      setOverviewError(null)
      setIsLoadingOverview(false)
      return
    }

    const cached = getCachedWorkspaceOverview(activeWorkspaceId)
    if (cached.data) {
      setOverview(cached.data)
      setOverviewError(null)
      setIsLoadingOverview(false)
    } else {
      setOverview(null)
      setOverviewError(null)
      setIsLoadingOverview(true)
    }

    fetchOverview(false)

    return () => {
      if (abortOverviewControllerRef.current) {
        abortOverviewControllerRef.current.abort()
      }
    }
  }, [activeWorkspaceId, fetchOverview])

  const hasActiveExecution = Boolean(
    (overview?.activeAgentExecution && !overview.activeAgentExecution.completedAt) ||
    (overview?.activeExecution && !overview.activeExecution.completedAt),
  )

  // Only poll when an execution is genuinely active/running. No idle background polling.
  useEffect(() => {
    if (!activeWorkspaceId || !hasActiveExecution) {
      return
    }

    const intervalMs = 3500
    const timer = setInterval(() => {
      fetchOverview(true)
    }, intervalMs)

    return () => clearInterval(timer)
  }, [activeWorkspaceId, hasActiveExecution, fetchOverview])

  const selectWorkspace = useCallback(
    (id: string) => {
      const target = workspaces.find((w) => w.id === id)
      if (target && target.status === RepositoryWorkspaceStatus.Completed) {
        setActiveWorkspaceId(id)
        if (typeof window !== "undefined") {
          window.localStorage.setItem(STORAGE_KEY, id)
        }
      }
    },
    [workspaces],
  )

  const connectWorkspace = useCallback(
    async (request: CreateRepositoryWorkspaceRequest): Promise<RepositoryWorkspace> => {
      const created = await createRepositoryWorkspace(request)
      const list = await getRepositoryWorkspaces()
      setWorkspaces(list)

      if (created.status === RepositoryWorkspaceStatus.Completed) {
        setActiveWorkspaceId(created.id)
        if (typeof window !== "undefined") {
          window.localStorage.setItem(STORAGE_KEY, created.id)
        }
      } else {
        setActiveWorkspaceId((prev) => {
          const resolved = resolveActiveWorkspace(list, prev)
          if (typeof window !== "undefined") {
            if (resolved) {
              window.localStorage.setItem(STORAGE_KEY, resolved)
            } else {
              window.localStorage.removeItem(STORAGE_KEY)
            }
          }
          return resolved
        })
      }

      return created
    },
    [resolveActiveWorkspace],
  )

  const activeWorkspace =
    workspaces.find(
      (w) =>
        w.id === activeWorkspaceId &&
        w.status === RepositoryWorkspaceStatus.Completed,
    ) ?? null

  const activeExecution = overview?.activeExecution ?? null
  const activeAgentExecution = overview?.activeAgentExecution ?? null

  const value: WorkspaceContextValue = {
    workspaces,
    activeWorkspace,
    activeWorkspaceId: activeWorkspace?.id ?? null,
    overview,
    activeExecution,
    activeAgentExecution,
    isLoading,
    isLoadingOverview,
    error,
    overviewError,
    selectWorkspace,
    refreshWorkspaces,
    refreshOverview: fetchOverview,
    connectWorkspace,
  }

  return (
    <WorkspaceContext.Provider value={value}>
      {children}
    </WorkspaceContext.Provider>
  )
}

export function useWorkspace() {
  const ctx = useContext(WorkspaceContext)
  if (!ctx) {
    throw new Error("useWorkspace must be used within WorkspaceProvider")
  }
  return ctx
}
