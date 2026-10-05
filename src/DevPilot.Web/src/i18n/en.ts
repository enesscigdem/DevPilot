import overview from "./locales/en/overview"
import review from "./locales/en/review"
import revision from "./locales/en/revision"
import impact from "./locales/en/impact"
import brain from "./locales/en/brain"
import arch from "./locales/en/arch"
import picker from "./locales/en/picker"
import repo from "./locales/en/repo"
import compare from "./locales/en/compare"
import insights from "./locales/en/insights"
import execWs from "./locales/en/execWs"
import executions from "./locales/en/executions"
import tasks from "./locales/en/tasks"
import shared from "./locales/en/shared"
import models from "./locales/en/models"
import modelCompare from "./locales/en/modelCompare"
import automation from "./locales/en/automation"

const en = {
  common: {
    theme: "theme",
    light: "Light",
    dark: "Dark",
    toggleTheme: "Toggle theme",
    language: "language",
    switchLanguage: "Switch language",
    searchPlaceholder: "Search or run a command…",
    indexed: "indexed",
    engineeringWorkspace: "engineering workspace",
    never: "never",
    justNow: "just now",
    minutesAgo: "{{n}}m ago",
    hoursAgo: "{{n}}h ago",
    daysAgo: "{{n}}d ago",
    retry: "Retry",
    cancel: "Cancel",
    loading: "Loading…",
    files_one: "{{count}} file",
    files_other: "{{count}} files",
  },
  nav: {
    overview: "Overview",
    planRun: "Plan & run",
    understand: "Understand",
    measure: "Measure",
    settings: "Settings",
    aiModels: "AI models",
    automation: "Automation",
    modelComparison: "Model comparison",
    tasks: "Tasks",
    executions: "Executions",
    repository: "Repository",
    impactMap: "Impact map",
    projectBrain: "Project Brain",
    insights: "Insights",
    compareExecutions: "Compare executions",
    taskImpact: "Task & Impact Analysis",
    execution: "Execution",
    reviewDelivery: "Review & delivery",
  },
  command: {
    goOverview: "Go to Overview",
    openRepository: "Open Repository",
    repositoryHint: "Structure and analyzer state",
    viewTasks: "View Tasks",
    tasksHint: "Plan and approve changes",
    openBrain: "Open Project Brain",
    brainHint: "Ask the codebase",
    viewExecutions: "View Executions",
    executionsHint: "Runs and review",
    impactHint: "How changes ripple across layers",
    insightsHint: "Success, repair, time and cost",
    compareHint: "Original vs retry",
    modelsHint: "Add your own models and assign them to steps",
    automationHint: "Let DevPilot carry tasks through to a pull request",
    switchToTurkish: "Switch language to Türkçe",
    switchToEnglish: "Switch language to English",
    languageHint: "Interface language",
    groupNavigate: "Navigate",
    groupSettings: "Settings",
    noRepository: "No repository selected",
    openTask: "Open task: {{title}}",
    openExecution: "Open execution: {{title}}",
    placeholder: "Search pages, tasks, executions…",
    noMatches: "No matches for \"{{query}}\"",
    navigate: "navigate",
    open: "open",
  },
  overview,
  review,
  revision,
  impact,
  brain,
  arch,
  picker,
  repo,
  compare,
  insights,
  execWs,
  executions,
  shared,
  tasks,
  models,
  modelCompare,
  automation,
}

export type Resources = typeof en
export default en
