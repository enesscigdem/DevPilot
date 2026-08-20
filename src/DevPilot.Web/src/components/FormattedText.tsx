import React from "react"
import { cn } from "@/lib/utils"

interface FormattedTextProps {
  text: string
  className?: string
}

interface InlineSpan {
  type: "text" | "code" | "bold" | "italic"
  content: string
}

function parseInlineFormatting(text: string): React.ReactNode[] {
  // Regex to match `code`, **bold**, *italic*
  const pattern = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*)/g
  const parts = text.split(pattern)

  return parts.map((part, index) => {
    if (!part) return null

    if (part.startsWith("`") && part.endsWith("`") && part.length >= 2) {
      return (
        <code
          key={index}
          className="rounded border border-border/60 bg-surface-2 px-1 py-0.5 font-mono text-[11.5px] text-foreground"
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

    return <React.Fragment key={index}>{part}</React.Fragment>
  })
}

export function FormattedText({ text, className }: FormattedTextProps) {
  if (!text) {
    return <span className="text-muted-foreground">No content provided.</span>
  }

  const lines = text.split("\n")
  const elements: React.ReactNode[] = []

  let inCodeBlock = false
  let codeBlockLanguage = ""
  let codeBlockLines: string[] = []
  let inBulletList = false
  let bulletListItems: React.ReactNode[] = []
  let inNumberedList = false
  let numberedListItems: React.ReactNode[] = []

  const flushBulletList = (keyPrefix: number) => {
    if (bulletListItems.length > 0) {
      elements.push(
        <ul key={`ul-${keyPrefix}`} className="my-2 ml-4 list-disc space-y-1 text-[12.5px] leading-relaxed text-foreground">
          {bulletListItems}
        </ul>
      )
      bulletListItems = []
      inBulletList = false
    }
  }

  const flushNumberedList = (keyPrefix: number) => {
    if (numberedListItems.length > 0) {
      elements.push(
        <ol key={`ol-${keyPrefix}`} className="my-2 ml-4 list-decimal space-y-1 text-[12.5px] leading-relaxed text-foreground">
          {numberedListItems}
        </ol>
      )
      numberedListItems = []
      inNumberedList = false
    }
  }

  const flushCodeBlock = (keyPrefix: number) => {
    if (inCodeBlock) {
      const codeContent = codeBlockLines.join("\n")
      elements.push(
        <div key={`codeblock-${keyPrefix}`} className="my-2 overflow-hidden rounded-[var(--radius-md)] border border-border bg-surface-3">
          {codeBlockLanguage && (
            <div className="border-b border-border/50 px-2.5 py-1 font-mono text-[10px] uppercase tracking-wider text-subtle-foreground">
              {codeBlockLanguage}
            </div>
          )}
          <pre className="overflow-x-auto p-2.5 font-mono text-[11.5px] leading-relaxed text-foreground">
            <code>{codeContent}</code>
          </pre>
        </div>
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
        flushBulletList(i)
        flushNumberedList(i)
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

    // Horizontal Rule
    if (/^(\*\*\*|---|___|===+)$/.test(trimmed)) {
      flushBulletList(i)
      flushNumberedList(i)
      elements.push(<hr key={`hr-${i}`} className="my-3 border-t border-border" />)
      continue
    }

    // Headings
    if (trimmed.startsWith("# ")) {
      flushBulletList(i)
      flushNumberedList(i)
      elements.push(
        <h1 key={`h1-${i}`} className="mt-3.5 mb-1.5 text-[14.5px] font-semibold tracking-tight text-foreground first:mt-0">
          {parseInlineFormatting(trimmed.slice(2))}
        </h1>
      )
      continue
    }

    if (trimmed.startsWith("## ")) {
      flushBulletList(i)
      flushNumberedList(i)
      elements.push(
        <h2 key={`h2-${i}`} className="mt-3 mb-1 text-[13.5px] font-semibold tracking-tight text-foreground first:mt-0">
          {parseInlineFormatting(trimmed.slice(3))}
        </h2>
      )
      continue
    }

    if (trimmed.startsWith("### ")) {
      flushBulletList(i)
      flushNumberedList(i)
      elements.push(
        <h3 key={`h3-${i}`} className="mt-2.5 mb-1 text-[13px] font-semibold text-foreground first:mt-0">
          {parseInlineFormatting(trimmed.slice(4))}
        </h3>
      )
      continue
    }

    if (trimmed.startsWith("#### ")) {
      flushBulletList(i)
      flushNumberedList(i)
      elements.push(
        <h4 key={`h4-${i}`} className="mt-2 mb-0.5 text-[12.5px] font-semibold text-foreground first:mt-0">
          {parseInlineFormatting(trimmed.slice(5))}
        </h4>
      )
      continue
    }

    // Bullet lists
    const bulletMatch = /^[*-]\s+(.+)$/.exec(trimmed)
    if (bulletMatch) {
      flushNumberedList(i)
      inBulletList = true
      bulletListItems.push(
        <li key={`li-${i}`} className="min-w-0 break-words">
          {parseInlineFormatting(bulletMatch[1])}
        </li>
      )
      continue
    }

    // Numbered lists
    const numberMatch = /^(\d+)\.\s+(.+)$/.exec(trimmed)
    if (numberMatch) {
      flushBulletList(i)
      inNumberedList = true
      numberedListItems.push(
        <li key={`li-num-${i}`} className="min-w-0 break-words">
          {parseInlineFormatting(numberMatch[2])}
        </li>
      )
      continue
    }

    // Non-list line, flush lists if needed
    flushBulletList(i)
    flushNumberedList(i)

    // Empty line / line break
    if (trimmed === "") {
      elements.push(<div key={`blank-${i}`} className="h-2" />)
      continue
    }

    // Regular paragraph
    elements.push(
      <p key={`p-${i}`} className="my-1 text-[12.5px] leading-relaxed text-foreground text-pretty break-words min-w-0">
        {parseInlineFormatting(rawLine)}
      </p>
    )
  }

  flushCodeBlock(lines.length)
  flushBulletList(lines.length)
  flushNumberedList(lines.length)

  return <div className={cn("space-y-0.5 min-w-0 overflow-x-hidden", className)}>{elements}</div>
}
