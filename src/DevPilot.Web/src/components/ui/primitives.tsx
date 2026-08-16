import { forwardRef, type ButtonHTMLAttributes, type HTMLAttributes, type ReactNode } from "react"
import { cn } from "@/lib/utils"

/* ---------------------------------- Button --------------------------------- */

type ButtonVariant = "primary" | "default" | "ghost" | "subtle" | "danger" | "accent"
type ButtonSize = "sm" | "md" | "lg" | "icon"

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: ButtonSize
}

const buttonVariants: Record<ButtonVariant, string> = {
  primary:
    "bg-primary text-primary-foreground hover:bg-primary-hover shadow-[var(--shadow-sm)] border border-transparent",
  default:
    "bg-surface text-foreground border border-border-strong hover:bg-surface-2 shadow-[var(--shadow-sm)]",
  subtle: "bg-surface-3 text-foreground border border-transparent hover:bg-border",
  ghost: "bg-transparent text-muted-foreground hover:bg-surface-3 hover:text-foreground border border-transparent",
  danger: "bg-transparent text-danger border border-border-strong hover:bg-danger-soft hover:border-danger/40",
  accent: "bg-accent text-accent-foreground hover:brightness-105 border border-transparent",
}

const buttonSizes: Record<ButtonSize, string> = {
  sm: "h-7 px-2.5 text-[12px] gap-1.5 rounded-[var(--radius-sm)]",
  md: "h-8.5 px-3 text-[13px] gap-2 rounded-[var(--radius-md)]",
  lg: "h-10 px-4 text-sm gap-2 rounded-[var(--radius-md)]",
  icon: "h-8 w-8 rounded-[var(--radius-md)]",
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ variant = "default", size = "md", className, ...props }, ref) => (
    <button
      ref={ref}
      className={cn(
        "inline-flex items-center justify-center whitespace-nowrap font-medium transition-colors outline-none focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:ring-offset-0 disabled:opacity-45 disabled:pointer-events-none",
        buttonVariants[variant],
        buttonSizes[size],
        className,
      )}
      {...props}
    />
  ),
)
Button.displayName = "Button"

/* ----------------------------------- Badge --------------------------------- */

type Tone = "neutral" | "blue" | "amber" | "green" | "red" | "gray"

const toneStyles: Record<Tone, string> = {
  neutral: "bg-surface-3 text-muted-foreground border-border",
  gray: "bg-surface-3 text-muted-foreground border-border",
  blue: "bg-primary-soft text-primary border-primary-ring/60",
  amber: "bg-accent-soft text-accent border-accent-line/60",
  green: "bg-success-soft text-success border-success/25",
  red: "bg-danger-soft text-danger border-danger/25",
}

export function Badge({
  tone = "neutral",
  className,
  children,
  mono,
}: {
  tone?: Tone
  className?: string
  children: ReactNode
  mono?: boolean
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium leading-none",
        mono && "font-mono tracking-tight",
        toneStyles[tone],
        className,
      )}
    >
      {children}
    </span>
  )
}

/* --------------------------------- StatusDot ------------------------------- */

export function StatusDot({
  tone = "neutral",
  pulse,
  className,
}: {
  tone?: Tone
  pulse?: boolean
  className?: string
}) {
  const color: Record<Tone, string> = {
    neutral: "bg-subtle-foreground",
    gray: "bg-subtle-foreground",
    blue: "bg-primary",
    amber: "bg-accent",
    green: "bg-success",
    red: "bg-danger",
  }
  return (
    <span className={cn("relative inline-flex h-2 w-2 shrink-0", className)}>
      {pulse && (
        <span className={cn("absolute inset-0 rounded-full opacity-40 animate-pulse-dot", color[tone])} />
      )}
      <span className={cn("relative inline-flex h-2 w-2 rounded-full", color[tone])} />
    </span>
  )
}

/* ----------------------------------- Card ---------------------------------- */

export function Panel({
  className,
  children,
  ...props
}: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        "rounded-[var(--radius-lg)] border border-border bg-surface shadow-[var(--shadow-sm)]",
        className,
      )}
      {...props}
    >
      {children}
    </div>
  )
}

export function SectionLabel({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("tech-label", className)}>{children}</div>
}

/* ------------------------------------ Kbd ---------------------------------- */

export function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="inline-flex h-5 min-w-[20px] items-center justify-center rounded border border-border bg-surface-2 px-1.5 font-mono text-[10.5px] font-medium text-muted-foreground shadow-[var(--shadow-sm)]">
      {children}
    </kbd>
  )
}

/* ---------------------------------- Meter ---------------------------------- */

export function Meter({
  value,
  tone = "blue",
  className,
}: {
  value: number
  tone?: Tone
  className?: string
}) {
  const barColor: Record<Tone, string> = {
    neutral: "bg-subtle-foreground",
    gray: "bg-subtle-foreground",
    blue: "bg-primary",
    amber: "bg-accent",
    green: "bg-success",
    red: "bg-danger",
  }
  return (
    <div className={cn("h-1.5 w-full overflow-hidden rounded-full bg-surface-3", className)}>
      <div
        className={cn("h-full rounded-full transition-all duration-500", barColor[tone])}
        style={{ width: `${Math.min(100, Math.max(0, value))}%` }}
      />
    </div>
  )
}

/* --------------------------------- IconChip -------------------------------- */

export function IconChip({
  children,
  tone = "neutral",
  className,
}: {
  children: ReactNode
  tone?: Tone
  className?: string
}) {
  const styles: Record<Tone, string> = {
    neutral: "bg-surface-3 text-muted-foreground",
    gray: "bg-surface-3 text-muted-foreground",
    blue: "bg-primary-soft text-primary",
    amber: "bg-accent-soft text-accent",
    green: "bg-success-soft text-success",
    red: "bg-danger-soft text-danger",
  }
  return (
    <span
      className={cn(
        "inline-flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] border border-border/60",
        styles[tone],
        className,
      )}
    >
      {children}
    </span>
  )
}
