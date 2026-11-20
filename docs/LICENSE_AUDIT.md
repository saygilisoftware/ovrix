# Lisans ve referans denetimi

20 Kasım 2026 tarihinde varsayılan dal başları ve kaynak dosyaları incelendi.

| Referans | Lisans ve son etkinlik | Kaynak ve derleme | Karar |
| --- | --- | --- | --- |
| [Glasir](https://github.com/Abhito/Glasir/tree/0f5347c4577f76b217931bd9771926de30d176ae) | MIT ve dosya bazında MPL-2.0, 20 Kasım | flow.hlsl çok geçişli çift yönlü blok araması ve sahne kesimi uyguluyor. repeat_filter.h örneklenmiş parlaklıkla tekrarları süzüyor. CMake, VS, build.cmd. Oynatma mpv/ReShade. | Tasarım referansı. D3D11 shader'ı özgün. MPL dosyası dağıtılırsa lisansı ve ilgili kaynak korunur. |
| [chiaki-ng](https://github.com/chiaki-ng/chiaki-ng/tree/43728af2e639fecb8a7af1cd68c29820a40de973) | AGPL-3.0-only, OpenSSL istisnası, 14 Kasım | lib/src/discovery.c konsol keşfini ayrı modülde uyguluyor. CMake ve Windows yapımında MSYS2/CI. | Nixani alt modülü olarak ayrı edinilir. |
| [Nixani Engine](https://github.com/Nixani/nixani-engine/tree/39b84ddea5402e38b1a1f9c0a3da2ef5df7cf03e) | Motor AGPL-3.0, IPC tarifi ve protokol başlığı MIT, 18 Kasım | C++20, CMake, clang-cl, vcpkg. src/main.cpp motoru, protokol v2 cihaz ve medya komutları. | Belgelenen IPC üzerinden bağımsız süreç. Motor ikilisi pakete eklenmez. Ayrı dağıtılırsa ilgili kaynak sağlanmalıdır. |
| [SubLens](https://github.com/bnsware/SubLens/tree/5f3f3506a5e19630fac8d0c81ab9fec8e3617eee) | MIT, 14 Kasım | PySide6, requirements.txt, paketleme betiği. sublens/ocr.py Windows OCR ve parlak metin ön işlemesini ayırıyor. | OCR tasarım referansı. |
| [DeskTranslate](https://github.com/DeskTranslate/DeskTranslate/tree/533ad42d1064cb7a85a7d1051ca380f1e4613e8b) | MIT, 1 Kasım | pyproject.toml bağımlılıkları sabitliyor. algorithms.py metin düzeni ve normalleştirmeyi ayırıyor. ONNX OCR nitelik testleri içeriyor. | OCR ve bağlam referansı. ONNX ağırlıkları paketlenmez. |
| [Magpie](https://github.com/Blinue/Magpie/tree/4a0c64a18d485fba50ffe40eef22c3d669654391) | GPL-3.0, 19 Kasım | Visual Studio ve Conan. D3D11 yakalama ve kompozisyon sunum modülleri ayrılmış. | Ölçekleme ve ekran sunumu referansı. |

Direct3D 11 shader model 5 Windows'ta ortak GPU hesaplama yoludur. Vulkan/ncnn RIFE CLI dosyalarla çalışır. Canlı işleme için yeni GPU bağlamı, ağırlık ve gecikme denetimi gerekir. DirectML/ONNX seçimi de model ve operatör desteğine bağlıdır. Windows OCR paketlenmiş bir tanıma modeli gerektirmez.

Özgün OVRIX kodu için MIT seçildi. FFmpeg, Argos ve Nixani ayrı edinilir. FFmpeg yapı seçenekleri lisansı değiştirir. Argos kitaplığının lisansı model lisansını belirlemez.
