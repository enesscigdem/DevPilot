// English API sentences with parameters -> Turkish. `$1`, `$2` refer to regex capture groups.
// Applied by srv() after the exact-match table in server.ts.
const patterns: [RegExp, string][] = [
  // --- goal board notes ---
  [/^Waits for “(.*)”: both change (.*)\.$/, "“$1” bitince başlayacak: ikisi de $2 dosyasını değiştiriyor."],
  [/^DevPilot could not start the analysis of this task\. Open it to retry\.$/, "DevPilot bu görevin analizini başlatamadı. Yeniden denemek için görevi açın."],
  // --- verdict findings ---
  [/^The base was (\d+) commit\(s\) behind origin when this ran; the change may need a rebase before merging\.$/, "Çalıştırma sırasında taban, origin'in $1 commit gerisindeydi; değişikliğin birleştirmeden önce rebase edilmesi gerekebilir."],
  [/^Generation needed (\d+) applicability repair\(s\) and (\d+) compact retr(?:y|ies)\.$/, "Üretim için $1 uygulanabilirlik onarımı ve $2 kompakt yeniden deneme gerekti."],
  [/^Compiler repair used (\d+) round\(s\)\.$/, "Derleyici onarımı $1 tur kullandı."],
  [/^Test repair used (\d+) round\(s\)\.$/, "Test onarımı $1 tur kullandı."],
  [/^(\d+) pre-existing failure\(s\) on the base commit remain unchanged\.$/, "Temel commit'teki $1 önceden var olan hata değişmeden kaldı."],
  [/^Discovered but not run: (.*)\.$/, "Keşfedildi ancak çalıştırılmadı: $1."],
  [/^The build failure persisted after the focused repair, so repair stopped with no diagnostic progress.$/, "Odaklı onarımdan sonra derleme hatası sürdü; tanılamada ilerleme olmadığı için onarım durduruldu."],
  [/^The tests failure persisted after the focused repair, so repair stopped with no diagnostic progress.$/, "Odaklı onarımdan sonra test hatası sürdü; tanılamada ilerleme olmadığı için onarım durduruldu."],
  [/^The build failure could not be tied to a file this task changed, so no safe repair target existed.$/, "Derleme hatası bu görevin değiştirdiği bir dosyaya bağlanamadı; güvenli bir onarım hedefi yoktu."],
  [/^The tests failure could not be tied to a file this task changed, so no safe repair target existed.$/, "Test hatası bu görevin değiştirdiği bir dosyaya bağlanamadı; güvenli bir onarım hedefi yoktu."],
  [/^The focused repair of the build failure produced no change to the working tree.$/, "Derleme hatasına yönelik odaklı onarım çalışma ağacında hiçbir değişiklik üretmedi."],
  [/^The focused repair of the tests failure produced no change to the working tree.$/, "Test hatasına yönelik odaklı onarım çalışma ağacında hiçbir değişiklik üretmedi."],
  // --- verdict headlines ---
  [/^No new regressions: (\d+) pre-existing failure\(s\) on the base commit remain, none were introduced\.$/, "Yeni regresyon yok: temel commit'teki $1 önceden var olan hata duruyor, yenisi eklenmedi."],
  [/^Needs review: The build passed, but (\d+) tests? still fails? after (\d+) automatic repair round\(s\)\.$/, "İnceleme gerekli: derleme geçti ancak $1 test, $2 otomatik onarım turundan sonra hâlâ başarısız."],
  [/^Needs review: (\d+) tests? still fails? after (\d+) automatic repair round\(s\)\.$/, "İnceleme gerekli: $1 test, $2 otomatik onarım turundan sonra hâlâ başarısız."],
  [/^Failed before verification: (.*)$/, "Doğrulamadan önce başarısız oldu: $1"],
]
export default patterns
