import { Link } from "react-router-dom"
import { ArrowRight, Boxes, FileSearch, GitPullRequest, ShieldCheck, Wrench } from "lucide-react"
import { Panel } from "@/components/ui/primitives"

const steps = [
  {
    icon: FileSearch,
    title: "Grounded plan",
    detail: "Impact analysis from the real symbol graph, with evidence and uncertainty. You approve before anything runs.",
    to: "/tasks",
  },
  {
    icon: Wrench,
    title: "Ordered generation + bounded repair",
    detail: "Dependency-ordered edits applied all-or-nothing; compile and test repair one file at a time.",
    to: "/executions",
  },
  {
    icon: ShieldCheck,
    title: "Baseline-aware verification",
    detail: "Failures are compared with the base commit, so pre-existing problems are never blamed on the change.",
    to: "/insights",
  },
  {
    icon: GitPullRequest,
    title: "Explained review + gated delivery",
    detail: "Predicted vs actual files, a plain-language verdict, then commit, PR, CI and a merge gate.",
    to: "/executions",
  },
]

/** Compact "how DevPilot ships a change" strip: the product's differentiators, linked to where they show up. */
export function PipelineStrip() {
  return (
    <Panel className="mb-6 p-4">
      <div className="mb-3 flex items-center gap-2">
        <Boxes className="h-4 w-4 text-primary" />
        <span className="text-[13px] font-semibold text-foreground">How DevPilot ships a change</span>
        <Link to="/insights" className="ml-auto flex items-center gap-1 text-[12px] font-medium text-primary hover:underline">
          See it measured
          <ArrowRight className="h-3 w-3" />
        </Link>
      </div>
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-4">
        {steps.map((step, index) => (
          <Link
            key={step.title}
            to={step.to}
            className="group rounded-[var(--radius-md)] border border-border bg-surface-2 p-3 transition-colors hover:border-border-strong hover:bg-surface"
          >
            <div className="flex items-center gap-2">
              <span className="flex h-5 w-5 items-center justify-center rounded-full bg-primary-soft font-mono text-[10.5px] font-semibold text-primary">
                {index + 1}
              </span>
              <step.icon className="h-3.5 w-3.5 text-subtle-foreground group-hover:text-foreground" />
              <span className="text-[12.5px] font-semibold text-foreground">{step.title}</span>
            </div>
            <p className="mt-1.5 text-[11.5px] leading-relaxed text-muted-foreground">{step.detail}</p>
          </Link>
        ))}
      </div>
    </Panel>
  )
}
