export interface ParsedRemote {
  host: string
  owner: string
  repository: string
}

export type RemoteProvider = "GitHub" | "GitLab" | "AzureDevOps" | "Bitbucket" | "Generic"

const AZURE_HOST = "dev.azure.com"
const BITBUCKET_HOST = "bitbucket.org"

export function isAzureHost(host: string): boolean {
  return host === AZURE_HOST || host === `ssh.${AZURE_HOST}` || host.endsWith(".visualstudio.com")
}

/** Same rule as the server: the provider follows from the host. */
export function inferProvider(host: string): RemoteProvider {
  if (host === "github.com") return "GitHub"
  if (isAzureHost(host)) return "AzureDevOps"
  if (host === BITBUCKET_HOST) return "Bitbucket"
  return host.includes("gitlab") ? "GitLab" : "Generic"
}

function stripGit(name: string): string {
  return name.toLowerCase().endsWith(".git") ? name.slice(0, -4) : name
}

/**
 * Mirrors the server parser: https/ssh/scp-style remotes of any host, nested namespaces allowed. Azure DevOps addresses
 * (dev.azure.com/org/project/_git/repo, org.visualstudio.com/project/_git/repo, ssh v3) all read as organization/project.
 */
export function parseRemoteUrl(raw: string): ParsedRemote | null {
  const value = raw.trim()
  if (!value) return null

  let host: string
  let path: string
  if (/^(https?|ssh):\/\//i.test(value)) {
    try {
      const url = new URL(value)
      host = url.hostname
      path = decodeURIComponent(url.pathname)
    } catch {
      return null
    }
  } else {
    const at = value.indexOf("@")
    const colon = value.indexOf(":")
    if (at < 0 || colon < at) return null
    host = value.slice(at + 1, colon)
    path = value.slice(colon + 1)
  }

  const segments = path.split("/").filter(Boolean)
  if (!host || segments.length < 2 || segments.some((s) => s === "." || s === "..")) return null
  host = host.toLowerCase()

  return isAzureHost(host) ? parseAzure(host, segments) : parseGeneric(host, segments)
}

function parseGeneric(host: string, segments: string[]): ParsedRemote | null {
  const repository = stripGit(segments[segments.length - 1])
  if (!repository) return null
  return { host, owner: segments.slice(0, -1).join("/"), repository }
}

function parseAzure(host: string, segments: string[]): ParsedRemote | null {
  let organization: string
  let project: string
  let repository: string

  const gitIndex = segments.indexOf("_git")
  if (gitIndex >= 0) {
    if (gitIndex + 1 >= segments.length) return null
    repository = segments[gitIndex + 1]
    const before = segments.slice(0, gitIndex)
    if (host.endsWith(".visualstudio.com")) {
      organization = host.slice(0, -".visualstudio.com".length)
      if (before.length > 0 && before[0].toLowerCase() === "defaultcollection") before.shift()
    } else {
      if (before.length === 0) return null
      organization = before.shift() as string
    }
    project = before.length > 0 ? before[0] : repository
  } else if (segments.length >= 4 && segments[0].toLowerCase() === "v3") {
    organization = segments[1]
    project = segments[2]
    repository = segments[3]
  } else {
    return null
  }

  repository = stripGit(repository)
  if (!organization || !project || !repository) return null
  return { host: AZURE_HOST, owner: `${organization}/${project}`, repository }
}
