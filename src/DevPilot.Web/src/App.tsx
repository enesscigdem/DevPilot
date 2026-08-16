import { Routes, Route, Navigate } from "react-router-dom"
import { AppShell } from "./components/AppShell"
import { Workspace } from "./pages/Workspace"
import { ProjectWorkspace } from "./pages/ProjectWorkspace"
import { Tasks } from "./pages/Tasks"
import { TaskImpact } from "./pages/TaskImpact"
import { Executions } from "./pages/Executions"
import { ExecutionWorkspace } from "./pages/ExecutionWorkspace"
import { CodeReview } from "./pages/CodeReview"
import { ProjectBrain } from "./pages/ProjectBrain"
import { Architecture } from "./pages/Architecture"

export default function App() {
  return (
    <AppShell>
      <Routes>
        <Route path="/" element={<Workspace />} />
        <Route path="/projects" element={<ProjectWorkspace />} />
        <Route path="/tasks" element={<Tasks />} />
        <Route path="/tasks/:id" element={<TaskImpact />} />
        <Route path="/executions" element={<Executions />} />
        <Route path="/executions/:id" element={<ExecutionWorkspace />} />
        <Route path="/review/:id" element={<CodeReview />} />
        <Route path="/brain" element={<ProjectBrain />} />
        <Route path="/architecture" element={<Architecture />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AppShell>
  )
}
