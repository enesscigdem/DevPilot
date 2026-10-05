import type { Resources } from "../../en"

const automation: Resources["automation"] = {
  eyebrow: "Ayarlar",
  title: "Otomasyon",
  description:
    "Bu repoda DevPilot'un bir task'ı kendi başına nereye kadar götürebileceğini belirleyin. Her otomatik adım, bir kişinin tetikleyeceği adımlarla aynı kontrollerden geçer. Açıkça güvenli olmayan her şey sebebiyle birlikte size bırakılır.",
  loading: "Otomasyon ayarları yükleniyor…",
  errLoad: "Otomasyon ayarları yüklenemedi.",
  errSave: "Otomasyon ayarları kaydedilemedi.",
  noWorkspace: "Önce bir repo seçin. Otomasyon repo bazında ayarlanır.",
  repository: "Repo",
  save: "Değişiklikleri kaydet",
  saving: "Kaydediliyor…",
  saved: "Kaydedildi.",
  levelHeading: "DevPilot kendi başına ne kadarını yapsın?",
  levels: {
    Manual: {
      name: "Manuel",
      summary: "Her adımı siz tetiklersiniz.",
      does: "Otomatik hiçbir şey yapılmaz. Otomasyon olmadan DevPilot böyle çalışır.",
    },
    SemiAuto: {
      name: "Planla ve çalıştır",
      summary: "Planı onaylar ve işi başlatır.",
      does: "Yeni bir task'ın planını onaylar ve çalıştırmayı başlatır. Biten değişikliği yine siz incelersiniz.",
    },
    AutoPr: {
      name: "Pull request olarak teslim et",
      summary: "Güvenli değişiklikler için PR açar.",
      does: "Ayrıca tüm güvenlik kontrollerini geçen bir değişikliği onaylar, commit'ler, push'lar ve pull request açar. Merge'ü siz yaparsınız.",
    },
    FullAuto: {
      name: "Tam otomatik",
      summary: "CI yeşil olunca merge eder.",
      does: "Ayrıca CI geçtiğinde ve değişiklik onaydan sonra değişmediğinde pull request'i merge eder.",
    },
  },
  activeSince: "Otomasyon yalnızca {{date}} tarihinden sonra oluşturulan task'lara dokunur. Önceki task'lar olduğu gibi kalır.",
  paused: {
    title: "Otomasyonu duraklat",
    body: "Tüm otomatik işlemleri hemen durdurur. Ayarlarınız korunur.",
    notice: "Otomasyon duraklatıldı. Devam ettirene kadar kendi başına hiçbir şey yapılmaz.",
  },
  safetyHeading: "Güvenlik kontrolleri",
  safetyIntro:
    "Bir değişiklik yalnızca tamamen doğrulandığında otomatik teslim edilir: build ve testler geçer, hiçbir test zayıflatılmamıştır, arayüze dokunulmamıştır ve gizli dosya yoktur. Bir kontrolden geçemeyen değişiklik, sebebi execution üzerinde gösterilerek sizi bekler.",
  limits: {
    maxFiles: "En büyük değişiklik (dosya)",
    maxFilesHint: "Daha büyük değişiklikler size bırakılır.",
    maxLines: "En büyük değişiklik (satır)",
    maxLinesHint: "Eklenen ve silinen satırların toplamı.",
    maxParallel: "Aynı anda çalışan task",
    maxParallelHint: "Yüksek değer daha hızlıdır ama modelinizin hız limitini daha çok kullanır.",
  },
  protectedHeading: "Korumalı yollar",
  protectedHint:
    "Her satıra bir desen. Bir eşleşmeye dokunan değişiklik her zaman size bırakılır. ** her klasör derinliğini, * tek klasör içini eşler.",
  requireCi: {
    label: "Yalnızca CI kontrolleri geçtiyse merge et",
    hint: "Bu açıkken hiç CI kontrolü olmayan bir repo otomatik merge edilmez.",
  },
  errRange: "Limitler izin verilen aralığın dışında.",
}

export default automation
