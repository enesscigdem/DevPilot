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
  conflict: {
    heading: "When two tasks change the same file",
    intro:
      "Goals run several tasks at once. This decides what happens when two of them would change the same file. DevPilot can tell which files a task changes, but not which lines, so the modes differ in how much risk of a merge conflict you accept for speed.",
    recommended: "Recommended",
    modes: {
      Careful: {
        name: "Careful",
        summary: "The later task waits until the earlier one is merged.",
        does: "Never any merge conflict between tasks of a goal, but tasks that share a file run one after the other.",
      },
      Balanced: {
        name: "Balanced",
        summary: "Share ordinary files, wait where a merge would fail anyway.",
        does: "Tasks run together on ordinary files, because git merges changes to different parts of a file by itself. They still wait for dependency and project files, lock files, migrations, generated files, and files one of them creates or deletes.",
      },
      Fast: {
        name: "Fast",
        summary: "Tasks never wait because of shared files.",
        does: "Maximum speed. If two pull requests collide, you resolve the conflict in GitHub.",
      },
    },
    note: "If two pull requests do collide, DevPilot cannot merge the second one by itself. It stays open and you resolve the conflict in GitHub.",
  },
}

export default automation
