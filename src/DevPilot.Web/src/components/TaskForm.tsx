import { useCallback, useEffect, useState } from 'react'
import { createTask, getWorkspaces } from '../api'
import { priorityOptions, Workspace } from '../types'

interface TaskFormProps {
  onCreated: (id: string) => void
  onCancel: () => void
}

export function TaskForm({ onCreated, onCancel }: TaskFormProps) {
  const [workspaces, setWorkspaces] = useState<Workspace[]>([])
  const [repositoryWorkspaceId, setRepositoryWorkspaceId] = useState<string>('')
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [acceptanceCriteria, setAcceptanceCriteria] = useState('')
  const [priority, setPriority] = useState<number>(1)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const loadWorkspaces = useCallback(async () => {
    try {
      const data = await getWorkspaces()
      setWorkspaces(data)
      if (data.length > 0 && !repositoryWorkspaceId) {
        setRepositoryWorkspaceId(data[0].id)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load workspaces')
    }
  }, [repositoryWorkspaceId])

  useEffect(() => {
    loadWorkspaces()
  }, [loadWorkspaces])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!repositoryWorkspaceId) {
      setError('Please select a repository workspace')
      return
    }
    if (!title.trim()) {
      setError('Title is required')
      return
    }

    setSaving(true)
    setError(null)
    try {
      const task = await createTask({
        repositoryWorkspaceId,
        title: title.trim(),
        description: description.trim(),
        acceptanceCriteria: acceptanceCriteria.trim() || undefined,
        priority,
      })
      onCreated(task.id)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create task')
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="page">
      <h2>Create Task</h2>
      <form className="form" onSubmit={handleSubmit}>
        <label>
          Repository Workspace
          <select
            value={repositoryWorkspaceId}
            onChange={(e) => setRepositoryWorkspaceId(e.target.value)}
            required
          >
            <option value="">Select a workspace…</option>
            {workspaces.map((ws) => (
              <option key={ws.id} value={ws.id}>
                {ws.displayName} ({ws.owner}/{ws.repository} — {ws.branch})
              </option>
            ))}
          </select>
        </label>

        <label>
          Title
          <input
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="e.g. Refactor authentication middleware"
            required
          />
        </label>

        <label>
          Description
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Describe what needs to be done and why."
            rows={5}
            required
          />
        </label>

        <label>
          Acceptance Criteria
          <textarea
            value={acceptanceCriteria}
            onChange={(e) => setAcceptanceCriteria(e.target.value)}
            placeholder="What must be true for this task to be considered complete?"
            rows={3}
          />
        </label>

        <label>
          Priority
          <select
            value={priority}
            onChange={(e) => setPriority(Number(e.target.value))}
            required
          >
            {priorityOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>

        {error && <div className="alert error">{error}</div>}

        <div className="form-actions">
          <button type="button" className="btn-secondary" onClick={onCancel}>
            Cancel
          </button>
          <button type="submit" className="btn-primary" disabled={saving}>
            {saving ? 'Creating…' : 'Create Task'}
          </button>
        </div>
      </form>
    </section>
  )
}
