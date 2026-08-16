import { useState } from 'react'
import { TaskList } from './components/TaskList'
import { TaskForm } from './components/TaskForm'
import { TaskDetail } from './components/TaskDetail'
import { WorkspaceView } from './components/WorkspaceView'

type View = 'list' | 'create' | 'detail' | 'workspace'

function App() {
  const [view, setView] = useState<View>('list')
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null)

  return (
    <div className="app">
      <header className="app-header">
        <div className="app-brand">
          <h1>DevPilot</h1>
          <span className="app-tagline">AI-powered Software Development & Delivery</span>
        </div>
        <nav className="app-nav">
          <button
            className={view === 'list' || view === 'detail' ? 'active' : ''}
            onClick={() => setView('list')}
          >
            Tasks
          </button>
          <button
            className={view === 'workspace' ? 'active' : ''}
            onClick={() => setView('workspace')}
          >
            Workspace
          </button>
        </nav>
      </header>

      <main className="app-main">
        {view === 'list' && (
          <TaskList
            onCreate={() => setView('create')}
            onSelect={(id) => {
              setSelectedTaskId(id)
              setView('detail')
            }}
          />
        )}
        {view === 'create' && (
          <TaskForm
            onCreated={(id) => {
              setSelectedTaskId(id)
              setView('detail')
            }}
            onCancel={() => setView('list')}
          />
        )}
        {view === 'detail' && selectedTaskId && (
          <TaskDetail
            taskId={selectedTaskId}
            onBack={() => setView('list')}
            onDeleted={() => setView('list')}
          />
        )}
        {view === 'workspace' && <WorkspaceView />}
      </main>
    </div>
  )
}

export default App
