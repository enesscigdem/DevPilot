import { useEffect, useMemo, useState } from "react"
import { useTranslation } from "react-i18next"
import { AlertCircle, ArrowLeft, Loader2 } from "lucide-react"
import { createGitConnection, getGitConnections } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import { inferProvider, parseRemoteUrl } from "@/lib/remoteUrl"
import type { GitConnection, GitConnectionProvider } from "@/types"

interface RemoteRepositoryFormProps {
  onBack: () => void
  onConnected: () => void
}

const NEW_TOKEN = "new"

const TOKEN_HINTS: Record<GitConnectionProvider, string> = {
  GitLab: "picker.tokenHintGitLab",
  AzureDevOps: "picker.tokenHintAzure",
  Bitbucket: "picker.tokenHintBitbucket",
  Generic: "picker.tokenHintGeneric",
}

const fieldClass =
  "h-9 w-full rounded-[var(--radius-md)] border border-border bg-surface px-3 text-sm text-foreground outline-none placeholder:text-subtle-foreground focus:border-border-strong"

export function RemoteRepositoryForm({ onBack, onConnected }: RemoteRepositoryFormProps) {
  const { t } = useTranslation()
  const { connectWorkspace, refreshWorkspaces, selectWorkspace } = useWorkspace()

  const [url, setUrl] = useState("")
  const [branch, setBranch] = useState("main")
  const [providerOverride, setProviderOverride] = useState<GitConnectionProvider | null>(null)
  const [connections, setConnections] = useState<GitConnection[]>([])
  const [selectedConnection, setSelectedConnection] = useState<string>(NEW_TOKEN)
  const [token, setToken] = useState("")
  const [username, setUsername] = useState("")
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getGitConnections().then(setConnections).catch(() => setConnections([]))
  }, [])

  const parsed = useMemo(() => parseRemoteUrl(url), [url])
  const isGitHub = parsed?.host === "github.com"
  const inferred = parsed ? inferProvider(parsed.host) : "Generic"
  const inferredProvider: GitConnectionProvider = inferred === "GitHub" ? "Generic" : inferred
  const provider = providerOverride ?? inferredProvider

  const hostConnections = useMemo(
    () => (parsed ? connections.filter((c) => c.host === parsed.host) : []),
    [connections, parsed],
  )

  // Prefer a saved token for this host when one exists.
  useEffect(() => {
    setSelectedConnection(hostConnections[0]?.id ?? NEW_TOKEN)
  }, [hostConnections])

  const urlError = url.trim() && !parsed ? t("picker.urlInvalid") : isGitHub ? t("picker.urlGitHub") : null
  const usingNewToken = selectedConnection === NEW_TOKEN
  const canSubmit =
    !!parsed && !isGitHub && !!branch.trim() && (usingNewToken ? !!token.trim() : true) && !busy

  const handleSubmit = async () => {
    if (!parsed || !canSubmit) return
    setBusy(true)
    setError(null)
    try {
      let connectionId = usingNewToken ? undefined : selectedConnection
      if (usingNewToken) {
        const created = await createGitConnection({
          provider,
          host: parsed.host,
          token: token.trim(),
          username: username.trim() || undefined,
        })
        connectionId = created.id
      }

      const workspace = await connectWorkspace({
        remoteUrl: url.trim(),
        branch: branch.trim(),
        gitConnectionId: connectionId,
      })
      await refreshWorkspaces()
      selectWorkspace(workspace.id)
      onConnected()
    } catch (err) {
      setError(err instanceof Error && err.message ? err.message : t("picker.errUrlConnect"))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="p-5">
      <button
        type="button"
        onClick={onBack}
        className="inline-flex items-center gap-1.5 text-xs font-medium text-subtle-foreground transition-colors hover:text-foreground"
      >
        <ArrowLeft className="h-3.5 w-3.5" />
        {t("picker.back")}
      </button>

      <div className="mt-3">
        <h2 className="text-base font-semibold text-foreground">{t("picker.urlTitle")}</h2>
        <p className="mt-0.5 text-xs text-subtle-foreground">{t("picker.urlDesc")}</p>
      </div>

      {error && (
        <div className="mt-3 flex items-center gap-2 rounded-[var(--radius-md)] border border-red-500/20 bg-red-500/10 p-3 text-xs text-red-500">
          <AlertCircle className="h-4 w-4 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      <div className="mt-4 space-y-3">
        <div>
          <label className="tech-label mb-1.5 block">{t("picker.urlLabel")}</label>
          <input
            autoFocus
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder={t("picker.urlPlaceholder")}
            disabled={busy}
            className={fieldClass}
          />
          {urlError && <p className="mt-1 text-xs text-amber-500">{urlError}</p>}
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="tech-label mb-1.5 block">{t("picker.branchInput")}</label>
            <input
              value={branch}
              onChange={(e) => setBranch(e.target.value)}
              disabled={busy}
              className={fieldClass}
            />
          </div>
          <div>
            <label className="tech-label mb-1.5 block">{t("picker.hostType")}</label>
            <select
              value={provider}
              onChange={(e) => setProviderOverride(e.target.value as GitConnectionProvider)}
              disabled={busy || !usingNewToken}
              className={fieldClass}
            >
              <option value="GitLab">{t("picker.hostGitLab")}</option>
              <option value="AzureDevOps">{t("picker.hostAzure")}</option>
              <option value="Bitbucket">{t("picker.hostBitbucket")}</option>
              <option value="Generic">{t("picker.hostGeneric")}</option>
            </select>
          </div>
        </div>

        {provider === "Generic" && (
          <p className="text-xs text-subtle-foreground">{t("picker.hostGenericNote")}</p>
        )}

        {hostConnections.length > 0 && (
          <div>
            <label className="tech-label mb-1.5 block">{t("picker.savedToken")}</label>
            <select
              value={selectedConnection}
              onChange={(e) => setSelectedConnection(e.target.value)}
              disabled={busy}
              className={fieldClass}
            >
              {hostConnections.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.displayName}
                  {c.username ? ` (${c.username})` : ""}
                </option>
              ))}
              <option value={NEW_TOKEN}>{t("picker.newToken")}</option>
            </select>
          </div>
        )}

        {usingNewToken && (
          <>
            <div>
              <label className="tech-label mb-1.5 block">{t("picker.tokenLabel")}</label>
              <input
                type="password"
                autoComplete="off"
                value={token}
                onChange={(e) => setToken(e.target.value)}
                placeholder={t("picker.tokenPlaceholder")}
                disabled={busy}
                className={fieldClass}
              />
              <p className="mt-1 text-xs text-subtle-foreground">
                {t(TOKEN_HINTS[provider])}{" "}
                {t("picker.tokenSaved")}
              </p>
            </div>
            <div>
              <label className="tech-label mb-1.5 block">{t("picker.usernameLabel")}</label>
              <input
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                disabled={busy}
                autoComplete="off"
                className={fieldClass}
              />
              {provider === "Bitbucket" && (
                <p className="mt-1 text-xs text-subtle-foreground">{t("picker.usernameHintBitbucket")}</p>
              )}
            </div>
          </>
        )}
      </div>

      <div className="mt-6 flex items-center justify-end gap-2.5 border-t border-border pt-4">
        <button
          type="button"
          onClick={onBack}
          disabled={busy}
          className="rounded-[var(--radius-md)] border border-border px-3.5 py-1.5 text-xs font-medium text-foreground hover:bg-surface-secondary"
        >
          {t("picker.cancel")}
        </button>
        <button
          type="button"
          onClick={handleSubmit}
          disabled={!canSubmit}
          className="inline-flex items-center gap-1.5 rounded-[var(--radius-md)] bg-foreground px-4 py-1.5 text-xs font-medium text-background transition-opacity hover:opacity-90 disabled:opacity-50"
        >
          {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
          {busy ? t("picker.connecting") : t("picker.connectIndex")}
        </button>
      </div>
    </div>
  )
}
