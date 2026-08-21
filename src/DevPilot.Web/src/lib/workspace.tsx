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

import {
  getCachedWorkspaceOverview,
  setCachedWorkspaceOverview,
} from "@/lib/workspaceCache"

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

  const activeReqOverviewWorkspaceIdRef = useRef<string | null>(null)
  const inFlightOverviewPromiseRef = useRef<{ workspaceId: string; promise: Promise<WorkspaceOverview> } | null>(null)
  const abortOverviewControllerRef = useRef<AbortController | null>(null)

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
    try {
      const list = await getRepositoryWorkspaces()
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

      // If an identical request is already in flight for this workspace, reuse the in-flight promise
      if (inFlightOverviewPromiseRef.current?.workspaceId === targetWorkspaceId) {
        try {
          const data = await inFlightOverviewPromiseRef.current.promise
          if (activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId) {
            setOverview(data)
            setOverviewError(null)
          }
        } catch {
          // Handled by primary caller
        }
        return
      }

      if (abortOverviewControllerRef.current) {
        abortOverviewControllerRef.current.abort()
      }
      const controller = new AbortController()
      abortOverviewControllerRef.current = controller

      const reqPromise = getWorkspaceOverview(targetWorkspaceId, {
        signal: controller.signal,
      })
      inFlightOverviewPromiseRef.current = { workspaceId: targetWorkspaceId, promise: reqPromise }

      try {
        const data = await reqPromise
        if (activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId) {
          setOverview(data)
          setCachedWorkspaceOverview(targetWorkspaceId, data)
          setOverviewError(null)
        }
      } catch (err) {
        if (err instanceof DOMException && err.name === "AbortError") {
          return
        }
        if (activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId) {
          // If we already have last-known-good cached data for this workspace, keep it without showing red error
          const hasLkg = Boolean(cached.data || overview)
          if (!hasLkg && !isBackground) {
            setOverviewError(
              err instanceof Error ? err.message : "Failed to load workspace overview.",
            )
          }
        }
      } finally {
        if (inFlightOverviewPromiseRef.current?.workspaceId === targetWorkspaceId) {
          inFlightOverviewPromiseRef.current = null
        }
        if (
          activeReqOverviewWorkspaceIdRef.current === targetWorkspaceId &&
          !isBackground
        ) {
          setIsLoadingOverview(false)
        }
      }
    },
    [activeWorkspaceId, overview],
  )

  // Trigger overview fetch when active workspace changes
  useEffect(() => {
    if (activeWorkspaceId) {
      const cached = getCachedWorkspaceOverview(activeWorkspaceId)
      if (cached.data) {
        setOverview(cached.data)
        setOverviewError(null)
      } else {
        setOverview(null)
        setOverviewError(null)
      }
    } else {
      setOverview(null)
      setOverviewError(null)
    }

    fetchOverview(false)

    return () => {
      if (abortOverviewControllerRef.current) {
        abortOverviewControllerRef.current.abort()
      }
    }
  }, [activeWorkspaceId, fetchOverview])

  // Single global overview polling owner: 3.5s when active execution running, 15s when idle
  useEffect(() => {
    if (!activeWorkspaceId) return

    const hasActiveExecution = Boolean(
      (overview?.activeAgentExecution && !overview.activeAgentExecution.completedAt) ||
      (overview?.activeExecution && !overview.activeExecution.completedAt),
    )
    const intervalMs = hasActiveExecution ? 3500 : 15000

    const timer = setInterval(() => {
      fetchOverview(true)
    }, intervalMs)

    return () => clearInterval(timer)
  }, [activeWorkspaceId, overview?.activeAgentExecution, overview?.activeExecution, fetchOverview])

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
