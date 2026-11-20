# Güvenlik

OVRIX yalnız kullanıcı tarafından seçilen dosya ve görüntü kaynaklarını işler. PS5 erişimi kullanıcının yetkili hesabını ve konsolun gösterdiği eşleştirme kodunu gerektirir.

- API anahtarı, PlayStation oturum belirteci ve kayıt anahtarları Windows DPAPI ile mevcut kullanıcı profiline bağlanır.
- Harici çalıştırılabilir dosyaları güvendiğiniz yayıncıdan edinin. Uygulama FFmpeg ve Nixani motorunu ayrı süreç olarak çalıştırır.
- Çevrimdışı modeller SHA-256 ile doğrulanır. Yerel API loopback adresiyle sınırlıdır. Bulut API HTTPS ve kullanıcının belirlediği adresi kullanır.
- Bulut çevirisi algılanan altyazıyı, son satırları ve sözlüğü seçili sağlayıcıya gönderir. Çeviri belleği yerel SQLite dosyasındadır.
- Yerel tanılama günlükleri uygulama tarafından hiçbir servise gönderilmez. Rapor paylaşmadan önce cihaz ve ağ bilgilerine göz atın.

Güvenlik açığı için proje yayımlandığında deponun özel güvenlik bildirim kanalını kullanın. Kimlik bilgilerine ve konsol kayıtlarına raporda yer vermeyin.
