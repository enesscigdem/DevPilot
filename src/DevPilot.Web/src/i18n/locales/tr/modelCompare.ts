import type { Resources } from "../../en"

const modelCompare: Resources["modelCompare"] = {
  eyebrow: "Model karşılaştırma",
  title: "Aynı görev, farklı modeller",
  description:
    "Onaylı plan her model için sırayla bir kez çalışır. Bitince sonucu, süreyi, token'ı ve maliyeti yan yana karşılaştırabilirsiniz.",
  back: "Göreve dön",
  loading: "Karşılaştırma yükleniyor…",
  errLoad: "Karşılaştırma yüklenemedi.",
  errCancel: "Karşılaştırma durdurulamadı.",
  sequentialNote: "Modeller sırayla çalışır, bu yüzden karşılaştırmanın tamamı yaklaşık tüm çalıştırmaların toplamı kadar sürer.",
  winnerHint: "Beğendiğiniz çalıştırmayı açıp her zamanki gibi inceleyin ve teslim edin. Diğerleri referans için açık kalır.",
  bestNote: "Yeşil, biten çalıştırmalar arasındaki en iyi değeri gösterir.",
  waitingForFirst: "Sonuçlar her çalıştırma bittikçe burada belirir.",
  stopRemaining: "Kalan çalıştırmaları atla",
  stopping: "Durduruluyor…",
  status: {
    Running: "Devam ediyor",
    Completed: "Tamamlandı",
    Cancelled: "Durduruldu",
  },
  runState: {
    Queued: "Sırada",
    Running: "Çalışıyor",
    Finished: "Bitti",
    Skipped: "Atlandı",
  },
  queuedHint: "Önceki çalıştırma bitince başlar",
  skippedHint: "Çalıştırılmadı",
  openExecution: "Çalıştırmayı aç",
  openReview: "İncele",
  runLabel: "{{n}}. çalıştırma",
  failedWith: "Çalıştırma başarısız: {{message}}",
  panel: {
    title: "Modelleri karşılaştır",
    description: "Bu planı 2 ya da 3 modelle sırayla çalıştırın ve sonuçları yan yana karşılaştırın.",
    needModels: "Karşılaştırmak için en az iki etkin model ekleyin.",
    manageModels: "Yapay zeka modellerini aç",
    pick: "2 ya da 3 model seçin",
    cost: "Her model görevin tamamını çalıştırır, bu yüzden süre ve maliyet katlanır.",
    start: "Karşılaştırmayı başlat",
    starting: "Başlatılıyor…",
    errStart: "Karşılaştırma başlatılamadı.",
    previous: "Önceki karşılaştırmalar",
    open: "Aç",
    modelsCount: "{{count}} model",
  },
}

export default modelCompare
