import i18n from "i18next"
import { initReactI18next } from "react-i18next"
import en from "./en"
import tr from "./tr"
import trServer from "./locales/tr/server"
import trServerPatterns from "./locales/tr/serverPatterns"

export type Lang = "en" | "tr"
export const LANGUAGES: { code: Lang; short: string; name: string }[] = [
  { code: "en", short: "EN", name: "English" },
  { code: "tr", short: "TR", name: "Türkçe" },
]

const STORAGE_KEY = "devpilot-lang"

function detectLang(): Lang {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    if (stored === "en" || stored === "tr") return stored
  } catch {
    /* storage unavailable */
  }
  return navigator.language?.toLowerCase().startsWith("tr") ? "tr" : "en"
}

export function setLang(lang: Lang) {
  void i18n.changeLanguage(lang)
}

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en, server: {} }, tr: { translation: tr, server: trServer } },
  ns: ["translation", "server"],
  lng: detectLang(),
  fallbackLng: "en",
  interpolation: { escapeValue: false },
})

i18n.on("languageChanged", (lng) => {
  document.documentElement.lang = lng
  try {
    window.localStorage.setItem(STORAGE_KEY, lng)
  } catch {
    /* ignore */
  }
})
document.documentElement.lang = i18n.language

export function currentLocale() {
  return i18n.language === "tr" ? "tr-TR" : "en-US"
}

/** Locale-aware formatters; use these instead of bare toLocale*String(). */
export const fmt = {
  dateTime: (d: Date | string | number) => new Date(d).toLocaleString(currentLocale()),
  date: (d: Date | string | number) => new Date(d).toLocaleDateString(currentLocale()),
  time: (d: Date | string | number, opts?: Intl.DateTimeFormatOptions) =>
    new Date(d).toLocaleTimeString(currentLocale(), opts),
  number: (n: number) => n.toLocaleString(currentLocale()),
}

/** "5m ago" style relative time, localized. */
export function relativeTime(dateStr?: string | null): string {
  if (!dateStr) return i18n.t("common.never")
  const d = new Date(dateStr)
  if (isNaN(d.getTime())) return dateStr
  const diffSec = Math.floor((Date.now() - d.getTime()) / 1000)
  if (diffSec < 60) return i18n.t("common.justNow")
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return i18n.t("common.minutesAgo", { n: diffMin })
  const diffHours = Math.floor(diffMin / 60)
  if (diffHours < 24) return i18n.t("common.hoursAgo", { n: diffHours })
  const diffDays = Math.floor(diffHours / 24)
  if (diffDays < 30) return i18n.t("common.daysAgo", { n: diffDays })
  return fmt.date(d)
}

/**
 * Translate a fixed English string produced by the API (verdicts, stage messages, ...).
 * Unknown strings (user data, free-form errors) pass through unchanged.
 */
export function srv(text: string | null | undefined): string {
  if (!text) return text ?? ""
  if (i18n.language !== "tr") return text
  const opts = { ns: "server", keySeparator: false, nsSeparator: false } as const
  if (i18n.exists(text, opts)) return i18n.t(text, opts)
  for (const [re, tpl] of trServerPatterns) {
    if (re.test(text)) return text.replace(re, tpl)
  }
  return text
}

export default i18n
