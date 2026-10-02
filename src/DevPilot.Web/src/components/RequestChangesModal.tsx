import { useState } from "react"
import { useTranslation } from "react-i18next"
import { GitPullRequest, Loader2, MessageSquareWarning, RotateCw, ShieldOff, Wrench } from "lucide-react"
import { Button } from "@/components/ui/primitives"

const MAX_LENGTH = 2000

interface RequestChangesModalProps {
  /** Number of the open pull request, when there is one, so the user knows it is updated rather than replaced. */
  pullRequestNumber?: number | null
  isSubmitting: boolean
  error: string | null
  onClose: () => void
  onSubmit: (feedback: string) => void
}

/** Collects the reviewer's feedback and says plainly what will happen to the branch, checks and approval. */
export function RequestChangesModal({ pullRequestNumber, isSubmitting, error, onClose, onSubmit }: RequestChangesModalProps) {
  const { t } = useTranslation()
  const [feedback, setFeedback] = useState("")
  const trimmed = feedback.trim()

  const effects: { icon: typeof Wrench; text: string }[] = [
    { icon: GitPullRequest, text: pullRequestNumber != null ? t("revision.modal.effectPr", { n: pullRequestNumber }) : t("revision.modal.effect1") },
    { icon: RotateCw, text: t("revision.modal.effect2") },
    { icon: ShieldOff, text: t("revision.modal.effect3") },
  ]
  if (pullRequestNumber != null) {
    effects.splice(1, 0, { icon: Wrench, text: t("revision.modal.effect1") })
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4 backdrop-blur-xs">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t("revision.modal.title")}
        className="w-full max-w-[540px] space-y-4 rounded-[var(--radius-lg)] border border-border bg-canvas p-6 shadow-xl"
      >
        <div className="space-y-1">
          <h3 className="flex items-center gap-2 text-[15px] font-semibold text-foreground">
            <MessageSquareWarning className="h-4 w-4 text-primary" />
            {t("revision.modal.title")}
          </h3>
          <p className="text-[12.5px] leading-relaxed text-muted-foreground">{t("revision.modal.desc")}</p>
        </div>

        <div>
          <textarea
            autoFocus
            className="h-32 w-full resize-none rounded-[var(--radius-md)] border border-border bg-surface p-3 text-[13px] text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
            placeholder={t("revision.modal.placeholder")}
            maxLength={MAX_LENGTH}
            value={feedback}
            onChange={(e) => setFeedback(e.target.value)}
            onKeyDown={(e) => {
              if ((e.ctrlKey || e.metaKey) && e.key === "Enter" && trimmed && !isSubmitting) onSubmit(trimmed)
            }}
          />
          <div className="mt-1 text-right font-mono text-[11px] text-subtle-foreground">
            {t("revision.modal.counter", { n: feedback.length, max: MAX_LENGTH })}
          </div>
        </div>

        <div className="space-y-1.5 rounded-[var(--radius-md)] border border-border bg-surface-2 p-3">
          <div className="tech-label">{t("revision.modal.effects")}</div>
          <ul className="space-y-1.5">
            {effects.map(({ icon: Icon, text }) => (
              <li key={text} className="flex items-start gap-2 text-[12px] text-muted-foreground">
                <Icon className="mt-0.5 h-3.5 w-3.5 shrink-0 text-subtle-foreground" />
                <span>{text}</span>
              </li>
            ))}
          </ul>
        </div>

        {error && <div className="rounded-[var(--radius-md)] bg-danger-soft/60 p-2 text-[12px] text-danger">{error}</div>}

        <div className="flex items-center justify-end gap-3">
          <Button variant="default" size="sm" disabled={isSubmitting} onClick={onClose}>
            {t("revision.modal.cancel")}
          </Button>
          <Button variant="primary" size="sm" disabled={isSubmitting || trimmed.length === 0} onClick={() => onSubmit(trimmed)}>
            {isSubmitting ? (
              <>
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
                {t("revision.modal.sending")}
              </>
            ) : (
              t("revision.modal.send")
            )}
          </Button>
        </div>
      </div>
    </div>
  )
}
