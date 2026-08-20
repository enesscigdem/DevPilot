import type { WorkspaceAnalysis, WorkspaceArchitecture, BrainStatus } from "@/types"

interface CacheEntry<T> {
  data: T
  timestamp: number
}

interface WorkspaceCacheStore {
  analysis?: CacheEntry<WorkspaceAnalysis>
  architecture?: CacheEntry<WorkspaceArchitecture>
  brainStatus?: CacheEntry<BrainStatus>
}

const cache = new Map<string, WorkspaceCacheStore>()

const DEFAULT_MAX_AGE_MS = 2 * 60 * 1000

function getStore(workspaceId: string): WorkspaceCacheStore {
  let store = cache.get(workspaceId)
  if (!store) {
    store = {}
    cache.set(workspaceId, store)
  }
  return store
}

export function getCachedWorkspaceAnalysis(workspaceId: string): { data: WorkspaceAnalysis | null; isStale: boolean } {
  const store = cache.get(workspaceId)
  if (!store?.analysis) return { data: null, isStale: true }
  const isStale = Date.now() - store.analysis.timestamp > DEFAULT_MAX_AGE_MS
  return { data: store.analysis.data, isStale }
}

export function setCachedWorkspaceAnalysis(workspaceId: string, data: WorkspaceAnalysis): void {
  const store = getStore(workspaceId)
  store.analysis = { data, timestamp: Date.now() }
}

export function getCachedWorkspaceArchitecture(workspaceId: string): { data: WorkspaceArchitecture | null; isStale: boolean } {
  const store = cache.get(workspaceId)
  if (!store?.architecture) return { data: null, isStale: true }
  const isStale = Date.now() - store.architecture.timestamp > DEFAULT_MAX_AGE_MS
  return { data: store.architecture.data, isStale }
}

export function setCachedWorkspaceArchitecture(workspaceId: string, data: WorkspaceArchitecture): void {
  const store = getStore(workspaceId)
  store.architecture = { data, timestamp: Date.now() }
}

export function getCachedBrainStatus(workspaceId: string): { data: BrainStatus | null; isStale: boolean } {
  const store = cache.get(workspaceId)
  if (!store?.brainStatus) return { data: null, isStale: true }
  const isStale = Date.now() - store.brainStatus.timestamp > DEFAULT_MAX_AGE_MS
  return { data: store.brainStatus.data, isStale }
}

export function setCachedBrainStatus(workspaceId: string, data: BrainStatus): void {
  const store = getStore(workspaceId)
  store.brainStatus = { data, timestamp: Date.now() }
}

export function invalidateWorkspaceCache(workspaceId?: string): void {
  if (workspaceId) {
    cache.delete(workspaceId)
  } else {
    cache.clear()
  }
}
