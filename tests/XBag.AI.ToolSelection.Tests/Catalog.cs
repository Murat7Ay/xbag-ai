namespace XBag.AI.ToolSelection.Tests;

/// <summary>Twelve everyday Turkish tools; every invocation is recorded in <see cref="Calls"/>.</summary>
internal sealed class Catalog
{
    public List<string> Calls { get; } = [];

    public AITool[] Tools => _tools ??=
    [
        Tool("hava_durumu", "Bir şehir için hava durumu tahmini verir: sıcaklık, yağmur, rüzgar.",
            (string sehir) => $"{sehir}: 18°C, öğleden sonra yağmur."),
        Tool("kur_cevirici", "Döviz kuru ile para çevirir: dolar, euro, Türk lirası.",
            (double miktar, string kaynak, string hedef) => $"{miktar} {kaynak} = {miktar * 41.2:0.##} {hedef}"),
        Tool("istanbul_trafik", "İstanbul trafik yoğunluğunu verir: köprüler ve otoyollar.",
            (string guzergah) => $"{guzergah}: yoğun, 45 dakika."),
        Tool("kargo_takip", "Kargo gönderisinin nerede olduğunu takip numarasıyla bulur.",
            (string takip_no) => "Ankara aktarma merkezinde."),
        Tool("namaz_vakitleri", "Bir şehir için günün namaz vakitlerini verir.",
            (string sehir) => "Öğle 13:02, ikindi 16:21."),
        Tool("nobetci_eczane", "Bir ilçedeki nöbetçi eczaneleri listeler.",
            (string ilce) => "Kadıköy: Moda Eczanesi."),
        Tool("ucak_bileti", "Şehirler arası uçak bileti arar.",
            (string nereden, string nereye, string tarih) => "SAW-ESB 07:30, 1.250 TL."),
        Tool("fatura_odeme", "Elektrik, su ve doğalgaz faturasını öder.",
            (string abone_no) => "Ödendi."),
        Tool("maas_hesaplama", "Brüt maaştan net maaşı hesaplar.",
            (double brut) => $"Net: {brut * 0.71:0}"),
        Tool("son_depremler", "Son depremleri büyüklük ve yer bilgisiyle listeler.",
            (double min_buyukluk) => "Marmara Denizi 4.1"),
        Tool("mac_skoru", "Süper Lig maç skorlarını getirir.",
            (string takim) => "2-1"),
        Tool("kullanici_bilgisi", "Giriş yapmış kullanıcının adını ve şehrini verir.",
            () => "Murat, İstanbul"),
    ];

    private AITool[]? _tools;

    public AIFunction this[string name] => Tools.OfType<AIFunction>().Single(t => t.Name == name);

    private AIFunction Tool(string name, string description, Delegate body) =>
        AIFunctionFactory.Create(body, new AIFunctionFactoryOptions { Name = name, Description = description })
            .Recorded(Calls);
}

internal static class CatalogExtensions
{
    /// <summary>Wraps a function so each invocation adds its name to <paramref name="calls"/>.</summary>
    public static AIFunction Recorded(this AIFunction function, List<string> calls) => new RecordingFunction(function, calls);

    private sealed class RecordingFunction(AIFunction inner, List<string> calls) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            lock (calls)
            {
                calls.Add(Name);
            }

            return base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
