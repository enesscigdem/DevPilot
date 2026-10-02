import type { Resources } from "../../en"

const arch: Resources["arch"] = {
  eyebrow: "Etki haritası",
  title: "Değişiklik etki haritası",
  description:
    "Çözümün canlı bağımlılık grafiği; Roslyn sembol grafiğinden türetilir. Vurgulanan düğümler aktif görevden etkilenir — bir değişikliğin katmanlara nasıl yayıldığını izleyin.",
  showingImpacted: "Etkilenenler gösteriliyor",
  highlightImpacted: "Etkilenenleri vurgula",
  errLoad: "Mimari grafiği yüklenemedi.",
  analyzing: "Çözüm mimari grafiği analiz ediliyor...",
  failed: "Mimari yüklenemedi",
  noWorkspace: "Depo çalışma alanı seçilmedi",
  noWorkspaceDesc: "Mimarisini incelemek için kenar çubuğundan bir çalışma alanı seçin veya oluşturun.",
  noProjects: "Proje bulunamadı",
  noProjectsDesc: "Seçili çalışma alanında analiz edilebilir .NET projesi veya önyüz modülü yok.",
  legendImpacted: "Aktif görevden etkilenen",
  legendPath: "Seçili bağımlılık yolu",
  inspector: "Düğüm denetçisi",
  impacted: "etkilenen",
  whyImpacted: "Neden etkileniyor",
  dependencies: "Bağımlılıklar",
  dependedOnBy: "Şunlar buna bağımlı",
  dependsOn: "Şunlara bağımlı",
  noDeps: "Proje bağımlılığı yok",
  keyFiles: "Ana dosyalar",
  noFiles: "Kaynak dosya bulunamadı",
  selectNode: "Bağımlılıklarını ve ana dosyalarını incelemek için bir düğüm seçin.",
}
export default arch
