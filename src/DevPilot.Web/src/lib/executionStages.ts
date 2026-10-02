import i18n from "@/i18n"

export type StageKey = "analyze" | "plan" | "approved" | "implement" | "build" | "review" | "pr"

const stageKeys: StageKey[] = ["analyze", "plan", "approved", "implement", "build", "review", "pr"]

// `label` is a getter so it follows the active language on every read.
export const stages: { key: StageKey; label: string }[] = stageKeys.map((key) => ({
  key,
  get label() {
    return i18n.t(`shared.stage.${key}`)
  },
}))
