import type { Resources } from "./en"
import overview from "./locales/tr/overview"
import review from "./locales/tr/review"
import revision from "./locales/tr/revision"
import impact from "./locales/tr/impact"
import brain from "./locales/tr/brain"
import arch from "./locales/tr/arch"
import picker from "./locales/tr/picker"
import repo from "./locales/tr/repo"
import compare from "./locales/tr/compare"
import insights from "./locales/tr/insights"
import execWs from "./locales/tr/execWs"
import executions from "./locales/tr/executions"
import tasks from "./locales/tr/tasks"
import shared from "./locales/tr/shared"
import models from "./locales/tr/models"
import modelCompare from "./locales/tr/modelCompare"
import automation from "./locales/tr/automation"

const tr: Resources = {
  common: {
    theme: "tema",
    light: "Açık",
    dark: "Koyu",
    toggleTheme: "Temayı değiştir",
    language: "dil",
    switchLanguage: "Dili değiştir",
    searchPlaceholder: "Ara veya komut çalıştır…",
    indexed: "indekslendi",
    engineeringWorkspace: "mühendislik çalışma alanı",
    never: "hiç",
    justNow: "az önce",
    minutesAgo: "{{n}} dk önce",
    hoursAgo: "{{n}} sa önce",
    daysAgo: "{{n}} gün önce",
    retry: "Tekrar dene",
    cancel: "İptal",
    loading: "Yükleniyor…",
    files_one: "{{count}} dosya",
    files_other: "{{count}} dosya",
  },
  nav: {
    overview: "Genel Bakış",
    planRun: "Planla ve çalıştır",
    understand: "Anla",
    measure: "Ölç",
    settings: "Ayarlar",
    aiModels: "Yapay zeka modelleri",
    automation: "Otomasyon",
    modelComparison: "Model karşılaştırma",
    tasks: "Görevler",
    executions: "Çalıştırmalar",
    repository: "Depo",
    impactMap: "Etki haritası",
    projectBrain: "Project Brain",
    insights: "İçgörüler",
    compareExecutions: "Çalıştırmaları karşılaştır",
    taskImpact: "Görev ve Etki Analizi",
    execution: "Çalıştırma",
    reviewDelivery: "İnceleme ve teslimat",
  },
  command: {
    goOverview: "Genel Bakış'a git",
    openRepository: "Depoyu aç",
    repositoryHint: "Yapı ve analizör durumu",
    viewTasks: "Görevleri gör",
    tasksHint: "Değişiklikleri planla ve onayla",
    openBrain: "Project Brain'i aç",
    brainHint: "Kod tabanına sor",
    viewExecutions: "Çalıştırmaları gör",
    executionsHint: "Çalıştırmalar ve inceleme",
    impactHint: "Değişikliklerin katmanlara etkisi",
    insightsHint: "Başarı, onarım, süre ve maliyet",
    compareHint: "Orijinal ve yeniden deneme",
    modelsHint: "Kendi modellerinizi ekleyin ve adımlara atayın",
    automationHint: "DevPilot task'ları pull request'e kadar götürsün",
    switchToTurkish: "Dili Türkçe yap",
    switchToEnglish: "Dili İngilizce yap",
    languageHint: "Arayüz dili",
    groupNavigate: "Git",
    groupSettings: "Ayarlar",
    noRepository: "Depo seçilmedi",
    openTask: "Görevi aç: {{title}}",
    openExecution: "Çalıştırmayı aç: {{title}}",
    placeholder: "Sayfa, görev, çalıştırma ara…",
    noMatches: "\"{{query}}\" için sonuç yok",
    navigate: "gezin",
    open: "aç",
  },
  overview,
  review,
  revision,
  impact,
  brain,
  arch,
  picker,
  repo,
  compare,
  insights,
  execWs,
  executions,
  shared,
  tasks,
  models,
  modelCompare,
  automation,
}

export default tr
