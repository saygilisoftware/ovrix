# Derleme ve kullanım

## Windows x64

Gerekenler .NET 10 SDK, Visual Studio 2022/2026 C++ masaüstü araçları, Windows SDK ve CMake 3.25 veya üstüdür. Kurulum EXE'si için Inno Setup 6 gerekir.

```powershell
./scripts/build-native.ps1
dotnet restore OVRIX.slnx --locked-mode
dotnet build OVRIX.slnx -c Release --no-restore
./scripts/test.ps1 -Media
./scripts/package.ps1
```

CMake için gerekirse `-Generator 'Visual Studio 17 2022'` ya da `-Generator 'Visual Studio 18 2026'` kullanın. CI varsayılan Windows görüntüsüne uygular. Çıktılar `artifacts/release` içindeki kullanıcı başına kurulum EXE'si, taşınabilir ZIP ve SHA-256 listesidir. Pakette .NET çalışma zamanı vardır. Paketler imzasızdır. İndirmeyi doğrulamak için `Get-FileHash -Algorithm SHA256 <paket>` çıktısını listeyle karşılaştırın.

## Video ve ses

Güvendiğiniz [Windows FFmpeg yapısını](https://ffmpeg.org/download.html#build-windows) sağlayın. Aynı dizinde `ffmpeg.exe` ve `ffprobe.exe` gerekir. HDMI için `dshow`, HDR dosya ton eşlemesi için `zscale` ve `tonemap` filtreleri gerekir. FFmpeg'i PATH'e ekleyin veya Ayarlar'dan açık ikili yolunu seçin. FFmpeg pakete gömülmez.

HDMI kartın sürücüsünü yükleyin. **Yakalama** bölümünde aygıtları tarayın, desteklenen çözünürlük ve piksel biçimini seçin ve kartın ses girişini atayın. Aygıt yeniden bağlanırken OVRIX sınırlı sayıda yeniden deneme yapar. Korunan kaynakların akışa izin vermesi gerekir. Ses çıkışı WASAPI paylaşım modunu kullanır. Negatif ses ayarı ilk olarak interpolasyon için eklenen tampondan düşülür.

## PS5 Remote Play

[Nixani Engine 1.1.0 kaynak revizyonunu](https://github.com/Nixani/nixani-engine/tree/39b84ddea5402e38b1a1f9c0a3da2ef5df7cf03e) upstream `setup.ps1`, `build.ps1` ve `package.ps1` adımlarıyla derleyin. Gerekli araçlar Visual Studio C++, clang-cl, CMake, Ninja, Git, Python ve vcpkg'dir. Kullandığınız vcpkg bir manifest baseline istiyorsa dağıtımı yapılan port revizyonunu `builtin-baseline` alanına ekleyin. Yerel doğrulamada vcpkg `74f5ea45ca78988784b810ad9bbb148ea7ff76d8` ve clang-cl 23.1.6 kullanıldı. OVRIX'te Nixani EXE yolunu seçin ve aynı dizinde motorun DLL'lerini tutun.

1. Kendi PS5'inizde Remote Play'i etkinleştirin.
2. Yerel konsolu bulun veya adresini yazın. Yetkili hesabın 8 baytlık Base64 PSN kimliğini ve PS5'in ekranda gösterdiği sekiz basamaklı kodu girin.
3. Çözünürlük ve FPS seçip başlatın. Bağlantı tanılaması **İzleme** bölümündedir.
4. İnternet üzerinden oturum için yetkili Remote Play erişim belirtecini girin ve hesaptaki konsolu listeden seçin. Bu sürüm girdisi hazır olan belirteci kullanır. Tarayıcı tabanlı PSN oturum açma ve yenileme akışı eksiktir.

## OCR ve çeviri

Windows Ayarları'ndaki dil seçeneklerinde kaynak dil için OCR özelliğini yükleyin. Windows OCR kullanılır. Otomatik tarama ekranın alt bölümündeki metni arar. Menü ve farklı konuşma kutusu konumlarında fareyle bölge seçin.

Çevrimdışı sağlayıcı için Windows x64 Python ortamında CTranslate2 ve SentencePiece'i kurun. Testte Python 3.10 ve aşağıdaki sürümler kullanıldı.

```powershell
python -m venv .venv-ovrix
.\.venv-ovrix\Scripts\python.exe -m pip install ctranslate2==4.9.0 sentencepiece==0.2.0
```

Ayarlar'daki Python alanına bu ortamın `python.exe` yolunu verin. **Çeviri** bölümünden İngilizce → Türkçe modeli indirin. Uygulama model arşivini sabit SHA-256 ile doğrular. Özel bir Argos arşivi için yayıncı hash'i ve doğru dil çifti gerekir. Model çalıştırılırken istekler yerel stdin/stdout üzerinden geçer.

Yerel LLM bağdaştırıcısı OpenAI uyumlu loopback API ve önceden kurulmuş model ister. Bulut modu HTTPS API adresi, model adı ve DPAPI ile saklanan anahtar ister.

## GitHub yayını

Windows iş akışı derlemeyi, temel testleri ve paketlemeyi çalıştırır. Yayın adayının paketlerini iş akışından alın ve [kabul tablosunu](TESTING.md) inceleyin. Kabul tamamlandıktan sonra `v1.0.0` etiketi için iş akışını `prepare_release` seçeneğiyle çalıştırarak GitHub sürüm taslağını hazırlayabilirsiniz. Uygulama yapılandırılan GitHub deposunun son yayımlanan sürümünü sorgulayabilir.
