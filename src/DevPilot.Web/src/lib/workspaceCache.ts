import type { WorkspaceAnalysis, WorkspaceArchitecture, BrainStatus, WorkspaceOverview } from "@/types"

interface CacheEntry<T> {
  data: T
  timestamp: number
  commitSha?: string | null
}

interface WorkspaceCacheStore {
  overview?: CacheEntry<WorkspaceOverview>
  analysis?: CacheEntry<WorkspaceAnalysis>
  architecture?: CacheEntry<WorkspaceArchitecture>
  brainStatus?: CacheEntry<BrainStatus>
}

const cache = new Map<string, WorkspaceCacheStore>()

const DEFAULT_MAX_AGE_MS = 5 * 60 * 1000 // 5 minutes fresh

function getStore(workspaceId: string): WorkspaceCacheStore {
  let store = cache.get(workspaceId)
  if (!store) {
    store = {}
    cache.set(workspaceId, store)
  }
  return store
}

export function getCachedWorkspaceOverview(workspaceId: string): { data: WorkspaceOverview | null; isStale: boolean } {
  const store = cache.get(workspaceId)
  if (!store?.overview) return { data: null, isStale: true }
  const isStale = Date.now() - store.overview.timestamp > DEFAULT_MAX_AGE_MS
  return { data: store.overview.data, isStale }
}

export function setCachedWorkspaceOverview(workspaceId: string, data: WorkspaceOverview): void {
  const store = getStore(workspaceId)
  store.overview = { data, timestamp: Date.now() }
}

export function getCachedWorkspaceAnalysis(workspaceId: string, commitSha?: string | null): { data: WorkspaceAnalysis | null; isStale: boolean } {
  const store = cache.get(workspaceId)
  if (!store?.analysis) return { data: null, isStale: true }
  if (commitSha && store.analysis.commitSha && store.analysis.commitSha !== commitSha) {
    return { data: store.analysis.data, isStale: true }
  }
  const isStale = Date.now() - store.analysis.timestamp > DEFAULT_MAX_AGE_MS
  return { data: store.analysis.data, isStale }
}

export function setCachedWorkspaceAnalysis(workspaceId: string, data: WorkspaceAnalysis): void {
  const store = getStore(workspaceId)
  store.analysis = {
    data,
    timestamp: Date.now(),
    commitSha: data.repository?.commitSha ?? null,
  }
}

export function getCachedWorkspaceArchitecture(workspaceId: string, commitSha?: string | null): { data: WorkspaceArchitecture | null; isStale: boolean } {
  const store = cache.get(workspaceId)
  if (!store?.architecture) return { data: null, isStale: true }
  if (commitSha && store.architecture.commitSha && store.architecture.commitSha !== commitSha) {
    return { data: store.architecture.data, isStale: true }
  }
  const isStale = Date.now() - store.architecture.timestamp > DEFAULT_MAX_AGE_MS
  return { data: store.architecture.data, isStale }
}

export function setCachedWorkspaceArchitecture(workspaceId: string, data: WorkspaceArchitecture): void {
  const store = getStore(workspaceId)
  store.architecture = {
    data,
    timestamp: Date.now(),
    commitSha: data.repository?.commitSha ?? null,
  }
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
