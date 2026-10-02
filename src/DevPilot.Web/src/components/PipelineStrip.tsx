import { Link } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { ArrowRight, Boxes, FileSearch, GitPullRequest, ShieldCheck, Wrench } from "lucide-react"
import { Panel } from "@/components/ui/primitives"

const steps = [
  {
    icon: FileSearch,
    title: "shared.pipeline.s1Title",
    detail: "shared.pipeline.s1Detail",
    to: "/tasks",
  },
  {
    icon: Wrench,
    title: "shared.pipeline.s2Title",
    detail: "shared.pipeline.s2Detail",
    to: "/executions",
  },
  {
    icon: ShieldCheck,
    title: "shared.pipeline.s3Title",
    detail: "shared.pipeline.s3Detail",
    to: "/insights",
  },
  {
    icon: GitPullRequest,
    title: "shared.pipeline.s4Title",
    detail: "shared.pipeline.s4Detail",
    to: "/executions",
  },
]

/** Compact "how DevPilot ships a change" strip: the product's differentiators, linked to where they show up. */
export function PipelineStrip() {
  const { t } = useTranslation()
  return (
    <Panel className="mb-6 p-4">
      <div className="mb-3 flex items-center gap-2">
        <Boxes className="h-4 w-4 text-primary" />
        <span className="text-[13px] font-semibold text-foreground">{t("shared.pipeline.title")}</span>
        <Link to="/insights" className="ml-auto flex items-center gap-1 text-[12px] font-medium text-primary hover:underline">
          {t("shared.pipeline.seeMeasured")}
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
              <span className="text-[12.5px] font-semibold text-foreground">{t(step.title)}</span>
            </div>
            <p className="mt-1.5 text-[11.5px] leading-relaxed text-muted-foreground">{t(step.detail)}</p>
          </Link>
        ))}
      </div>
    </Panel>
  )
}
