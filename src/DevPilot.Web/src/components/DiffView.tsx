import { Info } from "lucide-react"

export interface ParsedLine {
  id: number
  type: "header" | "hunk" | "add" | "del" | "context" | "info"
  content: string
  oldNo?: number
  newNo?: number
  filePath?: string
}

export function parseGitDiff(diffText: string): ParsedLine[] {
  if (!diffText) return []
  const rawLines = diffText.split("\n")
  const parsed: ParsedLine[] = []

  let currentOldLine: number | undefined = undefined
  let currentNewLine: number | undefined = undefined
  let currentFile: string | undefined = undefined

  for (let i = 0; i < rawLines.length; i++) {
    const line = rawLines[i]

    if (line.startsWith("diff --git ")) {
      const match = line.match(/b\/(.+)$/)
      if (match) {
        currentFile = match[1]
      }
    }

    if (
      line.startsWith("diff --git") ||
      line.startsWith("index ") ||
      line.startsWith("--- ") ||
      line.startsWith("+++ ") ||
      line.startsWith("old mode") ||
      line.startsWith("new mode") ||
      line.startsWith("new file") ||
      line.startsWith("deleted file") ||
      line.startsWith("similarity index") ||
      line.startsWith("rename from") ||
      line.startsWith("rename to")
    ) {
      parsed.push({
        id: i,
        type: "header",
        content: line,
        filePath: currentFile,
      })
      continue
    }

    if (line.startsWith("@@ ")) {
      const hunkMatch = line.match(/^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/)
      if (hunkMatch) {
        currentOldLine = parseInt(hunkMatch[1], 10)
        currentNewLine = parseInt(hunkMatch[2], 10)
      } else {
        currentOldLine = undefined
        currentNewLine = undefined
      }
      parsed.push({
        id: i,
        type: "hunk",
        content: line,
        filePath: currentFile,
      })
      continue
    }

    if (
      line.startsWith("[Redacted sensitive file content:") ||
      line.startsWith("[Binary file diff not shown:")
    ) {
      parsed.push({
        id: i,
        type: "info",
        content: line,
        filePath: currentFile,
      })
      continue
    }

    if (line.startsWith("+")) {
      parsed.push({
        id: i,
        type: "add",
        content: line,
        oldNo: undefined,
        newNo: currentNewLine !== undefined ? currentNewLine++ : undefined,
        filePath: currentFile,
      })
      continue
    }

    if (line.startsWith("-")) {
      parsed.push({
        id: i,
        type: "del",
        content: line,
        oldNo: currentOldLine !== undefined ? currentOldLine++ : undefined,
        newNo: undefined,
        filePath: currentFile,
      })
      continue
    }

    parsed.push({
      id: i,
      type: "context",
      content: line,
      oldNo: currentOldLine !== undefined ? currentOldLine++ : undefined,
      newNo: currentNewLine !== undefined ? currentNewLine++ : undefined,
      filePath: currentFile,
    })
  }

  return parsed
}

export function DiffRow({ line }: { line: ParsedLine }) {
  if (line.type === "hunk") {
    return (
      <div className="border-y border-primary/20 bg-primary-soft/40 px-3 py-1 font-mono text-[11px] text-primary">
        {line.content}
      </div>
    )
  }

  if (line.type === "header") {
    return (
      <div
        id={line.content.startsWith("diff --git") && line.filePath ? `file-diff-${line.filePath}` : undefined}
        className="border-b border-border/40 bg-surface-2 px-3 py-1 font-mono text-[11px] text-subtle-foreground"
      >
        {line.content}
      </div>
    )
  }

  if (line.type === "info") {
    return (
      <div className="flex items-center gap-2 border-y border-accent/20 bg-amber-soft/60 px-4 py-2 font-mono text-[12px] font-medium text-accent">
        <Info className="h-3.5 w-3.5 shrink-0" />
        <span>{line.content}</span>
      </div>
    )
  }

  const tone = line.type === "add" ? "bg-success-soft/60" : line.type === "del" ? "bg-danger-soft/60" : ""
  const sign = line.type === "add" ? "+" : line.type === "del" ? "−" : " "
  const signColor = line.type === "add" ? "text-success" : line.type === "del" ? "text-danger" : "text-subtle-foreground"

  const displayCode =
    line.content.length > 0 && (line.content[0] === "+" || line.content[0] === "-" || line.content[0] === " ")
      ? line.content.slice(1)
      : line.content

  return (
    <div className={"flex font-mono text-[12px] leading-[1.6] " + tone}>
      <span className="w-10 shrink-0 select-none border-r border-border/60 px-2 text-right text-subtle-foreground">
        {line.oldNo ?? ""}
      </span>
      <span className="w-10 shrink-0 select-none border-r border-border/60 px-2 text-right text-subtle-foreground">
        {line.newNo ?? ""}
      </span>
      <span className={"w-5 shrink-0 select-none text-center " + signColor}>{sign}</span>
      <code className="whitespace-pre pr-4 text-foreground">{displayCode || " "}</code>
    </div>
  )
}

/** A unified git diff with line numbers and +/- colouring. */
export function DiffView({ diff }: { diff: string }) {
  const lines = parseGitDiff(diff)
  return (
    <div className="overflow-x-auto">
      {lines.map((line) => (
        <DiffRow key={line.id} line={line} />
      ))}
    </div>
  )
}
