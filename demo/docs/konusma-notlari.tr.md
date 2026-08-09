# Konuşma notları — adım adım ne diyeceksin, nereyi göstereceksin

Kural: önce cümleyi kur, sonra komutu çalıştır, çıktı gelince tek şeyi işaret et. Çıktıyı
okumaya çalışma — seyirci zaten görüyor; sen sadece "bakın şuraya" de.

Sunum boyunca rakip motor adı anılmıyor; gerekirse "referans motor" de.

---

## Açılış (slayt 1–4)

Slayt 2'de dur, şunu anlat:

> "Entegrasyon ortamı diye bir derdimiz var. Gerçek servis ya paylaşımlı, ya kırık, ya da
> daha yazılmadı. Elle yazdığımız mock'lar da sadece mutlu senaryoya cevap veriyor —
> callback yok, mesaj yok, hata yok. Ama asıl tehlikeli olan üçüncüsü: gerçek API değişmiş,
> mock eski halinde kalmış. Build yeşil, prod kırık. Mockifyr'ın derdi bu üçünü birden çözmek."

Slayt 4'te haritayı 15 saniyede geç: "Tek hikâye üzerinden gideceğiz: Acme Payments diye
bir ödeme API'si. Sıfırdan sandbox kuracağız, her kanalı göreceğiz, en sonda da mock'un
hâlâ doğruyu söyleyip söylemediğini makineye soracağız."

---

## Act 1 — Spec'ten sandbox'a

**[Dashboard ana sayfa]** "Spin up a sandbox" kutusunu göster:

> "Boş bir tenant'tayım. Elimde sadece API'nin OpenAPI dokümanı var. Başka hiçbir şey yok."

**[Stubs → New stub → OpenAPI sekmesi]** — yaml'ı yapıştır, Stateful CRUD açık kalsın, Import:

> "Yapıştırıyorum, import diyorum... 5 operasyon, 5 stub. Ama dikkat, bunlar ezbere cevap
> veren stub'lar değil — path'ler resource şeklinde olduğu için create/read/update/delete
> gerçek bir doküman deposuna bağlandı. Yani bu sandbox'ın hafızası var."

**`./demo/demo.sh payments-create`**

> "Müşteri uygulaması gibi bir POST atıyorum... 201 döndü, Location header'ı verdi ve
> hemen arkasından o adresi GET'leyince kaydettiğim ödeme geri geldi. Yazdığımı okuyorum."

**`payments-get`** ve **`payments-list`** — hızlı geç: "Seed edilmiş veriler de burada,
az önce yarattığım da listede."

**[Resources ekranı]**

> "Aynı verinin yönetim yüzü burası. Koleksiyonlar, dokümanlar — sandbox'ı test öncesi
> istediğin veriyle doldurup sıfırlayabiliyorsun."

**[Access ekranı]** — Issue key de, adı `partner-portal`, quota 10:

> "Bu sandbox'ı bir partnere ya da başka bir takıma vereceksek kapıya kilit lazım.
> Key ürettim — ve şuna dikkat: token'ı şu an görüyorum, bir daha asla göremeyeceğim.
> Sistemde sadece tuzlanmış hash'i duruyor."

**`key-quota`**

> "Şimdi bu key ile istek atıyorum — tenant header'ı yok bakın, key'in kendisi hangi
> tenant olduğunu söylüyor. Her cevapta rate limit header'ları var; beşinci istekte
> remaining sıfıra düştü, altıncıda 429 ve Retry-After. Sessizce yutmuyor, dürüstçe
> 'kotanı bitirdin' diyor."

---

## Act 2 — Eşleşme kriterleri ve "neden eşleşmedi?"

**[Stubs → order stub'ını aç]** Form ve JSON sekmelerini göster:

> "Bu stub üç şart koşuyor: POST /api/orders olacak, X-Partner-Key doğru olacak,
> body'deki sku alanı WIDGET-1 olacak. Cevap da statik değil — isteğin içinden
> sku ve adedi alıp cevaba yazıyor."

**`order-ok`**

> "Şartlara uyan istek: 201, ve bakın sku ile qty benim gönderdiğim değerler."

**`order-bad`**

> "Şimdi yanlış key ile atıyorum: 404. Buraya kadar normal. Ama asıl soru şu —
> entegrasyon yapan geliştirici bu 404'ü görünce ne yapacak? Neden eşleşmedi?"

**`near-miss`**

> "Cevabı motora soruyoruz. Bak: urlPath tuttu, method tuttu, body tuttu —
> headers X-Partner-Key tutmadı, gelen değer de 'WRONG'. Alan adları mapping
> JSON'ının kendi kelimeleri; yani bu çıktıyı alıp kendi stub dosyanda
> arayabiliyorsun. Bir de şu önemli: bu teşhis admin tarafında; servis edilen
> 404'ün byte'ı değişmiyor."

**[Request journal]** — kısaca: "Bu tenant'a gelen her istek burada, eşleşen eşleşmeyen."

---

## Act 3 — Callback

**`webhook`**

> "Gerçek ödeme akışı senkron değil: authorize isteği atıyorum, bana hemen 202 dönüyor —
> ve yarım saniye sonra mock, benim sistemime callback atıyor. Komut ikisini de gösterdi:
> journal'da hem authorize isteği var hem de callback'in kendisi. Detaya bakınca
> delivered true, karşı tarafın 200 cevabı bile kayıtlı. Callback gelmedi mi tartışması
> burada bitiyor."

**[Journal detayında Callback sekmesini göster]**

Slayttan tek cümle: "Aynı mekanizmanın Kafka'ya event basan versiyonu da var —
stub cevap verirken topic'e mesaj yayınlıyor; gerçek broker'la test edilmiş durumda."

---

## Act 4 — Mesajlar

**`sms`**

> "Uygulamam SMS gönderiyor — endpoint'in şekline bakın, sağlayıcının gerçek API'siyle
> aynı. Yani resmi SDK'yı hiç değiştirmeden buraya yöneltebiliyorsun. Cevap da gerçekçi:
> sid verdi, queued dedi. Ama kimseye SMS gitmedi."

**[Messages ekranı]**

> "Tek gelen kutusu: e-posta da SMS de burada. Şu satıra dikkat — OTP rozetini kendisi
> çıkarmış."

**`otp`**

> "E2E testin en bilinen çilesi: 'SMS'teki kodu nereden alacağım?' Cevap: API'den.
> Kod 482913, testin tek yapması gereken bu endpoint'i çağırmak."

**`email`**

> "Mail tarafı da gerçek SMTP. İşin güzel tarafı şu: SMTP kullanıcı adı hangi tenant'a
> yazılacağını söylüyor. Uygulama gerçek mail atıyor, kutuya düşüyor, kimseye gitmiyor."

---

## Act 5 — HTTP'nin ötesi

**`grpc-descriptor`**

> "gRPC için tek gereken proto descriptor'ı. Upload ettim — serving true dedi, restart yok.
> Servisleri ve metodları da listeledi."

**`grpc`**

> "Gerçek bir gRPC istemcisiyle, HTTP/2 üstünden çağırıyorum. Tenant bilgisi metadata'da.
> Cevap stub'dan geldi. Yani REST mock'layan aynı motor, gRPC de konuşuyor."

**`graphql`** sonra hemen **`graphql-messy`**

> "GraphQL stub'ı query + değişkenler + operasyon adına bakıyor. Şimdi işin numarası:
> aynı sorguyu bozuyorum — boşlukları sildim, alanların sırasını değiştirdim...
> yine eşleşti. Çünkü metin olarak değil, sorgunun ağacı üzerinden karşılaştırıyor.
> Gerçek istemciler sorguyu her formatta gönderir; mock bundan etkilenmiyor."

**`ws`**

> "WebSocket: bağlanır bağlanmaz karşılama mesajı geldi — bunu sunucu kendi gönderdi.
> ping attım pong geldi, shout atınca da tenant'ın bütün bağlı soketlerine broadcast
> yaptı. Bunlar da stub gibi tanımlanıyor, message-mapping diye."

**[Stubs ağacına dön]**

> "Şuna bakın: aynı listede HTTP var, GraphQL var, gRPC var, WebSocket var — rozetlerinden
> ayırt ediyorsunuz. Tek motor, tek yönetim yüzü."

---

## Act 6 — Senaryolar

**`scenario`**

> "Aynı endpoint'e iki kez aynı isteği attım: ilkinde pending, ikincisinde settled.
> Çünkü stub'lar bir durum makinesine bağlı — ilk cevap durumu ilerletiyor."

**[Scenarios ekranı]** — pill'lere tıkla:

> "Durum ekrandan görülüyor ve yönetiliyor. Started'a tıklıyorum — akış başa sardı.
> Test senaryonu istediğin duruma kurup istediğin kadar tekrar oynatabiliyorsun."

---

## Act 7 — Kayıt ve drift (tenant: Globex Retail)

**[Tenant değiştir]**

> "Şimdi ikinci müşteri: Globex. Dikkat — stub listesi bomboş. Acme'nin hiçbir şeyi
> burada görünmüyor; izolasyon ayar değil, mimarinin kendisi."

**[Recordings ekranı]** — Target `http://localhost:9090`, Start:

> "Senaryo şu: Globex'in gerçek faturalama API'si var — burada 9090 portunda duran servis
> onu oynuyor. Mock'u elle yazmak yerine kaydedeceğiz. Start dedim; şu andan itibaren bu
> tenant'a gelen her istek gerçek API'ye gidiyor ve yanıtıyla birlikte kaydediliyor."

**`record-drive`**

> "İki çağrı attım — cevaplar gerçek API'den geldi, mock sadece aradan geçirdi."

**[Snapshot + Import all düğmeleri]**

> "Snapshot: kaydedilen trafik stub'a dönüştü. Import all: artık bu tenant'ın stub'ları
> bunlar. Gerçek API'yi kapatsak da Globex'in sandbox'ı aynı cevapları verir."

**`drift`**

> "Şimdi hayatın gerçeği: aylar geçti, gerçek API değişti. currency alanı kalktı,
> settlementBatch diye yeni bir alan geldi. Kimse bize haber vermedi tabii."

**`record-verify`**

> "Soru şu: benim stub'larım hâlâ gerçeği mi söylüyor? Soruyorum... İşte:
> upstream'de settlementBatch var, stub'da yok — fieldMissing. Stub currency dönüyor,
> gerçek artık dönmüyor — fieldUnexpected. Ve şuna dikkat: id'ler, timestamp'ler
> şikâyet listesinde yok; karşılaştırma yapısal, o yüzden gürültü üretmiyor."

**`record-stop`** — "Kaydı kapatıyorum, stub'lar cevap vermeye devam ediyor."
Tenant'ı Acme Payments'a geri al.

---

## Act 8 — Zaman, kaos, sözleşme

**`token`** → **`clock-freeze`** → **`token`**

> "Bu endpoint bir saat geçerli token üretiyor. Süre dolunca ne olacağını test etmek
> için normalde ne yapıyoruz? Bekliyoruz, ya da sistem saatiyle oynuyoruz. Burada
> tenant'ın saatini donduruyorum — 2027'ye. Aynı isteği atıyorum: token artık 2027'de
> üretilmiş görünüyor. Sadece bu tenant için; journal ve audit gerçek zamanda kalıyor."

**`clock-reset`** — sessizce çalıştır.

**`chaos-on`** → **`chaos-probe`** → **`chaos-off`**

> "Peki bağımlılık toptan kötüleştiğinde uygulaman ne yapıyor? Profil tanımlıyorum:
> 300 milisaniye gecikme artı yüzde 40 oranında 503. Beş istek atıyorum — karışık
> 200'ler ve 503'ler, hepsi yavaşladı. Kritik detay: seed 42. Aynı seed'le aynı sıra
> gelir; yani bu kaos koşusu bir regresyon testine dönüşür. Ve admin API'si hiç
> bozulmaz — kaosu her zaman kapatabilirsin."

**`verify-stubs`**

> "Kapanışa geliyorum. Sözleşmeye soruyorum: stub'larım kontratı karşılıyor mu?
> 5 operasyonun 5'i karşılanmış. 8 tane de kontratta olmayan stub var — bilerek:
> senaryolar, webhook, gRPC... Rapor dürüst; ne eksik ne fazla, söylüyor."

**`verify-traffic`**

> "Ve madalyonun öbür yüzü — istemciler kontratın içinde mi kaldı? Mock her şeye cevap
> verdiği için normalde bu hatayı asla görmezsin; istemci yanlış çağrı yapar, mock yine
> döner, herkes mutlu, prod'da patlar. Burada journal'daki gerçek trafiği spec'le
> karşılaştırıyor ve kontrat dışı çağrıları tek tek sayıyor."

---

## Kapanış (slayt 13–15)

> "Peki bütün bunların doğru davrandığına neden inanalım? Şöyle: bu motorun bire bir
> davranış iddiaları, Docker'da çalışan referans motora karşı diferansiyel testle
> kanıtlanıyor — dört suite'te 1122 yeşil test. Referansın olmadığı yerlerde gerçek
> istemciler kullanılıyor: gerçek mail kütüphanesi, resmi SMS SDK'sı, resmi Kafka
> client'ı. Performans da iddia değil ölçüm: bin stub arasından eşleşme 392 nanosaniye."

Son slayt:

> "Toparlarsam: spec'ten bir dakikada sandbox, entegrasyonun dokunduğu her kanal,
> ve mock'un yalan söylemeye başladığı anı haber veren üç doğrulama. Sorularınızı
> alayım — isterseniz sandbox'ın adresini ve bir key verip kendi makinenizden
> kurcalamanıza da açayım."

---

## Genel sahne notları

- Her komuttan sonra 2 saniye sus. Çıktının okunmasına izin ver.
- Bir şey ters giderse panik yok: aynı adımı tekrar çalıştır; hâlâ tersse
  "bunu birazdan döneceğim" de, DEMO.md'deki kurtarma bölümüne göre devam et.
- Terminal fontunu büyüt, dashboard'da zoom %110–125 iyi çalışıyor.
- Soru gelirse kısa cevap ver, "kapanışta buna döneceğim" demekten çekinme —
  akışı bölme.
