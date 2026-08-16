import { useCallback, useEffect, useState } from 'react'
import { getTasks, getWorkspaces } from '../api'
import { priorityLabels, statusLabels, TaskListItem, Workspace } from '../types'

interface TaskListProps {
  onCreate: () => void
  onSelect: (id: string) => void
}

export function TaskList({ onCreate, onSelect }: TaskListProps) {
  const [tasks, setTasks] = useState<TaskListItem[]>([])
  const [workspaces, setWorkspaces] = useState<Workspace[]>([])
  const [status, setStatus] = useState<string>('')
  const [priority, setPriority] = useState<string>('')
  const [repositoryWorkspaceId, setRepositoryWorkspaceId] = useState<string>('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const loadWorkspaces = useCallback(async () => {
    try {
      const data = await getWorkspaces()
      setWorkspaces(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load workspaces')
    }
  }, [])

  const loadTasks = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const data = await getTasks({
        status: status === '' ? undefined : Number(status),
        priority: priority === '' ? undefined : Number(priority),
        repositoryWorkspaceId: repositoryWorkspaceId || undefined,
      })
      setTasks(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load tasks')
    } finally {
      setLoading(false)
    }
  }, [status, priority, repositoryWorkspaceId])

  useEffect(() => {
    loadWorkspaces()
  }, [loadWorkspaces])

  useEffect(() => {
    loadTasks()
  }, [loadTasks])

  return (
    <section className="page">
      <div className="page-header">
        <h2>Tasks</h2>
        <button className="btn-primary" onClick={onCreate}>
          + New Task
        </button>
      </div>

      <div className="filters">
        <label>
          Status
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            {Object.entries(statusLabels).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>

        <label>
          Priority
          <select value={priority} onChange={(e) => setPriority(e.target.value)}>
            <option value="">All</option>
            {Object.entries(priorityLabels).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>

        <label>
          Workspace
          <select
            value={repositoryWorkspaceId}
            onChange={(e) => setRepositoryWorkspaceId(e.target.value)}
          >
            <option value="">All Workspaces</option>
            {workspaces.map((ws) => (
              <option key={ws.id} value={ws.id}>
                {ws.displayName}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error && <div className="alert error">{error}</div>}

      {loading ? (
        <div className="loading">Loading tasks…</div>
      ) : tasks.length === 0 ? (
        <div className="empty-state">
          <p>No tasks found.</p>
          <button className="btn-secondary" onClick={onCreate}>
            Create your first task
          </button>
        </div>
      ) : (
        <div className="task-list">
          {tasks.map((task) => (
            <div
              key={task.id}
              className="task-card"
              onClick={() => onSelect(task.id)}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') onSelect(task.id)
              }}
            >
              <div className="task-card-header">
                <h3>{task.title}</h3>
                <span className={`badge status-${task.status}`}>
                  {statusLabels[task.status] ?? task.status}
                </span>
              </div>
              <div className="task-card-meta">
                <span>{task.repositoryName}</span>
                <span className={`badge priority-${task.priority}`}>
                  {priorityLabels[task.priority] ?? task.priority}
                </span>
                <span className="task-date">
                  Updated {new Date(task.updatedAt).toLocaleString()}
                </span>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
