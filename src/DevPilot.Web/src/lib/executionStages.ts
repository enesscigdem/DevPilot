export type StageKey = "analyze" | "plan" | "approved" | "implement" | "build" | "review" | "pr"

export const stages: { key: StageKey; label: string }[] = [
  { key: "analyze", label: "Analyze" },
  { key: "plan", label: "Plan" },
  { key: "approved", label: "Approved" },
  { key: "implement", label: "Implement" },
  { key: "build", label: "Build & Test" },
  { key: "review", label: "Review" },
  { key: "pr", label: "Pull Request" },
]
