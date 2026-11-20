# OVRIX 1.0.0 doğrulaması

20 Kasım 2026. Yerel ortam Windows 10 x64, .NET SDK 10.0.401, NVIDIA GeForce RTX 4060 Ti ve AMD Radeon Graphics bulunan makine. Windows 11, fiziksel HDMI kartı, konsol, kumanda, PSN oturumu ve HDR ekran testleri ayrı doğrulama gerektirir.

## Kabul tablosu

| Kriter | Durum | Kanıt |
| --- | --- | --- |
| Windows masaüstü uygulaması açılır | PASS | Yerel WPF penceresi, gezinme, görsel kompozisyon ve paket açılış testi |
| HDMI capture kaynağı seçilir | BLOCKED | DirectShow aygıt ve biçim seçimi bağlı. Tarama OBS sanal kamera buldu. Fiziksel kartla akış denenemedi. |
| PS5 Remote Play uygulamadan yönetilir | BLOCKED | Ayrı derlenen gerçek Nixani Engine 1.1.0 ile protokol v2 el sıkışması ve LAN taraması geçti. Tarama 0 PS5 buldu. Eşleştirme, canlı oturum ve PSN doğrulaması için gerçek cihaz ve hesap gerekir. |
| Video gerçek zamanlı oynatılır | PASS | FFmpeg video dosyası, 60 kare ve son PTS 1,967 s. Sentetik Annex B hattı 60 kare teslim etti. |
| Kare interpolasyonu çalışır | PASS | Direct3D 11 sentetik ara kare hatası, referans kareye göre 39,9'dan 8,6'ya düştü. Oynatma matrisi aşağıda. |
| Kaynak FPS ve çıktı FPS ayrı ölçülür | PASS | Giriş, farklı içerik, WPF'ye iletilen çıktı ve sentez sayaçları ayrı. Tekrar ve sahne kesimi sentetik testte doğrulandı. |
| GPU ölçekleme çalışır | PASS | 1920×1080 → 2560×1440 ve yazılımda en boy oranı testi |
| Altyazı OCR çalışır | PASS | Türkçe Windows OCR tanıyıcısı Latin metni ve sözcük sınırlarını doğru okudu. İngilizce dil paketi bu makinede kurulu değildi. |
| İngilizce metin Türkçe çevrilir | PASS | Gerçek model, “Hello, we should go now.” girdisine “Merhaba, şimdi gitmeliyiz.” döndürdü. |
| Çevrimdışı çeviri çalışır | PASS | SHA-256 doğrulamalı Argos 1.5, Python, SentencePiece ve CTranslate2 ile çalıştı. |
| Desteklenen AI sağlayıcısı çalışır | PASS | Yerel OpenAI uyumlu HTTP sözleşmesi, önbellek, düzeltme ve istek iptali sınandı. Gerçek LLM ve bulut hesabı doğrulaması BLOCKED. |
| Türkçe altyazı görüntü üzerine işlenir | PASS | OCR → gerçek model sonucu ve çift dilli WPF kompozisyon testi |
| OCR video oynatmayı bloke etmez | PASS | Altyazı ve çeviri etkinken 5 saniyelik 30 FPS dosyadan 150 video karesi teslim edildi. |
| Ses düzgün oynatılır | BLOCKED | Dosya testinde 385.024 bayt PCM ve oynatma testlerinde WASAPI tampon verisi ölçüldü. Fiziksel çıkış ve görüntü/ses senkronizasyonu doğrulanmadı. |
| Remote Play kumanda girdisi | BLOCKED | Oyun kumandası protokol alanları sınandı. Fiziksel DualSense ve etkin PS5 oturumu gerekir. |
| Profiller ve ayarlar kalıcı | PASS | JSON profil ve DPAPI kaydetme, okuma ve kaldırma testleri |
| Arayüz kontrolleri işlevlere bağlı | PASS | WPF gezinme ve altyazı bileşimi sınandı. Cihaza bağlı denetimler için ilgili BLOCKED satırları geçerlidir. |
| Hata mesajları anlaşılır | PASS | Eksik dosya, geçersiz IPC, model ve HTTP durumları için hata yolları |
| Otomatik testler çalışır | PASS | Yerel test paketi ve GPU matrisi geçti. GitHub çalıştırması depo oluşturulduktan sonra doğrulanır. |
| Dağıtım paketleri üretilebilir | PASS | Windows x64 kurulum EXE'si ve kendinden yeterli ZIP, SHA-256 manifesti |
| Lisans ve bağımlılık bildirimleri | PASS | Sabit kaynak revizyonları, özgün MIT lisansı ve paket içi üçüncü taraf metinleri |
| README ve kurulum talimatları | PASS | README ve BUILD.md |
| HDR passthrough, VRR ve AI upscaling | FAIL | PQ/BT.2020 dosya → SDR test edildi. Yerel HDR/VRR sunumu ve AI video ölçekleme uygulanmadı. |
| Tam PSN oturum açma ve belirteç yenileme | FAIL | Uygulama hazır yetkili belirteci DPAPI ile saklar. Tarayıcı oturum açma ve yenileme akışı eksik. |
| 1080p60 → sürekli 1080p120 | FAIL | 4 saniyelik kaynakta 235 özgün ve 195 ara kare. 120 FPS fiziksel sunum ve uzun süreli kararlılık doğrulanmadı. |

## Ölçüm

4 saniyelik sentetik klipler ve WPF öncesi teslimler. Gerçek ekran yenilemesi veya kontrolcüden ekrana gecikme ölçümü yerine geçmez. Sentez süresi D3D11 işlem ve geri okumayı içerir.

| Giriş | İnterpolasyon | Ölçekleme | Özgün | Ara | Kayıp | Son sentez |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 1080p30 | Kapalı | Kapalı | 120 | 0 | 0 | — |
| 1080p30 | Düşük gecikme | Kapalı | 118 | 106 | 2 | 9,50 ms |
| 1080p30 | Kalite | Kapalı | 118 | 104 | 2 | 10,39 ms |
| 1080p60 | Kapalı | Kapalı | 240 | 0 | 0 | — |
| 1080p60 | Dengeli | Kapalı | 235 | 195 | 5 | 7,30 ms |
| 1080p30 | Düşük gecikme | 1440p | 120 | 104 | 0 | 9,82 ms |

Kısa testlerde tampon ve ara kare atlama kararı iş yüküyle değişebilir. Test komutu:

```powershell
./scripts/test.ps1 -Gpu -Ocr -Media -Performance -Python C:\path\python.exe -Model C:\path\translate-en_tr-1_5.argosmodel
./scripts/verify-packages.ps1 -Install
```

## HDMI gerektirmeyen altyazı ve HUD testi

29 saniyelik, 1280×720 ve 30 FPS sentetik video dosyası normal WPF oynatma yolunda açıldı. Test, kaynak satırın ekranda ilk görüldüğü kareyi renk işaretinden ve Türkçe altyazının ilk görüldüğü anı WPF bileşiminden ölçer. İlk çalıştırmada çeviri belleği boştur. Windows OCR tanıyıcısı bu makinede `tr`, çeviri yönü İngilizce → Türkçedir.

| İngilizce kaynak satır | HUD çıktısı | İlk koşudaki gecikme | Değerlendirme |
| --- | --- | ---: | --- |
| Hello, we should go now. | Hellq şimdi gitmemiz gerekiyor. | 1.947 ms | Soğuk model açılışı dahil. OCR selamlamayı yanlış okudu. |
| Do not move until the guard leaves. | Koruma yapraklarına kadar hareket etmeyin. | 340 ms | OCR doğru. Model “leaves” sözcüğünün anlamını karıştırdı. |
| Meet me at the station. | İstasyonda benimle tanışın. | 420 ms | Buluşma isteği tanışma olarak çevrildi. |
| You have three minutes. | Üç dakikanız var. | 319 ms | Anlam korundu. |
| I am running out of time. | Ben zaman tükeniyorum. | 451 ms | Model dilbilgisi hatası yaptı. |
| That was a close call. | Bu, yakın bir çağrıydı. | 397 ms | Deyim sözcüğü sözcüğüne çevrildi. |
| Hello, we should go now. | Hellq şimdi gitmemiz gerekiyor. | 234 ms | Önbellekten gelen tekrar, OCR hatasını da taşıdı. |
| Go! | — | — | 180 ms boyunca kaynakta görüldü. Türkçe altyazı üretilmedi. |

İki bağımsız ve boş önbellekli oturumda ilk altyazı **1.799–1.947 ms**, sıcak modelle yeni satırların medyanları **259 ms** ve **397 ms** oldu. Oturum içi önbellek tekrarı **234–251 ms** sürdü. İki test aynı metin sonuçlarını verdi ve 180 ms ekranda kalan satır iki testte de kaçtı. Oynatma, tanıma ve HUD akışı çalıştı. Bu örnekte çevrimdışı modelin oyun diyaloğundaki anlam doğruluğu yetersiz kaldı. Yerel LLM ve bulut sağlayıcıları bu ölçüme dahil değildir.

```powershell
dotnet run --project tests/Ovrix.Tests.csproj -c Release -- --hud artifacts/translation-qa C:\path\python.exe C:\path\translate-en_tr-1_5.argosmodel
```

Komut açık oynatıcıdan alınmış iki PNG, kaynak MP4 ve ölçümleri içeren JSON üretir.
