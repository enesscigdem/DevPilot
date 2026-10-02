import type { Resources } from "../../en"

const tasks: Resources["tasks"] = {
  eyebrow: "Görevler",
  title: "Mühendislik görevleri",
  description:
    "Bir mühendislik değişikliğini sade bir dille anlatın. DevPilot Roslyn çalışma alanını analiz eder, bir plan önerir ve koda dokunmadan önce onayınızı bekler.",
  titlePlaceholder: "Görev başlığı (örn. Herkese açık ürünler uç noktasına hız sınırlaması ekle)",
  descriptionPlaceholder: "Açıklama / ayrıntılar (isteğe bağlı)…",
  context: "Bağlam",
  noActiveWorkspace: "Aktif çalışma alanı yok",
  activeWorkspace: "· aktif çalışma alanı",
  analyze: "Analiz et",
  filterPlaceholder: "Görevleri filtrele",
  loading: "Görevler API'den yükleniyor…",
  failedLoad: "Görevler yüklenemedi",
  noWorkspaceEmpty: "Aktif depo çalışma alanı yok. Görevleri görmek için bir çalışma alanı bağlayın veya seçin.",
  noMatch: "Seçili filtreyle eşleşen görev yok.",
  noTasks: "Görev bulunamadı.",
  errLoad: "Görevler sunucudan yüklenemedi.",
  errNoWorkspace: "Aktif depo çalışma alanı yok. Lütfen bir çalışma alanı bağlayın veya seçin.",
  errCreate: "Görev oluşturulamadı.",
  filters: {
    all: "Tümü",
    awaitingApproval: "Onay bekliyor",
    executing: "Çalışıyor",
    blocked: "Engellendi",
    done: "Bitti",
    failed: "Başarısız",
    draft: "Taslak",
  },
}
export default tasks
