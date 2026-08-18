import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react"
import { getRepositoryWorkspaces, createRepositoryWorkspace } from "@/api"
import {
  RepositoryWorkspaceStatus,
  type RepositoryWorkspace,
  type CreateRepositoryWorkspaceRequest,
} from "@/types"

interface WorkspaceContextValue {
  workspaces: RepositoryWorkspace[]
  activeWorkspace: RepositoryWorkspace | null
  activeWorkspaceId: string | null
  isLoading: boolean
  error: string | null
  selectWorkspace: (id: string) => void
  refreshWorkspaces: () => Promise<void>
  connectWorkspace: (request: CreateRepositoryWorkspaceRequest) => Promise<RepositoryWorkspace>
}

const WorkspaceContext = createContext<WorkspaceContextValue | null>(null)

const STORAGE_KEY = "devpilot-active-workspace-id"

export function WorkspaceProvider({ children }: { children: ReactNode }) {
  const [workspaces, setWorkspaces] = useState<RepositoryWorkspace[]>([])
  const [activeWorkspaceId, setActiveWorkspaceId] = useState<string | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const resolveActiveWorkspace = useCallback(
    (
      list: RepositoryWorkspace[],
      preferredId?: string | null,
    ): string | null => {
      const persistedId =
        preferredId !== undefined
          ? preferredId
          : typeof window !== "undefined"
            ? window.localStorage.getItem(STORAGE_KEY)
            : null

      if (persistedId) {
        const found = list.find((w) => w.id === persistedId)
        if (found && found.status === RepositoryWorkspaceStatus.Completed) {
          return found.id
        }
      }

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

  const value: WorkspaceContextValue = {
    workspaces,
    activeWorkspace,
    activeWorkspaceId: activeWorkspace?.id ?? null,
    isLoading,
    error,
    selectWorkspace,
    refreshWorkspaces,
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
