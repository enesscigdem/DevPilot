import { useCallback, useEffect, useState } from 'react'
import { getWorkspaces } from '../api'
import { Workspace } from '../types'

export function WorkspaceView() {
  const [workspaces, setWorkspaces] = useState<Workspace[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const data = await getWorkspaces()
      setWorkspaces(data)
      if (data.length > 0 && !selectedId) {
        setSelectedId(data[0].id)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load workspaces')
    } finally {
      setLoading(false)
    }
  }, [selectedId])

  useEffect(() => {
    load()
  }, [load])

  const selected = workspaces.find((ws) => ws.id === selectedId)

  return (
    <section className="page workspace-page">
      <div className="workspace-sidebar">
        <h2>Workspaces</h2>
        {loading && workspaces.length === 0 && <div className="loading">Loading…</div>}
        {error && <div className="alert error">{error}</div>}
        <ul className="workspace-list">
          {workspaces.map((ws) => (
            <li
              key={ws.id}
              className={selectedId === ws.id ? 'selected' : ''}
              onClick={() => setSelectedId(ws.id)}
            >
              <div className="workspace-name">{ws.displayName}</div>
              <div className="workspace-repo">
                {ws.owner}/{ws.repository}
              </div>
              <div className="workspace-branch">{ws.branch}</div>
            </li>
          ))}
        </ul>
      </div>

      <div className="workspace-main">
        {selected ? (
          <>
            <h2>{selected.displayName}</h2>
            <div className="workspace-details">
              <div className="detail-row">
                <span>Owner</span>
                <span>{selected.owner}</span>
              </div>
              <div className="detail-row">
                <span>Repository</span>
                <span>{selected.repository}</span>
              </div>
              <div className="detail-row">
                <span>Branch</span>
                <span>{selected.branch}</span>
              </div>
              <div className="detail-row">
                <span>Status</span>
                <span>{selected.status}</span>
              </div>
            </div>
            <div className="workspace-hint">
              <p>
                This is a light engineering workspace view. Files, branches and build
                pipelines will be linked here in future releases.
              </p>
            </div>
          </>
        ) : (
          <div className="empty-state">
            <p>Select a workspace to inspect its configuration.</p>
          </div>
        )}
      </div>
    </section>
  )
}
