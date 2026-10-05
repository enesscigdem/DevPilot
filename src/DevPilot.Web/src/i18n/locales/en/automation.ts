const automation = {
  eyebrow: "Settings",
  title: "Automation",
  description:
    "Decide how far DevPilot may carry a task on its own in this repository. Every automatic step goes through the same checks a person would trigger, and anything that is not clearly safe is left for you with the reason.",
  loading: "Loading automation settings…",
  errLoad: "Automation settings could not be loaded.",
  errSave: "Automation settings could not be saved.",
  noWorkspace: "Select a repository first. Automation is configured per repository.",
  repository: "Repository",
  save: "Save changes",
  saving: "Saving…",
  saved: "Saved.",
  levelHeading: "How much should DevPilot do on its own?",
  levels: {
    Manual: {
      name: "Manual",
      summary: "You trigger every step.",
      does: "Nothing automatic. This is how DevPilot works without automation.",
    },
    SemiAuto: {
      name: "Plan and run",
      summary: "Approves the plan and starts the work.",
      does: "Approves the plan of a new task and starts the execution. You still review the finished change.",
    },
    AutoPr: {
      name: "Deliver as pull request",
      summary: "Opens a pull request for safe changes.",
      does: "Also approves a finished change that passes every safety check, then commits, pushes and opens the pull request. You merge it.",
    },
    FullAuto: {
      name: "Fully automatic",
      summary: "Merges once CI is green.",
      does: "Also merges the pull request when CI passes and the change is unchanged since approval.",
    },
  },
  activeSince: "Automation acts on tasks created after {{date}}. Earlier tasks stay as they are.",
  paused: {
    title: "Pause automation",
    body: "Stops every automatic action immediately. Your settings are kept.",
    notice: "Automation is paused. Nothing happens on its own until you resume it.",
  },
  safetyHeading: "Safety checks",
  safetyIntro:
    "A change is only delivered automatically when it is fully verified: build and tests pass, no test was weakened, the UI is untouched and no secret file is involved. A change that fails a check waits for you, with the reason shown on the execution.",
  limits: {
    maxFiles: "Largest change (files)",
    maxFilesHint: "Bigger changes are left for you.",
    maxLines: "Largest change (lines)",
    maxLinesHint: "Added plus deleted lines.",
    maxParallel: "Tasks running at once",
    maxParallelHint: "Higher is faster but uses more of your model's rate limit.",
  },
  protectedHeading: "Protected paths",
  protectedHint:
    "One pattern per line. A change touching a match is always left for you. ** matches any folder depth, * matches within one folder.",
  requireCi: {
    label: "Merge only when CI checks passed",
    hint: "A repository without any CI checks is never merged automatically while this is on.",
  },
  errRange: "Limits are out of range.",
}

export default automation
