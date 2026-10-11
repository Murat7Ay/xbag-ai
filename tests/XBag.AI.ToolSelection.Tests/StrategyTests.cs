namespace XBag.AI.ToolSelection.Tests;

/// <summary>The strategies on their own: what a recall test in an app would look like.</summary>
public sealed class StrategyTests
{
    private readonly Catalog _catalog = new();

    private IReadOnlyList<AIFunctionDeclaration> Tools => _catalog.Tools.OfType<AIFunctionDeclaration>().ToArray();

    [Theory]
    [InlineData("İSTANBUL TRAFİĞİ", "istanbul_trafik")]
    [InlineData("istanbul trafigi", "istanbul_trafik")]
    [InlineData("ISTANBUL'DA KÖPRÜLER", "istanbul_trafik")]
    [InlineData("doviz", "kur_cevirici")]
    [InlineData("Döviz", "kur_cevirici")]
    [InlineData("nöbetçi eczane", "nobetci_eczane")]
    [InlineData("maaşım net ne olur?", "maas_hesaplama")]
    [InlineData("deprem oldu mu?", "son_depremler")]
    [InlineData("Fenerbahçe maçı kaç kaç?", "mac_skoru")]
    [InlineData("havası nasıl?", "hava_durumu")]
    public async Task Keywords_survive_Turkish_case_diacritics_and_suffixes(string query, string expected)
    {
        IReadOnlyList<ToolScore> ranked = await new KeywordToolSelectionStrategy().RankAsync(query, Tools);

        Assert.Equal(expected, ranked[0].Tool.Name);
    }

    [Fact]
    public async Task Keywords_prefer_the_rare_word()
    {
        // "şehir" is in three tools, "hava" in one: hava_durumu first, the other "şehir" tools after it.
        IReadOnlyList<ToolScore> ranked = await new KeywordToolSelectionStrategy().RankAsync("şehir hava", Tools);

        Assert.Equal(["hava_durumu", "namaz_vakitleri", "ucak_bileti"], ranked.Select(s => s.Tool.Name));
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    [Fact]
    public async Task Stop_words_do_not_drag_in_tools_whose_descriptions_merely_say_ve()
    {
        const string query = "İstanbul trafik ve hava durumu";      // "ve" is in four descriptions

        IReadOnlyList<ToolScore> filtered = await new KeywordToolSelectionStrategy().RankAsync(query, Tools);
        IReadOnlyList<ToolScore> raw = await new KeywordToolSelectionStrategy { StopWords = [] }.RankAsync(query, Tools);

        Assert.Equal(["istanbul_trafik", "hava_durumu"], filtered.Select(s => s.Tool.Name));
        Assert.Equal(5, raw.Count);                                  // + fatura_odeme, son_depremler, kullanici_bilgisi
    }

    [Fact]
    public async Task Keywords_return_nothing_for_a_question_that_shares_no_word()
    {
        Assert.Empty(await new KeywordToolSelectionStrategy().RankAsync("Yarın şemsiye almalı mıyım?", Tools));
        Assert.Empty(await new KeywordToolSelectionStrategy().RankAsync("?!", Tools));
    }

    [Fact]
    public async Task Keywords_split_camel_case_names_and_accept_a_custom_description()
    {
        AIFunction getWeather = AIFunctionFactory.Create(() => "sunny", "GetWeather");

        IReadOnlyList<ToolScore> byName = await new KeywordToolSelectionStrategy().RankAsync("weather in Izmir", [getWeather]);
        IReadOnlyList<ToolScore> custom = await new KeywordToolSelectionStrategy { DescribeTool = _ => "hava" }.RankAsync("hava", [getWeather]);

        Assert.Single(byName);
        Assert.Single(custom);
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeywordToolSelectionStrategy { PrefixLength = 2 });
    }

    [Fact]
    public async Task Embeddings_find_the_tool_that_shares_no_word_with_the_question()
    {
        var strategy = new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator());

        IReadOnlyList<ToolScore> ranked = await strategy.RankAsync("Yarın şemsiye almalı mıyım?", Tools);

        Assert.Equal("hava_durumu", Assert.Single(ranked).Tool.Name);
        Assert.Equal(1, ranked[0].Score, precision: 5);
    }

    [Fact]
    public async Task Tool_embeddings_are_computed_once_in_one_batch_and_queries_are_cached()
    {
        var generator = new ConceptEmbeddingGenerator();
        var strategy = new EmbeddingToolSelectionStrategy(generator);

        await strategy.RankAsync("Yarın şemsiye almalı mıyım?", Tools);
        await strategy.RankAsync("Yarın şemsiye almalı mıyım?", Tools);              // a tool loop asks the same question again
        await strategy.RankAsync("Paketim nerede?", Tools);
        AIFunction newTool = AIFunctionFactory.Create(() => "34 ABC 123", "plaka_sorgula", "Araç plakası sorgular.");
        await strategy.RankAsync("Paketim nerede?", [.. Tools, newTool]);

        Assert.Equal([12, 1, 1, 1], generator.Batches.Select(b => b.Length));
        Assert.Equal("plaka sorgula. Araç plakası sorgular.", generator.Batches[3][0]);
        Assert.StartsWith("hava durumu. Bir şehir için hava durumu tahmini verir", generator.Batches[0][0]);
        Assert.EndsWith(". sehir", generator.Batches[0][0]);
    }

    [Fact]
    public async Task An_all_zero_embedding_scores_0_not_NaN()
    {
        // "Merhaba!" hits no concept: an all-zero vector, for which TensorPrimitives.CosineSimilarity returns NaN.
        var byDefault = new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator());
        var everything = new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator()) { ScoreThreshold = -1 };

        Assert.Empty(await byDefault.RankAsync("Merhaba!", Tools));
        IReadOnlyList<ToolScore> all = await everything.RankAsync("Merhaba!", Tools);
        Assert.Equal(12, all.Count);
        Assert.All(all, s => Assert.Equal(0, s.Score));
    }

    [Fact]
    public async Task The_score_threshold_drops_weak_matches()
    {
        var loose = new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator());
        var strict = new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator()) { ScoreThreshold = 0.8f };
        const string query = "Yağmurda kargo gelir mi, bir de dolar ne durumda?";      // weather 1, money 1, cargo 1

        IReadOnlyList<ToolScore> all = await loose.RankAsync(query, Tools);
        IReadOnlyList<ToolScore> none = await strict.RankAsync(query, Tools);

        Assert.Equal(["hava_durumu", "kargo_takip", "kur_cevirici"], all.Select(s => s.Tool.Name).Order());
        Assert.All(all, s => Assert.Equal(1 / Math.Sqrt(3), s.Score, precision: 5));
        Assert.Empty(none);
    }

    [Fact]
    public async Task A_generator_that_returns_the_wrong_number_of_embeddings_is_reported()
    {
        var strategy = new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator { DropOne = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.RankAsync("hava", Tools).AsTask());
        Assert.Equal("The embedding generator returned 11 embeddings for 12 tool descriptions.", error.Message);
    }

    [Fact]
    public async Task Hybrid_ranking_combines_keywords_and_embeddings()
    {
        var hybrid = new HybridToolSelectionStrategy(
            new KeywordToolSelectionStrategy(),
            new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator()));

        // Keywords: the pharmacy, then the flight and cargo tools ("nerede" matches "nereden" and "nerede" in them).
        // Embeddings: the umbrella (weather) and the pharmacy. Found by both, the pharmacy wins; keyword-only false positives last.
        IReadOnlyList<ToolScore> ranked = await hybrid.RankAsync("Şemsiye alayım mı, nöbetçi eczane nerede?", Tools);

        Assert.Equal(["nobetci_eczane", "hava_durumu", "ucak_bileti", "kargo_takip"], ranked.Select(s => s.Tool.Name));
        Assert.Throws<ArgumentException>(() => new HybridToolSelectionStrategy());
    }

    [Fact]
    public async Task An_embedding_strategy_plugs_into_the_pipeline()
    {
        var model = new ScriptedChatClient().Reply("Evet, şemsiye al.");
        var embeddings = new EmbeddingToolSelectionStrategy(new ConceptEmbeddingGenerator());

        await model.WithToolLoop(o =>
            {
                o.Strategy = embeddings;
                o.MaxTools = 3;
            })
            .GetResponseAsync("Yarın şemsiye almalı mıyım?", new ChatOptions { Tools = _catalog.Tools });

        Assert.Equal(["hava_durumu"], model.Requests[0].ToolNames());
    }
}
