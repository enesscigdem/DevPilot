import i18n from "@/i18n"
import React from "react"
import { cn } from "@/lib/utils"

interface FormattedTextProps {
  text: string
  className?: string
  /** "sm" suits dense panels; "md" is for reading answers (larger text and headings). */
  size?: "sm" | "md"
}

const SIZES = {
  sm: { body: "text-[12.5px]", h1: "text-[14.5px]", h2: "text-[13.5px]", h3: "text-[13px]", h4: "text-[12.5px]", code: "text-[11.5px]", cell: "text-[11.5px]" },
  md: { body: "text-[14px]", h1: "text-[18px]", h2: "text-[16px]", h3: "text-[14.5px]", h4: "text-[14px]", code: "text-[12.5px]", cell: "text-[12.5px]" },
} as const

/** Inline: `code`, **bold**, *italic*, [text](https://link). */
function parseInlineFormatting(text: string, codeSize: string): React.ReactNode[] {
  const pattern = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*|\[[^\]]+\]\([^)\s]+\))/g
  const parts = text.split(pattern)

  return parts.map((part, index) => {
    if (!part) return null

    if (part.startsWith("`") && part.endsWith("`") && part.length >= 2) {
      return (
        <code
          key={index}
          className={cn("rounded border border-border/60 bg-surface-2 px-1 py-0.5 font-mono text-foreground", codeSize)}
        >
          {part.slice(1, -1)}
        </code>
      )
    }

    if (part.startsWith("**") && part.endsWith("**") && part.length >= 4) {
      return (
        <strong key={index} className="font-semibold text-foreground">
          {part.slice(2, -2)}
        </strong>
      )
    }

    if (part.startsWith("*") && part.endsWith("*") && part.length >= 2) {
      return (
        <em key={index} className="italic">
          {part.slice(1, -1)}
        </em>
      )
    }

    const link = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(part)
    if (link) {
      // Only web links are made clickable; anything else (javascript:, data:) stays plain text.
      return /^https?:\/\//i.test(link[2]) ? (
        <a key={index} href={link[2]} target="_blank" rel="noreferrer noopener" className="text-primary underline-offset-2 hover:underline">
          {link[1]}
        </a>
      ) : (
        <React.Fragment key={index}>{link[1]}</React.Fragment>
      )
    }

    return <React.Fragment key={index}>{part}</React.Fragment>
  })
}

const TABLE_ROW = /^\s*\|.*\|\s*$/
const TABLE_SEPARATOR = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/

function splitRow(line: string): string[] {
  return line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map((cell) => cell.trim())
}

export function FormattedText({ text, className, size = "sm" }: FormattedTextProps) {
  if (!text) {
    return <span className="text-muted-foreground">{i18n.t("shared.noContent")}</span>
  }

  const s = SIZES[size]
  const inline = (value: string) => parseInlineFormatting(value, s.code)
  const lines = text.split("\n")
  const elements: React.ReactNode[] = []

  let inCodeBlock = false
  let codeBlockLanguage = ""
  let codeBlockLines: string[] = []
  let bulletListItems: React.ReactNode[] = []
  let numberedListItems: React.ReactNode[] = []

  const indentOf = (raw: string) => Math.min(3, Math.floor((raw.length - raw.trimStart().length) / 2))

  const flushBulletList = (keyPrefix: number) => {
    if (bulletListItems.length > 0) {
      elements.push(
        <ul key={`ul-${keyPrefix}`} className={cn("my-2 ml-5 list-disc space-y-1 leading-relaxed text-foreground", s.body)}>
          {bulletListItems}
        </ul>,
      )
      bulletListItems = []
    }
  }

  const flushNumberedList = (keyPrefix: number) => {
    if (numberedListItems.length > 0) {
      elements.push(
        <ol key={`ol-${keyPrefix}`} className={cn("my-2 ml-5 list-decimal space-y-1 leading-relaxed text-foreground", s.body)}>
          {numberedListItems}
        </ol>,
      )
      numberedListItems = []
    }
  }

  const flushLists = (i: number) => {
    flushBulletList(i)
    flushNumberedList(i)
  }

  const flushCodeBlock = (keyPrefix: number) => {
    if (inCodeBlock) {
      elements.push(
        <div key={`codeblock-${keyPrefix}`} className="my-2 overflow-hidden rounded-[var(--radius-md)] border border-border bg-surface-3">
          {codeBlockLanguage && (
            <div className="border-b border-border/50 px-2.5 py-1 font-mono text-[10px] uppercase tracking-wider text-subtle-foreground">
              {codeBlockLanguage}
            </div>
          )}
          <pre className={cn("overflow-x-auto p-2.5 font-mono leading-relaxed text-foreground", s.code)}>
            <code>{codeBlockLines.join("\n")}</code>
          </pre>
        </div>,
      )
      codeBlockLines = []
      codeBlockLanguage = ""
      inCodeBlock = false
    }
  }

  for (let i = 0; i < lines.length; i++) {
    const rawLine = lines[i]
    const trimmed = rawLine.trim()

    // Fenced code block boundary
    if (trimmed.startsWith("```")) {
      if (inCodeBlock) {
        flushCodeBlock(i)
      } else {
        flushLists(i)
        inCodeBlock = true
        codeBlockLanguage = trimmed.slice(3).trim()
        codeBlockLines = []
      }
      continue
    }

    if (inCodeBlock) {
      codeBlockLines.push(rawLine)
      continue
    }

    // Table: a pipe row followed by a separator row (| --- | --- |)
    if (TABLE_ROW.test(rawLine) && i + 1 < lines.length && TABLE_SEPARATOR.test(lines[i + 1])) {
      flushLists(i)
      const header = splitRow(rawLine)
      const rows: string[][] = []
      let j = i + 2
      while (j < lines.length && TABLE_ROW.test(lines[j])) {
        rows.push(splitRow(lines[j]))
        j++
      }
      elements.push(
        <div key={`table-${i}`} className="my-2.5 overflow-x-auto rounded-[var(--radius-md)] border border-border">
          <table className={cn("w-full border-collapse text-left", s.cell)}>
            <thead className="bg-surface-2">
              <tr>
                {header.map((cell, c) => (
                  <th key={c} className="border-b border-border px-3 py-2 font-semibold text-foreground">
                    {inline(cell)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row, r) => (
                <tr key={r} className="border-b border-border/60 last:border-b-0">
                  {row.map((cell, c) => (
                    <td key={c} className="px-3 py-1.5 align-top text-foreground">
                      {inline(cell)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>,
      )
      i = j - 1
      continue
    }

    // Horizontal Rule
    if (/^(\*\*\*|---|___|===+)$/.test(trimmed)) {
      flushLists(i)
      elements.push(<hr key={`hr-${i}`} className="my-3 border-t border-border" />)
      continue
    }

    // Headings (longest marker first so "#### " is not read as "# ")
    const heading = /^(#{1,4})\s+(.+)$/.exec(trimmed)
    if (heading) {
      flushLists(i)
      const level = heading[1].length
      const cls = [
        "",
        cn("mt-4 mb-2 font-semibold tracking-tight text-foreground first:mt-0", s.h1),
        cn("mt-4 mb-1.5 font-semibold tracking-tight text-foreground first:mt-0", s.h2),
        cn("mt-3 mb-1 font-semibold text-foreground first:mt-0", s.h3),
        cn("mt-2.5 mb-0.5 font-semibold text-foreground first:mt-0", s.h4),
      ][level]
      const Tag = (`h${level}` as "h1" | "h2" | "h3" | "h4")
      elements.push(
        <Tag key={`h-${i}`} className={cls}>
          {inline(heading[2])}
        </Tag>,
      )
      continue
    }

    // Blockquote
    const quote = /^>\s?(.*)$/.exec(trimmed)
    if (quote) {
      flushLists(i)
      elements.push(
        <blockquote key={`q-${i}`} className={cn("my-2 border-l-2 border-primary-ring pl-3 italic leading-relaxed text-muted-foreground", s.body)}>
          {inline(quote[1])}
        </blockquote>,
      )
      continue
    }

    // Bullet lists (leading spaces nest the item)
    const bulletMatch = /^[*-]\s+(.+)$/.exec(trimmed)
    if (bulletMatch) {
      flushNumberedList(i)
      bulletListItems.push(
        <li key={`li-${i}`} className="min-w-0 break-words" style={{ marginLeft: indentOf(rawLine) * 16 }}>
          {inline(bulletMatch[1])}
        </li>,
      )
      continue
    }

    // Numbered lists
    const numberMatch = /^(\d+)\.\s+(.+)$/.exec(trimmed)
    if (numberMatch) {
      flushBulletList(i)
      numberedListItems.push(
        <li key={`li-num-${i}`} className="min-w-0 break-words" style={{ marginLeft: indentOf(rawLine) * 16 }}>
          {inline(numberMatch[2])}
        </li>,
      )
      continue
    }

    // Non-list line, flush lists if needed
    flushLists(i)

    // Empty line / line break
    if (trimmed === "") {
      elements.push(<div key={`blank-${i}`} className="h-2" />)
      continue
    }

    // Regular paragraph
    elements.push(
      <p key={`p-${i}`} className={cn("my-1 min-w-0 break-words leading-relaxed text-foreground text-pretty", s.body)}>
        {inline(rawLine)}
      </p>,
    )
  }

  flushCodeBlock(lines.length)
  flushLists(lines.length)

  return <div className={cn("space-y-0.5 min-w-0 overflow-x-hidden", className)}>{elements}</div>
}
