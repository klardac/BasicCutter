# Basic Cutter

Unity 6 için level design aracı. Pencere ve kapıların duvarın içinde kalan kısmını keser ve deliğin iç yüzeylerini (pervazları) duvarın materyaliyle kapatır. Kesimler geri alınabilir: orijinal mesh her zaman saklanır.

- Built-in, URP ve HDRP ile çalışır (sadece mesh üretir, shader veya pipeline kodu yoktur).
- Sadece editörde çalışır. Oyunun build'ine hiçbir kod veya maliyet eklemez.
- Prefab'larla, prefab instance'larıyla ve normal sahne objeleriyle çalışır.

## Kurulum

1. `BasicCutter.unitypackage` dosyasını projeye sürükleyin veya **Assets > Import Package > Custom Package** ile içe aktarın.
2. Ek paket gerekmez.

## Hızlı başlangıç

1. **Tools > Basic Cutter > Create Demo Scene** ile örnek sahneyi açın ve pencereleri sürükleyin.
2. Kendi pencereniz için pencerenin kök objesini (tercihen prefab'ını) seçin ve **Add Component > Basic Cutter > Mesh Cutter** ekleyin (veya **Tools > Basic Cutter > Add Cutter To Selection**).
3. Pencereyi duvarın içine yerleştirin. Duvar anında kesilir. **Snap To Wall** (Alt+Shift+W) pencereyi duvara oturtur ve duvar kalınlığında ortalar.

Duvara bir şey eklemeniz gerekmez. Kesilen her duvara otomatik olarak `Cuttable Mesh` bileşeni eklenir. Pencere uzaklaştırılır, silinir veya devre dışı bırakılırsa duvar orijinal haline döner ve bileşen kaldırılır.

## Mesh Cutter (pencere / kapı / niş)

Inspector yukarıdan aşağıya şu bölümlerden oluşur.

**Durum kartı:** Kesicinin ne yaptığını tek satırda gösterir.
- Renkli nokta: yeşil kesiyor, sarı sorunlu, kırmızı delik oluşturulamıyor, gri hiçbir şey kesmiyor, mavi arka planda kesiliyor.
- Altında kesim ekseni, hat nokta sayısı, nişin açıldığı yüz ve kapının alt kenarının kayması yazar.
- Kesilen duvarların adları butondur; tıklayınca o duvar seçilir.
- Sorunların yanında tek tıkla düzeltme butonları vardır: **Enable Read/Write** (okunamayan model), **Select** (sorunlu duvar), **Snap To Wall** (pencere hiçbir duvarın içinde değil), **Settings** (canlı güncelleme kapalı).

**Type:** `Window`, `Door`, `Niche` veya `Skylight`. Seçilen türe göre doğru ayarlar kendiliğinden yapılır ve ilgisiz alanlar gizlenir. Skylight, kesim eksenini Y yapar (çatı penceresi, zemin açıklığı).

**Cut Details:**

| Ayar | Açıklama |
|---|---|
| **Shape** | `Exact` (varsayılan): alt objelerdeki tüm mesh'lerin kesim yönünden görünen dış hattı, birebir. `Box` / `Arch` / `Circle`: parametrik şekil (Size, Center, Arch Rise ve **Fit To Window Meshes** butonu görünür). `Custom Mesh`: kapalı bir mesh'i doğrudan kesici hacim olarak kullanır. |
| **Padding** | Deliği büyütür (+) veya küçültür (−), santimetre. -1 cm çerçevenin duvarın üstüne binmesini sağlar, ışık sızmaz. |

**Door** (sadece Door türünde): **Bottom Snap**, alt kenarın zemine veya duvar altına oturmak için en fazla ne kadar kayabileceği (varsayılan 5 cm).

**Niche** (sadece Niche türünde): **Depth** nişin derinliği; **Open Other Face** nişi diğer yüzde açar.

**Surfaces:**

| Ayar | Açıklama |
|---|---|
| **Jamb Material** | Deliğin iç yüzeylerinin materyali. Boşsa duvarın kendi materyali kullanılır. |
| **Trim** | Deliğin çevresine duvar yüzeyinde bir çerçeve (söve) üretir. |
| **Sides / Width / Thickness / Material** | Çerçevenin hangi yüzlerde olacağı (nişte gizli: sadece açık yüz), genişliği, duvardan ne kadar taşacağı ve materyali. |
| **Sill (Depth / Overhang)** | Pencere ve nişlerde denizlik. Depth: çerçeveden ne kadar daha fazla taşacağı. Overhang: yanlardan ne kadar taşacağı. |

**Advanced** (başta kapalı):

| Ayar | Açıklama |
|---|---|
| **Axis** | Duvarın içinden geçen yerel eksen. `Auto`, pencere mesh'lerinin en ince eksenini seçer. |
| **Segments** | Arch ve Circle şekillerinde eğrinin parça sayısı. |
| **Depth Margin** | Kesimin duvarın iki yüzünden ne kadar taşacağı. |
| **Target Layers** | Sadece bu layer'lardaki objeler kesilir. |

Uzunluklar santimetre olarak girilir ve metre olarak saklanır (1 birim = 1 m varsayılır). Bölümlerin açık/kapalı durumu hatırlanır.

Exact şeklinde pencerenin modelinin kapalı olması gerekmez. Cam düzlemi, açık yüzeyler ve iç içe geçmiş parçalar sorun çıkarmaz. Yalnızca dış hat kullanılır ve içteki boşluklar doldurulur.

Butonlar:

- **Snap To Wall:** Pencereyi en yakın duvara taşır. **Align Rotation** açıkken kavisli duvarlarda pencereyi yüzeye göre de döndürür.
- **Rebuild:** Kesimi yeniden hesaplar.
- **Bake Walls / Unbake Walls:** Kestiği duvarları dondurur veya canlı güncellemeye geri açar.

## Kapılar

Kapılar normal Mesh Cutter ile de kesilir. Kapının altı duvarın altıyla aynı hizadaysa delik duvarın altına kadar açılır. Kapının altındaki zemin ayrı bir objeyse asla kesilmez, çünkü kesici sadece kapı yönünde içinden geçtiği objeleri keser.

Kapı prefab'larında **Type** olarak **Door** seçin. Bu modda:

- **Padding** alt kenara uygulanmaz, sadece yanlara ve üste uygulanır. Böylece negatif Padding kapıda eşik oluşturmaz.
- **Kapı modeli duvarın altından biraz yukarıdaysa** (Bottom Snap mesafesi içinde), delik duvarın altına kadar uzatılır ve eşik şeridi kalmaz.
- **Duvar ile zemin tek bir mesh ise** ve kapı modeli zemin yüzeyine göre biraz aşağıda veya yukarıdaysa, deliğin altı zemin yüzeyine oturtulur. Zeminde oluk açılmaz.
- Duvar zeminin içine Bottom Snap'ten daha derin gömülüyse veya kapı bilerek yukarıda duruyorsa (örneğin yükseltilmiş eşik), alt kenara dokunulmaz.

Inspector'da kapının alt kenarının ne kadar kaydırıldığı gösterilir. Kapı modelinin yerel yukarı ekseni (Y) yukarıya bakmalıdır.

## Nişler

**Type = Niche** seçildiğinde kesici duvarı delip geçmez, tek yüzde **Depth** kadar derin bir oyuk açar. Duvar nişleri, raf boşlukları ve şömine yuvaları için kullanılır.

- Niş, pencere modelinin bulunduğu (en yakın) yüzde açılır. Model duvarın tam ortasındaysa veya hiç model yoksa (Box/Arch/Circle şekli) kesicinin negatif eksen tarafındaki yüz seçilir. **Open Other Face** ile diğer yüze çevrilir.
- Nişin arka yüzü ve iç yüzleri duvarın materyalini (veya Jamb Material'ı) alır.
- Niş derinliği duvardan kalınsa kesim normal delik olur ve inspector'da bilgi gösterilir.

## Birleşen açıklıklar

Aynı duvarda üst üste binen kesiciler artık tek bir açıklık olarak birleşir. Örneğin kapı ile yanındaki dar pencere (yan ışık) tek bir açıklık olur. Basit kesicileri üst üste koyarak L şeklinde veya karmaşık açıklıklar kurabilirsin. Inspector'da hangi kesicilerle birleşildiği bilgi olarak gösterilir.

## Pervaz materyali, çerçeve ve denizlik

- **Jamb Material:** Deliğin iç yüzeylerine ayrı bir materyal verir. Örneğin dışı tuğla, içi sıva veya ahşap kasa. Duvarın Mesh Renderer'ına sona bir materyal slotu eklenir. Kesim kaldırılınca bu slot da kaldırılır.
- **Trim:** Deliğin çevresine, duvarın yüzeyine oturan bir çerçeve üretir. Her köşesi duvara ışınla yansıtıldığı için kavisli duvarlarda da yüzeyi takip eder. Kapı modunda alt kenarda çerçeve olmaz ve uçlar zemine dik biter. Nişlerde sadece açık yüzde oluşur.
- **Sill:** Pencerenin altına, çerçeveden daha fazla taşan ve yanlardan daha geniş bir denizlik ekler. Kapılarda kullanılmaz.
- Çerçeve, pencerenin altında **"Trim (Basic Cutter)"** adlı bir alt obje olarak oluşturulur ve otomatik güncellenir. Elle yapılan değişiklikler kalıcı olmaz. Pencerenin dış hattına (Exact şekline) dahil edilmez.

## Cuttable Mesh (duvar)

| Mod | Davranış |
|---|---|
| **Live** | Pencereler değiştikçe otomatik yeniden kesilir. |
| **Frozen** | Bake edilmiş hali: mevcut kesim korunur, değişiklikler uygulanmaz. |
| **Excluded** | Orijinal mesh gösterilir ve kesiciler bu objeyi yok sayar. |

Butonlar: **Rebuild**, **Bake / Unbake**, **Revert To Original / Include Again**.

## Menü: Tools > Basic Cutter

- **Rebuild All:** Açık sahnedeki tüm kesimleri yeniden hesaplar.
- **Bake All In Scene / Unbake All In Scene:** Tüm duvarları dondurur veya canlıya alır.
- **Strip Components (Final)...:** Kesik mesh'leri bırakıp tüm Basic Cutter bileşenlerini kaldırır. Ctrl+Z ile geri alınabilir.
- **Clean Up Unused Generated Meshes...:** Hiçbir sahne veya prefab tarafından kullanılmayan üretilmiş mesh'leri siler. Önce sahnelerinizi kaydedin.
- **Settings...:** Project Settings > Basic Cutter sayfasını açar.

## Ayarlar (Project Settings > Basic Cutter)

- **Mesh Storage:**
  - `Embedded` (varsayılan): Kesik mesh'ler ve çerçeveler, onları kullanan sahnenin içinde saklanır. Prefab modunda ise prefab dosyasının içinde saklanır. Ekstra dosya oluşmaz; sahne başka projeye taşınırken bir şey unutulmaz. Sahnedeki objelerden yeni prefab yaptığında mesh'ler otomatik olarak prefab dosyasının içine yeniden üretilir.
  - `Asset Folder`: Her mesh, **Generated Mesh Folder** içinde ayrı bir `.asset` dosyası olarak saklanır. Her sahne ve prefab için bir alt klasör oluşur. Bu klasörü versiyon kontrolüne ekleyin.
  - Ayar değiştirildiğinde mevcut mesh'ler bir sonraki kesimde yeni yönteme geçer. Asset Folder'dan Embedded'a geçtikten sonra **Clean Up Unused Generated Meshes** ile eski dosyaları silebilirsin.
- **Live Update / Interval:** Sürüklerken canlı kesim ve en kısa güncelleme aralığı.
- **Background Cutting:** Canlı güncellemede büyük duvarlar (1500 üçgen ve üzeri) arka planda, ayrı bir thread'de kesilir. Editör sürüklerken takılmaz; sonuç birkaç kare sonra görünür ve beklerken inspector'da "Cutting in the background..." yazar. Sahne kaydedilirken bekleyen kesimler tamamlanır.
- **Lightmap UVs:** `Auto` modunda, orijinal mesh'te UV2 varsa veya obje GI için statikse lightmap UV'leri yeniden üretilir. Düzenleme durduktan kısa süre sonra çalışır.
- **Gizmo:** Kesim hattının sahnede çizimi.

## Nasıl çalışır

1. Kesici hacim, pencerenin dış hattından (Exact) veya seçilen şekilden bir prizma olarak üretilir. Prizmanın derinliği, duvara kesim ekseni boyunca atılan ışınlarla duvarın gerçek kalınlığına göre belirlenir. Bu yüzden kavisli ve eğri duvarlarda da doğru çalışır ve kapının altındaki zemin gibi sadece temas eden objeleri kesmez.
2. Duvardan BSP tabanlı boolean çıkarma yapılır (double hassasiyet). Sadece kesicinin çevresindeki üçgenler bu işleme girer; duvarın geri kalanı olduğu gibi kopyalanır. Bu yüzden yüksek poligonlu duvarlarda hızlıdır ve kesimden etkilenmeyen bölgelerin topolojisi hiç değişmez. Sonuç bir hacim kontrolünden geçer; şüpheli bir durumda tüm duvar üzerinden yeniden hesaplanır. Parçalanan yüzeyler orijinal üçgenlerine göre birleştirilip temiz şekilde yeniden üçgenlenir. T-junction'lar onarılır, mesh bit düzeyinde kapalı kalır.
3. Duvar yüzeyleri orijinal normal, UV, renk ve materyallerini aynen korur. Deliğin iç yüzeyleri duvarın materyalini alır. UV'leri, duvarın aynı yöne bakan yüzeyinin eşlemesinden devam ettirilir (doku yoğunluğu ve yönü korunur).

## İpuçları ve sınırlamalar

- **Duvarlar kapalı (watertight) mesh olmalı.** Açık mesh'ler kesilmez ve pencere inspector'ında uyarı gösterilir.
- Import edilen modellerde mesh okunamıyorsa model import ayarlarından **Read/Write**'ı açın.
- **Gölgelerde ince ışık çizgileri:** Ayrı mesh'ler tam yüzey yüzeye temas ettiğinde (çerçeve ile delik kenarı, köşede birleşen iki duvar) gölge bias'ı yüzünden ince ışık sızıntısı görülebilir. Bu kesimden değil, gölge haritasından kaynaklanır; kesik mesh'ler bit düzeyinde kapalıdır. Çözümler:
  - Pencerelerde **Padding**'e küçük bir negatif değer verin (demo `-0.01` kullanır), böylece çerçeve duvarın üstüne biner. Kapılarda bunu **Door** modu açıkken yapın, yoksa altta eşik kalır.
  - Köşelerde duvarları birbirinin içine taşacak şekilde yerleştirin.
  - Gerekirse duvarın Mesh Renderer'ında **Cast Shadows = Two Sided** ayarını kullanın.
- Pencere köşeye (iki duvarın birleştiği yere) konulmamalı.
- **Asset Folder** modunda sahneler ve prefab'lar başka projelere taşınırken `Generated Mesh Folder` içeriği de taşınmalıdır. **Embedded** modunda buna gerek yoktur.
