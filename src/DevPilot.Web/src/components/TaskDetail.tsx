import { useCallback, useEffect, useState } from 'react'
import { deleteTask, getTask, updateTaskStatus } from '../api'
import { priorityLabels, statusLabels, Task } from '../types'

interface TaskDetailProps {
  taskId: string
  onBack: () => void
  onDeleted: () => void
}

export function TaskDetail({ taskId, onBack, onDeleted }: TaskDetailProps) {
  const [task, setTask] = useState<Task | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selectedStatus, setSelectedStatus] = useState<number>(0)
  const [saving, setSaving] = useState(false)
  const [analyzing, setAnalyzing] = useState(false)

  const loadTask = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const data = await getTask(taskId)
      setTask(data)
      setSelectedStatus(data.status)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load task')
    } finally {
      setLoading(false)
    }
  }, [taskId])

  useEffect(() => {
    loadTask()
  }, [loadTask])

  const handleStatusChange = async () => {
    if (!task || task.status === selectedStatus) return
    setSaving(true)
    setError(null)
    try {
      await updateTaskStatus(task.id, { status: selectedStatus })
      await loadTask()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update status')
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async () => {
    if (!task || !confirm('Are you sure you want to delete this task?')) return
    setSaving(true)
    setError(null)
    try {
      await deleteTask(task.id)
      onDeleted()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete task')
      setSaving(false)
    }
  }

  const handleAnalyze = async () => {
    // Visual only — no AI call is triggered.
    setAnalyzing(true)
    setTimeout(() => setAnalyzing(false), 1200)
  }

  if (loading) return <div className="loading">Loading task…</div>
  if (error) return <div className="alert error">{error}</div>
  if (!task) return null

  return (
    <section className="page">
      <button className="btn-text" onClick={onBack}>
        ← Back to tasks
      </button>

      <div className="detail-header">
        <h2>{task.title}</h2>
        <div className="badges">
          <span className={`badge status-${task.status}`}>
            {statusLabels[task.status] ?? task.status}
          </span>
          <span className={`badge priority-${task.priority}`}>
            {priorityLabels[task.priority] ?? task.priority}
          </span>
        </div>
      </div>

      <div className="detail-meta">
        <p>
          <strong>Workspace:</strong> {task.repositoryWorkspaceName} ({task.repositoryOwner}/
          {task.repositoryName})
        </p>
        <p>
          <strong>Created:</strong> {new Date(task.createdAt).toLocaleString()}
        </p>
        <p>
          <strong>Updated:</strong> {new Date(task.updatedAt).toLocaleString()}
        </p>
      </div>

      <div className="detail-section">
        <h3>Description</h3>
        <p className="detail-body">{task.description || 'No description provided.'}</p>
      </div>

      {task.acceptanceCriteria && (
        <div className="detail-section">
          <h3>Acceptance Criteria</h3>
          <p className="detail-body">{task.acceptanceCriteria}</p>
        </div>
      )}

      <div className="detail-section">
        <h3>Impact Analysis</h3>
        <div className="impact-empty">
          <p>Impact analysis has not been run yet.</p>
          <button
            className="btn-primary"
            onClick={handleAnalyze}
            disabled={analyzing}
          >
            {analyzing ? 'Analyzing…' : 'Analyze Task'}
          </button>
        </div>
      </div>

      <div className="detail-actions">
        <label className="status-editor">
          Status
          <select
            value={selectedStatus}
            onChange={(e) => setSelectedStatus(Number(e.target.value))}
          >
            {Object.entries(statusLabels).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>
        <button
          className="btn-secondary"
          onClick={handleStatusChange}
          disabled={saving || task.status === selectedStatus}
        >
          {saving ? 'Updating…' : 'Update Status'}
        </button>
        <button className="btn-danger" onClick={handleDelete} disabled={saving}>
          Delete
        </button>
      </div>
    </section>
  )
}
