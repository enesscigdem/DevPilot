// English strings emitted by the API -> Turkish. Keys are the exact English text.
// Sentences with parameters live in serverPatterns.ts.
const server: Record<string, string> = {
  // overview / attention
  "Build failed": "Derleme başarısız",
  "Test failed": "Test başarısız",
  "Agent execution failed": "Ajan çalıştırması başarısız",
  "PR creation failed": "PR oluşturma başarısız",
  "CI checks failed": "CI kontrolleri başarısız",
  "Execution failed": "Çalıştırma başarısız",
  "Review ready for approval": "İnceleme onaya hazır",
  "Plan ready for review": "Plan incelemeye hazır",
  "Review rejected": "İnceleme reddedildi",
  "Reviewer rejected changes": "İnceleyici değişiklikleri reddetti",
  "Remote CI checks failed": "Uzak CI kontrolleri başarısız",
  "approved review for": "şunun incelemesini onayladı:",
  "rejected review for": "şunun incelemesini reddetti:",
  "merged changes for": "şunun değişikliklerini birleştirdi:",

  // stage names (usage timings)
  Generation: "Üretim",
  Build: "Derleme",
  Test: "Test",
  Repair: "Onarım",

  // verdict findings
  "The base was fast-forwarded to match origin before this run.": "Taban, bu çalıştırmadan önce origin ile eşleşecek şekilde ileri sarıldı.",
  "Origin could not be reached, so base freshness is unknown.": "Origin'e ulaşılamadı; bu yüzden tabanın güncelliği bilinmiyor.",
  "A failing test passed on a confirmation rerun and was treated as flaky; no repair was needed.": "Başarısız bir test doğrulama yeniden çalıştırmasında geçti ve kararsız (flaky) sayıldı; onarım gerekmedi.",
  "The baseline comparison was inconclusive, so a failure was repaired from raw diagnostics only. It could not be proven new or pre-existing.": "Baseline karşılaştırması sonuçsuz kaldı; bu yüzden hata yalnızca ham tanılardan onarıldı. Hatanın yeni mi yoksa önceden var mı olduğu kanıtlanamadı.",
  "A test repair broke the build.": "Bir test onarımı derlemeyi bozdu.",

  // verdict headlines / actions
  "Verified: build and tests passed on the final change.": "Doğrulandı: nihai değişiklikte derleme ve testler geçti.",
  "Review the diff and approve.": "Diff'i inceleyip onaylayın.",
  "Review the diff and approve; the pre-existing failures are not caused by this change.": "Diff'i inceleyip onaylayın; önceden var olan hatalar bu değişiklikten kaynaklanmıyor.",
  "Partially verified: the baseline comparison was inconclusive.": "Kısmen doğrulandı: baseline karşılaştırması sonuçsuz kaldı.",
  "Run the repository checks on the base commit locally to confirm the failure was not pre-existing, then review the diff.": "Hatanın önceden var olmadığını doğrulamak için depo kontrollerini temel commit üzerinde yerelde çalıştırın, ardından diff'i inceleyin.",
  "Partially verified: the build passed but verification was incomplete (no tests discovered or unresolved checks).": "Kısmen doğrulandı: derleme geçti ancak doğrulama eksik (test bulunamadı veya çözülemeyen kontroller var).",
  "Review the diff carefully; add or run tests for the changed behavior.": "Diff'i dikkatle inceleyin; değişen davranış için test ekleyin veya çalıştırın.",
  "Not verified: no trustworthy build or test checks were discovered for this repository.": "Doğrulanmadı: bu depo için güvenilir bir derleme veya test kontrolü bulunamadı.",
  "Review the diff manually; rely on CI after pushing.": "Diff'i elle inceleyin; push sonrası CI'a güvenin.",
  "Not verified: verification could not run because of an infrastructure error.": "Doğrulanmadı: bir altyapı hatası yüzünden doğrulama çalıştırılamadı.",
  "Fix the environment (toolchain, Docker, network) and retry, or rely on CI; merge stays blocked locally.": "Ortamı (araç zinciri, Docker, ağ) düzeltip yeniden deneyin veya CI'a güvenin; birleştirme yerelde engelli kalır.",
  "Needs review: an automated test repair may have weakened existing tests.": "İnceleme gerekli: otomatik bir test onarımı mevcut testleri zayıflatmış olabilir.",
  "Inspect the test diff for removed assertions, skips or deleted tests before approving, or retry.": "Onaylamadan önce test diff'inde kaldırılan assertion, atlanan veya silinen testlere bakın ya da yeniden deneyin.",
  "Needs review: verification could not be completed automatically.": "İnceleme gerekli: doğrulama otomatik olarak tamamlanamadı.",
  "Needs review: verification ended with an unresolved failure.": "İnceleme gerekli: doğrulama çözülmemiş bir hatayla sona erdi.",
  "Inspect the failing evidence below; retry the execution or fix the failing files manually.": "Aşağıdaki hata kanıtını inceleyin; çalıştırmayı yeniden deneyin veya başarısız dosyaları elle düzeltin.",
  "Failed before verification.": "Doğrulamadan önce başarısız oldu.",
  "Retry the execution. If it keeps failing, re-run impact analysis or narrow the plan.": "Çalıştırmayı yeniden deneyin. Başarısız olmaya devam ederse etki analizini yeniden çalıştırın veya planı daraltın.",
  "The execution was cancelled.": "Çalıştırma iptal edildi.",
  "Start a new execution when ready.": "Hazır olduğunuzda yeni bir çalıştırma başlatın.",

  // insights signals
  "Pre-existing failures separated": "Önceden var olan hatalar ayrıştırıldı",
  "Runs where failures already on the base commit were proven not to be caused by the change.": "Temel commit'te zaten var olan hataların değişiklikten kaynaklanmadığının kanıtlandığı çalıştırmalar.",
  "Recovered by repair": "Onarımla kurtarıldı",
  "Runs that hit a build, test or edit-applicability problem and still reached a deliverable state.": "Derleme, test veya düzenleme uygulanabilirliği sorunuyla karşılaşıp yine de teslim edilebilir duruma ulaşan çalıştırmalar.",
  "Flaky failures absorbed": "Kararsız (flaky) hatalar sönümlendi",
  "A failing test passed on a confirmation rerun, so no repair was spent on it.": "Başarısız bir test doğrulama yeniden çalıştırmasında geçti; bu yüzden onun için onarım harcanmadı.",
  "Weakened tests caught": "Zayıflatılan testler yakalandı",
  "A test repair removed assertions, added skips or deleted tests and was sent to review.": "Bir test onarımı assertion'ları kaldırdı, atlamalar ekledi veya testleri sildi ve incelemeye gönderildi.",
  "Baseline unverified": "Baseline doğrulanamadı",
  "Runs repaired while the baseline comparison was inconclusive; never reported as fully verified.": "Baseline karşılaştırması sonuçsuzken onarılan çalıştırmalar; asla tam doğrulanmış olarak raporlanmaz.",
  "Stale base detected": "Eski taban tespit edildi",
  "Runs started while the local base was behind origin.": "Yerel taban origin'in gerisindeyken başlatılan çalıştırmalar.",
}
export default server
