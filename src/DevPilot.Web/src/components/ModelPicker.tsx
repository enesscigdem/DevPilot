import { useEffect, useMemo, useRef, useState } from "react"
import { Check, ChevronDown } from "lucide-react"
import { useTranslation } from "react-i18next"
import { cn } from "@/lib/utils"
import type { AiModelOption } from "@/types"

const FAMILIES: { key: string; label: string; pattern: RegExp }[] = [
  { key: "claude", label: "Claude", pattern: /^claude/i },
  { key: "gpt", label: "GPT", pattern: /^(gpt|o\d|chatgpt)/i },
  { key: "gemini", label: "Gemini", pattern: /^gemini/i },
  { key: "deepseek", label: "DeepSeek", pattern: /^deepseek/i },
  { key: "qwen", label: "Qwen", pattern: /^qwen/i },
  { key: "kimi", label: "Kimi", pattern: /^kimi/i },
  { key: "glm", label: "GLM", pattern: /^glm/i },
  { key: "llama", label: "Llama", pattern: /^llama/i },
  { key: "mistral", label: "Mistral", pattern: /^(mistral|mixtral)/i },
  { key: "grok", label: "Grok", pattern: /^grok/i },
]

function familyOf(id: string): string {
  return FAMILIES.find((f) => f.pattern.test(id))?.key ?? "other"
}

/**
 * Model name field with a searchable, grouped list of the models a provider offers.
 * Typing still works for names that are not in the list.
 */
export function ModelPicker({
  value,
  onChange,
  options,
  placeholder,
  inputClassName,
}: {
  value: string
  onChange: (value: string) => void
  options: AiModelOption[]
  placeholder?: string
  inputClassName?: string
}) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  // Close when clicking anywhere outside the picker.
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener("mousedown", onDown)
    return () => document.removeEventListener("mousedown", onDown)
  }, [open])

  // Filter by what the user typed, but show everything when the field already holds an exact name.
  const filtered = useMemo(() => {
    const q = value.trim().toLowerCase()
    if (!q || options.some((o) => o.id.toLowerCase() === q)) return options
    return options.filter((o) => o.id.toLowerCase().includes(q) || (o.displayName ?? "").toLowerCase().includes(q))
  }, [options, value])

  const groups = useMemo(() => {
    const byFamily = new Map<string, AiModelOption[]>()
    for (const option of filtered) {
      const key = familyOf(option.id)
      byFamily.set(key, [...(byFamily.get(key) ?? []), option])
    }
    const known = FAMILIES.filter((f) => byFamily.has(f.key)).map((f) => ({ key: f.key, label: f.label, items: byFamily.get(f.key)! }))
    const other = byFamily.get("other")
    return other ? [...known, { key: "other", label: t("models.form.otherModels"), items: other }] : known
  }, [filtered, t])

  // Flat order matches what is rendered, so keyboard navigation follows the visible list.
  const flat = useMemo(() => groups.flatMap((g) => g.items), [groups])

  useEffect(() => setActive(0), [value, options])

  useEffect(() => {
    if (!open) return
    listRef.current?.querySelector<HTMLElement>(`[data-index="${active}"]`)?.scrollIntoView({ block: "nearest" })
  }, [active, open])

  const choose = (id: string) => {
    onChange(id)
    setOpen(false)
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (options.length === 0) return
    if (e.key === "ArrowDown") {
      e.preventDefault()
      setOpen(true)
      setActive((i) => Math.min(i + 1, Math.max(flat.length - 1, 0)))
    } else if (e.key === "ArrowUp") {
      e.preventDefault()
      setActive((i) => Math.max(i - 1, 0))
    } else if (e.key === "Enter" && open && flat[active]) {
      e.preventDefault()
      choose(flat[active].id)
    } else if (e.key === "Escape") {
      setOpen(false)
    }
  }

  let index = -1

  return (
    <div ref={rootRef} className="relative min-w-0 flex-1">
      <input
        className={cn(inputClassName, options.length > 0 && "pr-8")}
        value={value}
        placeholder={placeholder}
        onChange={(e) => {
          onChange(e.target.value)
          if (options.length > 0) setOpen(true)
        }}
        onFocus={() => options.length > 0 && setOpen(true)}
        onKeyDown={onKeyDown}
        role="combobox"
        aria-expanded={open}
        aria-autocomplete="list"
        autoComplete="off"
      />
      {options.length > 0 && (
        <button
          type="button"
          tabIndex={-1}
          aria-label={t("models.form.toggleList")}
          onClick={() => setOpen((o) => !o)}
          className="absolute right-1.5 top-1/2 -translate-y-1/2 rounded p-1 text-subtle-foreground hover:text-foreground"
        >
          <ChevronDown className={cn("h-4 w-4 transition-transform", open && "rotate-180")} />
        </button>
      )}

      {open && options.length > 0 && (
        <div
          ref={listRef}
          role="listbox"
          className="absolute left-0 right-0 z-50 mt-1.5 max-h-72 overflow-y-auto rounded-[var(--radius-md)] border border-border-strong bg-surface py-1 shadow-[var(--shadow-lg)]"
        >
          {flat.length === 0 && <div className="px-3 py-3 text-[12.5px] text-subtle-foreground">{t("models.form.noModelMatches")}</div>}
          {groups.map((group) => (
            <div key={group.key}>
              <div className="tech-label sticky top-0 bg-surface px-3 py-1.5">{group.label}</div>
              {group.items.map((option) => {
                index += 1
                const i = index
                const selected = option.id === value
                return (
                  <div
                    key={option.id}
                    role="option"
                    aria-selected={selected}
                    data-index={i}
                    // mousedown, not click: the input must not lose focus before the choice registers.
                    onMouseDown={(e) => {
                      e.preventDefault()
                      choose(option.id)
                    }}
                    onMouseEnter={() => setActive(i)}
                    className={cn(
                      "flex cursor-pointer items-center gap-2 px-3 py-1.5",
                      i === active ? "bg-surface-3" : "hover:bg-surface-2",
                    )}
                  >
                    <div className="min-w-0 flex-1">
                      <div className="truncate text-[13px] font-medium text-foreground">{option.displayName ?? option.id}</div>
                      {option.displayName && option.displayName !== option.id && (
                        <div className="truncate font-mono text-[11px] text-subtle-foreground">{option.id}</div>
                      )}
                    </div>
                    {selected && <Check className="h-3.5 w-3.5 shrink-0 text-primary" />}
                  </div>
                )
              })}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
